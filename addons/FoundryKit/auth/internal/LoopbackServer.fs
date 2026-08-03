namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.core

## Accepts exactly one OAuth redirect on a loopback port and hands back its query.
##
## The desktop installed-app flow (RFC 8252) sends the authorization server's answer to a
## URL the app itself serves. This class is that server: it binds an ephemeral port on
## [constant BIND_ADDRESS], waits for the browser to arrive, parses the query string of the
## first request line, answers with a page the player can read, and closes.
##
## [b]It binds loopback only, and that is a security property rather than a default.[/b]
## A listener on [code]0.0.0.0[/code] would let anyone on the same network deliver a forged
## callback carrying an attacker's authorization code. RFC 8252 §8.3 requires the loopback
## interface for exactly this reason.
##
## [b]One listener serves one wait.[/b] [method await_callback] may be called once; a
## second call is refused rather than allowed to join or displace the first, and the
## listening socket is released the moment the wait settles, whichever way it ended. A
## listener that kept its port would hold it for the lifetime of the process.
##
## [b]Lifetime, single-settle and deferred resumption mirror [NativeRequest] and
## [HttpClient][/b], which solved the identical problems first — see [member _in_flight]
## and [method _resolve]. This is not defensive decoration: #107 records that awaiting a
## coroutine which suspended and has already completed does not fail in this engine, it
## hangs forever, so the naive version of this class presents as an infinite loop rather
## than as a bug.
##
## [b]Known limitation.[/b] The first request to arrive settles the wait, so a browser that
## opens a speculative connection and sends an unrelated request (a favicon fetch, say)
## before the redirect would settle it with that request's query. No browser observed to
## date does this ahead of the navigation it was told to make, and the alternative —
## ignoring requests whose query is empty — would silently swallow a genuine malformed
## callback that the caller needs to see reported.
class_name LoopbackServer extends RefCounted

## The only address this server ever binds. See the class documentation.
const BIND_ADDRESS: String = "127.0.0.1"

## Bounds the whole wait when a caller does not supply its own timeout. Matches the legacy
## desktop backend's listen window: long enough for a player to read a consent screen.
const DEFAULT_TIMEOUT_SECONDS: float = 120.0

## The largest request line accepted, in bytes.
##
## Anything connecting to this port can withhold the newline that ends the request line
## forever, so without a cap a peer could grow this process's memory until it died. Real
## callbacks are a few hundred bytes; an authorization code and a state token together do
## not approach eight kibibytes.
const MAX_REQUEST_BYTES: int = 8 * 1024

## What the browser shows once the callback has been delivered.
const _SUCCESS_PAGE: String = """<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Signed in</title></head>
<body style="font-family: system-ui, sans-serif; text-align: center; padding: 4rem;">
<h1>You are signed in</h1>
<p>You can close this tab and return to the game.</p>
</body>
</html>
"""

## What the browser shows when the callback reports an error rather than a code.
##
## Deliberately vague about which error it was: the query is the caller's to interpret, and
## a page in a browser is not where a player finds out why. It must simply not claim a
## success that did not happen.
const _REFUSED_PAGE: String = """<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Sign-in was not completed</title></head>
<body style="font-family: system-ui, sans-serif; text-align: center; padding: 4rem;">
<h1>Sign-in was not completed</h1>
<p>You can close this tab and return to the game.</p>
</body>
</html>
"""

## What the browser shows when the request could not be read at all.
const _UNREADABLE_PAGE: String = """<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Sign-in failed</title></head>
<body style="font-family: system-ui, sans-serif; text-align: center; padding: 4rem;">
<h1>That request could not be read</h1>
<p>You can close this tab and try signing in again from the game.</p>
</body>
</html>
"""

## Keeps every listener with a suspended wait alive independent of its caller.
##
## A caller typically discards its local variable once it holds the returned [Coroutine] —
## the coroutine's suspended state keeps only a raw pointer to its owner, not a strong
## reference. Without this registry the listener would be freed mid-wait, taking its poll
## loop and its watchdog with it, and the wait would never settle. The entry is removed
## when [method await_callback] resumes.
static var _in_flight: Array[LoopbackServer] = []

signal _settled(outcome: LoopbackOutcome)

var _log: FoundryKitLog
var _server: TCPServer? = null
var _connection: StreamPeerTCP? = null
var _request: String = ""
var _received_bytes: int = 0
var _has_awaited: bool = false
var _has_settled: bool = false
var _settle_count: int = 0
var _started_ticks_ms: int = 0

