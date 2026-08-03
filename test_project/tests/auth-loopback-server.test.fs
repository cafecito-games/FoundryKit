namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.auth.internal

## Covers the loopback redirect listener end to end over a real socket.
##
## Nothing here is faked: every test that delivers a callback opens a real
## [StreamPeerTCP] to the port the server bound and writes a real HTTP request line. The
## socket is the thing under test, so faking it would leave the interesting half — accept,
## read, parse, reply, close — unproven.
##
## What this suite cannot verify: that a real browser follows the redirect, and that a real
## authorization server sends the callback. Both belong to epic G.
class_name AuthLoopbackServerTests
extends RefCounted
uses Test

## Long enough that a slow CI frame cannot turn a delivered callback into a timeout, short
## enough that a genuine hang is killed by the runner rather than waited out.
const _GENEROUS_TIMEOUT_SECONDS: float = 10.0

## Bounds every polling helper below, so a defect surfaces as a failing assertion rather
## than a suite that never finishes.
const _MAX_POLLED_FRAMES: int = 1200

var _log: FoundryKitLog

## Holds every client socket a test opened until that test ends.
##
## A [StreamPeerTCP] closes as soon as its last reference goes, so a client left to a local
## variable would disconnect the moment the helper that made it returned — before the
## server ever read the request.
var _clients: Array[StreamPeerTCP] = []

var _servers: Array[LoopbackServer] = []

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_clients = []
	_servers = []

func after_each() -> void:
	for client: StreamPeerTCP in _clients:
		client.disconnect_from_host()
	_clients = []
	for server: LoopbackServer in _servers:
		server.stop()
	_servers = []

## Renders an outcome as a comparable string, so assertions never need a failure helper.
func _describe(outcome: LoopbackOutcome) -> String:
	match outcome:
		LoopbackOutcome.Received(query):
			return "received:%s:%s" % [
				str(query.get("code", "")), str(query.get("state", ""))]
		LoopbackOutcome.TimedOut(_elapsed_seconds):
			return "timeout"
		LoopbackOutcome.Failed(detail):
			return "failed:%s" % detail
	return "unreachable"

func _query_of(outcome: LoopbackOutcome) -> Dictionary[String, String]:
	match outcome:
		LoopbackOutcome.Received(query):
			return query
		LoopbackOutcome.TimedOut(_elapsed_seconds):
			return {}
		LoopbackOutcome.Failed(_detail):
			return {}
	return {}

func _tree() -> SceneTree?:
	return Engine.get_main_loop() as SceneTree

func _started_server() -> LoopbackServer:
	var server: LoopbackServer = LoopbackServer.new(_log)
	_servers.append(server)
	server.start()
	return server

## Opens a client socket to [param port] and writes [param request] on it.
##
## Returns having written the request and without awaiting anything afterwards, so a caller
## can reach its own `await` on the server's pending wait in the same frame. That ordering
## is not incidental: a wait that settled in an earlier frame is a completed coroutine, and
## awaiting one of those hangs this engine forever (#107).
async func _connect_and_send(port: int, request: String) -> StreamPeerTCP:
	var client: StreamPeerTCP = StreamPeerTCP.new()
	_clients.append(client)
	client.connect_to_host("127.0.0.1", port)
	var tree: SceneTree? = _tree()
	if tree == null:
		return client
	var loop: SceneTree = tree
	var frames: int = 0
	client.poll()
	while client.get_status() == StreamPeerTCP.STATUS_CONNECTING and frames < _MAX_POLLED_FRAMES:
		await loop.process_frame
		frames += 1
		client.poll()
	client.put_data(request.to_utf8_buffer())
	return client

## Reads whatever the server wrote, until it closes the connection or the budget runs out.
async func _read_response(client: StreamPeerTCP) -> String:
	var tree: SceneTree? = _tree()
	if tree == null:
		return ""
	var loop: SceneTree = tree
	var received: String = ""
	var frames: int = 0
	while frames < _MAX_POLLED_FRAMES:
		client.poll()
		var available: int = client.get_available_bytes()
		if available > 0:
			received += client.get_utf8_string(available)
			continue
		if not received.is_empty() and client.get_status() != StreamPeerTCP.STATUS_CONNECTED:
			break
		await loop.process_frame
		frames += 1
	return received