func _init(log: FoundryKitLog) -> void:
	_log = log

## Binds an ephemeral loopback port. Returns whether the listener is up.
##
## Idempotent: a second call on a listening server keeps the port it already has, so a
## caller cannot invalidate a redirect URI it has already published to the browser.
func start() -> bool:
	if _server != null:
		return true
	var server: TCPServer = TCPServer.new()
	# Port 0 asks the operating system for a free one. The redirect URI is therefore not
	# known until after this call, which is why the flow binds before it builds the URL.
	var error: Error = server.listen(0, BIND_ADDRESS)
	if error != OK:
		_log.warn("the loopback listener could not bind %s (%d)" % [BIND_ADDRESS, error])
		return false
	_server = server
	_log.debug("loopback listener bound to %s:%d" % [BIND_ADDRESS, server.get_local_port()])
	return true

## Returns the bound port, or 0 when the listener is not up.
func port() -> int:
	var server: TCPServer? = _server
	if server == null:
		return 0
	var listening: TCPServer = server
	return listening.get_local_port()

## Returns the address the listener binds, so a caller — or a test — can assert it.
func bind_address() -> String:
	return BIND_ADDRESS

## Returns the redirect URI to hand the authorization server, or an empty string when the
## listener is not up.
func redirect_uri() -> String:
	var bound_port: int = port()
	if bound_port == 0:
		return ""
	return "http://%s:%d/" % [BIND_ADDRESS, bound_port]

## Counts the times this listener's wait has settled. Always 0 or 1.
##
## Public because "settles exactly once" is otherwise unobservable, and a watchdog that
## fires over the top of a callback that already arrived leaves every other assertion
## green.
func settle_count() -> int:
	return _settle_count

## Waits for the first request and returns what it carried.
##
## Callable once per listener. A second call returns [code]Failed[/code] immediately
## without suspending, rather than joining the first wait — two callers sharing one
## callback is never what either of them meant.
##
## Settles as [code]TimedOut[/code] if no request arrives within [param timeout_seconds],
## and as [code]Failed[/code] if the listener was never started, has no [SceneTree] to poll
## from, or is stopped while waiting. It never returns without settling.
async func await_callback(
		timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS) -> LoopbackOutcome:
	if _has_awaited:
		# Returned without suspending on purpose. Awaiting [signal _settled] here would hand
		# this caller the first wait's result, and settling it would take that result away
		# from the caller that asked for it. A coroutine that never suspends is safe to
		# await at any later point (#107); one that suspended and completed is not.
		return LoopbackOutcome.Failed("this listener has already been awaited")
	_has_awaited = true
	_in_flight.append(self)

	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree == null:
		# There is nowhere to poll the socket from and no clock to bound the wait. Answer
		# rather than hang; a headless host must get something it can act on.
		_resolve(LoopbackOutcome.Failed("no scene tree is available"))
	elif _server == null:
		_resolve(LoopbackOutcome.Failed("the loopback server is not listening"))
	else:
		_started_ticks_ms = Time.get_ticks_msec()
		# The watchdog bounds a player reading a consent screen in their browser, not game
		# simulation — it must keep running while the tree is paused or its time scale is
		# zero, which is what the trailing arguments ask for.
		var timer: SceneTreeTimer = tree.create_timer(timeout_seconds, true, false, true)
		timer.timeout.connect(_on_timeout)
		_pump(tree)

	var outcome: LoopbackOutcome = await _settled
	_in_flight.erase(self)
	return outcome

## Releases the port and, if a wait is outstanding, settles it.
##
## Idempotent, and safe to call after the wait has already settled — which is what lets the
## caller stop the listener unconditionally on every exit path without first working out
## which one it is on.
func stop() -> void:
	_release_sockets()
	if _has_awaited and not _has_settled:
		_resolve(LoopbackOutcome.Failed("the loopback listener was stopped"))

## Polls the socket once per idle frame until the wait settles.
##
## Started without being awaited: the caller awaits [signal _settled] instead, so there is
## exactly one place a wait is resumed from. Awaiting this directly would be the #107
## hazard in its purest form.
##
## Returns [code]void[/code] rather than an outcome for the same reason — nothing may await
## it.
async func _pump(tree: SceneTree) -> void:
	while not _has_settled:
		await tree.process_frame
		if _has_settled:
			return
		_poll_once()