async func _skip_frames(count: int) -> void:
	var tree: SceneTree? = _tree()
	if tree == null:
		return
	var loop: SceneTree = tree
	for _index: int in range(count):
		await loop.process_frame

func test_binds_loopback_and_reports_its_port() -> void:
	var server: LoopbackServer = _started_server()
	Expect.that(server.port() > 0).to_be_true()
	Expect.that(server.bind_address()).to_equal("127.0.0.1")
	server.stop()

func test_starting_twice_keeps_the_same_port() -> void:
	var server: LoopbackServer = _started_server()
	var first_port: int = server.port()
	Expect.that(server.start()).to_be_true()
	Expect.that(server.port()).to_equal(first_port)

## The security property, asserted rather than assumed: a listener reachable from the LAN
## would let anyone on it deliver a forged OAuth callback. Connecting to one of this
## machine's own non-loopback addresses must be refused, which it can only be if the socket
## is bound to 127.0.0.1 rather than 0.0.0.0.
func test_is_not_reachable_on_a_non_loopback_address() -> void:
	var server: LoopbackServer = _started_server()
	var external: String = _non_loopback_address()
	if external.is_empty():
		return
	var probe: StreamPeerTCP = StreamPeerTCP.new()
	_clients.append(probe)
	probe.connect_to_host(external, server.port())
	var tree: SceneTree? = _tree()
	if tree == null:
		return
	var loop: SceneTree = tree
	var frames: int = 0
	probe.poll()
	while probe.get_status() == StreamPeerTCP.STATUS_CONNECTING and frames < 120:
		await loop.process_frame
		frames += 1
		probe.poll()
	Expect.that(probe.get_status() == StreamPeerTCP.STATUS_CONNECTED).to_be_false()

## Returns an IPv4 address of this machine that is neither loopback nor link-local, or an
## empty string when it has none — a container with only a loopback interface, for one.
func _non_loopback_address() -> String:
	for address: String in IP.get_local_addresses():
		if address.get_slice_count(".") != 4:
			continue
		if address.begins_with("127.") or address.begins_with("169.254."):
			continue
		return address
	return ""