func _poll_once() -> void:
	if not _accept_connection():
		return
	var current: StreamPeerTCP? = _connection
	if current == null:
		return
	var connection: StreamPeerTCP = current
	connection.poll()

	var available: int = connection.get_available_bytes()
	if available > 0:
		# Counted here rather than derived from the buffer afterwards: the buffer holds
		# decoded characters, and a character is up to four bytes, so its length would
		# understate what the peer actually made this process hold.
		_received_bytes += available
		_request += connection.get_utf8_string(available)

	var newline: int = _request.find("\n")
	if newline < 0:
		if _received_bytes > MAX_REQUEST_BYTES:
			# Nothing terminating the line yet and already past the cap, so no amount of
			# further reading can produce an acceptable one.
			_refuse_oversized(connection)
			return
		if connection.get_status() != StreamPeerTCP.STATUS_CONNECTED:
			# The peer went away before finishing its request line. Reporting it beats
			# waiting out the full watchdog window for a browser that is no longer there.
			_resolve(LoopbackOutcome.Failed(
					"the peer closed the connection before sending a request"))
		return

	# The cap is checked against the completed line, and only once the line is known to be
	# complete: a single read can deliver a whole oversized line, terminator and all, so
	# testing the cap only while the line is still unterminated would let exactly that case
	# through. Measuring the line rather than everything read is equally deliberate —
	# everything after the first newline is headers, and a browser sending a few kibibytes of
	# cookies must not be mistaken for an abusive peer.
	#
	# Measured in bytes, before trimming. A peer that pads a line with a mebibyte of spaces,
	# or spells it in four-byte characters, has made this process hold that much whatever the
	# line collapses to or however few characters it turns out to be.
	var raw_line: String = _request.substr(0, newline)
	if raw_line.to_utf8_buffer().size() > MAX_REQUEST_BYTES:
		_refuse_oversized(connection)
		return
	_complete(connection, raw_line.strip_edges())

func _refuse_oversized(connection: StreamPeerTCP) -> void:
	_log.warn("a loopback peer sent an oversized request line")
	_write_response(connection, "400 Bad Request", _UNREADABLE_PAGE)
	_resolve(LoopbackOutcome.Failed(
			"the request line exceeded %d bytes" % MAX_REQUEST_BYTES))

## Takes the pending connection, if there is not one already. Returns whether there is a
## connection to read from.
func _accept_connection() -> bool:
	if _connection != null:
		return true
	var server: TCPServer? = _server
	if server == null:
		return false
	var listening: TCPServer = server
	if not listening.is_connection_available():
		return false
	var peer: StreamPeerTCP? = listening.take_connection()
	if peer == null:
		return false
	_connection = peer
	return true

## Answers the browser and settles the wait with the query the request carried.
##
## The reply is written before the wait is resolved because [method _resolve] closes the
## socket. Without it the player sees a browser error page on a sign-in that in fact
## succeeded, which is the most confusing failure this class can produce.
##
## Only a callback that carries an authorization code and no [code]error[/code] is answered
## with a page claiming success. That is the one piece of OAuth vocabulary this class knows,
## and it is unavoidable: it is the only participant with a page in front of the player, so
## telling someone who has just pressed Cancel — or whose callback arrived malformed — that
## they are signed in would be a plain lie they then act on. What the error [i]means[/i]
## stays the caller's to decide, and the outcome it receives is an ordinary
## [code]Received[/code] either way (RFC 6749 §4.1.2).
##
## [b]The page is written before [code]state[/code] has been verified,[/b] because only the
## caller knows the value to compare against. A forged callback carrying a plausible code is
## therefore shown a success page while sign-in fails. Moving the reply behind that check
## would mean holding the socket open past the settlement, which is the lifetime the
## single-settle contract above exists to rule out; giving this class the expected
## [code]state[/code] would move the check out of the backend, where it can be proved not to
## exchange the code. Both are worse trades than one misleading page in an attack that has
## already failed.
func _complete(connection: StreamPeerTCP, request_line: String) -> void:
	var query: Dictionary[String, String] = _query_of(request_line)
	var code: String = ""
	if query.has("code"):
		code = query["code"]
	if query.has("error") or code.is_empty():
		_write_response(connection, "200 OK", _REFUSED_PAGE)
	else:
		_write_response(connection, "200 OK", _SUCCESS_PAGE)
	_resolve(LoopbackOutcome.Received(query))