func test_delivers_the_query_of_the_first_request() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	await _connect_and_send(server.port(), "GET /?code=abc&state=xyz HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("received:abc:xyz")

func test_percent_encoded_values_are_decoded() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	await _connect_and_send(
			server.port(), "GET /?code=a%2Fb%3Dc&state=x+y HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	var query: Dictionary[String, String] = _query_of(outcome)
	Expect.that(query.get("code", "")).to_equal("a/b=c")
	Expect.that(query.get("state", "")).to_equal("x y")

## Telling someone who has just pressed Cancel that they are signed in is a lie the player
## acts on, and the listener is the only participant with a page in front of them.
func test_a_refused_callback_is_not_answered_with_a_success_page() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	var client: StreamPeerTCP = await _connect_and_send(
			server.port(), "GET /?error=access_denied&state=xyz HTTP/1.1\r\n\r\n")
	await pending
	var response: String = await _read_response(client)
	Expect.that(response.contains("You are signed in")).to_be_false()
	Expect.that(response.contains("Sign-in was not completed")).to_be_true()

func test_an_error_callback_is_delivered_like_any_other() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	await _connect_and_send(
			server.port(), "GET /?error=access_denied&state=xyz HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	var query: Dictionary[String, String] = _query_of(outcome)
	Expect.that(query.get("error", "")).to_equal("access_denied")

func test_a_request_without_a_query_yields_an_empty_dictionary() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	await _connect_and_send(server.port(), "GET / HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	Expect.that(_query_of(outcome).is_empty()).to_be_true()

## Without a readable reply the player sees a browser error page on a sign-in that in fact
## succeeded, which is the single most confusing failure this class can produce.
func test_answers_the_browser_with_a_readable_html_page() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	var client: StreamPeerTCP = await _connect_and_send(
			server.port(), "GET /?code=abc&state=xyz HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("received:abc:xyz")
	var response: String = await _read_response(client)
	Expect.that(response.begins_with("HTTP/1.1 200 OK")).to_be_true()
	Expect.that(response.contains("Content-Type: text/html")).to_be_true()
	Expect.that(response.contains("<html")).to_be_true()
	Expect.that(response.contains("</html>")).to_be_true()

func test_times_out_when_no_callback_arrives() -> void:
	var server: LoopbackServer = _started_server()
	var outcome: LoopbackOutcome = await server.await_callback(0.2)
	Expect.that(_describe(outcome)).to_equal("timeout")

## A listener left holding its port keeps it for the lifetime of the process, so settling
## must release it whichever way the wait ended.
func test_a_timed_out_listener_releases_its_port() -> void:
	var server: LoopbackServer = _started_server()
	await server.await_callback(0.2)
	Expect.that(server.port()).to_equal(0)

func test_stop_is_idempotent() -> void:
	var server: LoopbackServer = _started_server()
	server.stop()
	server.stop()
	Expect.that(server.port()).to_equal(0)

func test_stopping_while_waiting_settles_the_wait() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	server.stop()
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("failed:the loopback listener was stopped")
	Expect.that(server.settle_count()).to_equal(1)

func test_awaiting_a_listener_that_never_started_fails_rather_than_hanging() -> void:
	var server: LoopbackServer = LoopbackServer.new(_log)
	_servers.append(server)
	var outcome: LoopbackOutcome = await server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	Expect.that(_describe(outcome)).to_equal("failed:the loopback server is not listening")

func test_a_second_wait_is_refused_rather_than_stealing_the_first() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	var refused: LoopbackOutcome = await server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	Expect.that(_describe(refused)).to_equal(
			"failed:this listener has already been awaited")
	server.stop()
	var first: LoopbackOutcome = await pending
	Expect.that(_describe(first)).to_equal("failed:the loopback listener was stopped")
	Expect.that(server.settle_count()).to_equal(1)

## The defect this guards against is a watchdog that fires after a callback already
## arrived. It leaves every assertion above green and only shows up as a sign-in that
## reports success and then reports a timeout over the top of it.
func test_settles_exactly_once_when_the_watchdog_fires_after_the_callback() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(0.2)
	await _connect_and_send(server.port(), "GET /?code=abc&state=xyz HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("received:abc:xyz")
	# Well past the 0.2 s watchdog, which is still connected when a callback settles first.
	await _skip_frames(60)
	Expect.that(server.settle_count()).to_equal(1)

## The lifetime hazard [member LoopbackServer._in_flight] exists for: the caller keeps only
## the coroutine, so without the registry the suspended wait is freed and its poll loop and
## watchdog vanish with it.
func test_settles_when_its_caller_keeps_only_the_coroutine() -> void:
	var server: LoopbackServer = LoopbackServer.new(_log)
	server.start()
	var port: int = server.port()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	server = LoopbackServer.new(_log)
	await _connect_and_send(port, "GET /?code=abc&state=xyz HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("received:abc:xyz")

## A request line longer than the cap is refused rather than buffered, so a peer cannot
## grow this process's memory by never sending a newline.
func test_an_oversized_request_line_is_refused() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	var oversized: String = "GET /?code=%s" % "a".repeat(LoopbackServer.MAX_REQUEST_BYTES + 64)
	await _connect_and_send(server.port(), oversized)
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal(
			"failed:the request line exceeded %d bytes" % LoopbackServer.MAX_REQUEST_BYTES)

## The bypass a cap tested only against an unterminated buffer would leave open: one read
## can deliver a whole oversized line, newline and all.
func test_an_oversized_request_line_that_arrives_complete_is_refused() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	var oversized: String = "GET /?code=%s HTTP/1.1\r\n\r\n" % (
			"a".repeat(LoopbackServer.MAX_REQUEST_BYTES + 64))
	await _connect_and_send(server.port(), oversized)
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal(
			"failed:the request line exceeded %d bytes" % LoopbackServer.MAX_REQUEST_BYTES)

## The cap bounds the request line, not the request. A browser sending several kibibytes of
## cookies with an ordinary callback is not an abusive peer, and refusing it would break
## sign-in on exactly the machines that have signed in before.
func test_large_headers_after_a_short_request_line_are_accepted() -> void:
	var server: LoopbackServer = _started_server()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(_GENEROUS_TIMEOUT_SECONDS)
	var request: String = "GET /?code=abc&state=xyz HTTP/1.1\r\nCookie: %s\r\n\r\n" % (
			"c".repeat(LoopbackServer.MAX_REQUEST_BYTES * 2))
	await _connect_and_send(server.port(), request)
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("received:abc:xyz")