## Parses the query string of a request line such as
## [code]GET /?code=abc&state=xyz HTTP/1.1[/code].
##
## Keys and values are URI-decoded, and a [code]+[/code] is read as a space:
## authorization servers form-encode query values, so a state token containing one would
## otherwise arrive corrupted and fail the comparison it exists for.
##
## A request with no query, or one this does not understand, yields an empty dictionary.
## Reporting that as a listener failure would be wrong — the request did arrive, and what a
## callback without a [code]code[/code] means is the caller's decision.
func _query_of(request_line: String) -> Dictionary[String, String]:
	var query: Dictionary[String, String] = {}
	var parts: PackedStringArray = request_line.split(" ", false)
	if parts.size() < 2:
		return query
	var target: String = parts[1]
	var mark: int = target.find("?")
	if mark < 0:
		return query
	# A fragment never reaches a server, but a peer may still send one; anything after it
	# belongs to no parameter.
	var raw_query: String = target.substr(mark + 1)
	var fragment: int = raw_query.find("#")
	if fragment >= 0:
		raw_query = raw_query.substr(0, fragment)
	for pair: String in raw_query.split("&", false):
		var separator: int = pair.find("=")
		if separator < 0:
			query[_decoded(pair)] = ""
			continue
		query[_decoded(pair.substr(0, separator))] = _decoded(pair.substr(separator + 1))
	return query

func _decoded(value: String) -> String:
	return value.replace("+", " ").uri_decode()

## Writes one complete HTTP response.
##
## [code]Connection: close[/code] and an explicit [code]Content-Length[/code] together tell
## the browser the answer is finished, so it renders the page rather than spinning until
## the socket closes. A write that fails is logged and otherwise ignored: the callback
## already arrived, and losing the courtesy page does not make the sign-in less successful.
func _write_response(connection: StreamPeerTCP, status: String, body: String) -> void:
	var payload: PackedByteArray = body.to_utf8_buffer()
	var response: String = (
			"HTTP/1.1 %s\r\n"
			+ "Content-Type: text/html; charset=utf-8\r\n"
			+ "Content-Length: %d\r\n"
			+ "Connection: close\r\n"
			+ "Cache-Control: no-store\r\n"
			+ "\r\n") % [status, payload.size()]
	var header: PackedByteArray = response.to_utf8_buffer()
	header.append_array(payload)
	var error: Error = connection.put_data(header)
	if error != OK:
		_log.debug("the loopback reply could not be written (%d)" % error)
	connection.poll()

func _on_timeout() -> void:
	# The watchdog stays connected until the wait settles, so a wait that already received
	# a callback can still reach here. Check first, so a delivered callback is never
	# overwritten by a timeout.
	if _has_settled:
		return
	var elapsed_seconds: float = float(Time.get_ticks_msec() - _started_ticks_ms) / 1000.0
	_log.warn("no loopback callback arrived within %.1fs" % elapsed_seconds)
	_resolve(LoopbackOutcome.TimedOut(elapsed_seconds))

## Marks the wait settled and releases both sockets immediately, but defers the signal that
## resumes the awaiting coroutine.
##
## A wait can settle before its caller has reached its own `await` — [method stop] called
## on the line after [method await_callback] does exactly that. Emitting synchronously
## there would consume the one-shot completion the caller is about to connect to, and this
## engine answers an `await` on an already-completed coroutine by waiting forever rather
## than by returning (#107). Deferring by one message-queue turn guarantees the caller has
## reached its `await` first. Same hazard as [method NativeRequest._resolve], same fix.
func _resolve(outcome: LoopbackOutcome) -> void:
	if _has_settled:
		return
	_has_settled = true
	_settle_count += 1
	_release_sockets()
	_settled.emit.call_deferred(outcome)

## Closes the accepted connection and the listening socket, in that order.
##
## Called from both [method stop] and [method _resolve], so a port is released whichever
## way the wait ended. A listener that kept its port would hold it until the process exits.
func _release_sockets() -> void:
	var current: StreamPeerTCP? = _connection
	if current != null:
		var connection: StreamPeerTCP = current
		_connection = null
		connection.disconnect_from_host()
	var server: TCPServer? = _server
	if server != null:
		var listening: TCPServer = server
		_server = null
		listening.stop()
