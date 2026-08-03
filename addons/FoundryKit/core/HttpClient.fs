namespace games.cafecito.foundrykit.core

## The production [HttpTransport], backed by the engine's [HTTPRequest].
##
## One instance is long-lived and reusable: every call to [method send] owns its own
## request state, so concurrent and sequential calls cannot settle each other. Callers
## depend on [HttpTransport], never on this class, so no test needs a network.
##
## Lifetime, single-settle and deferred-resume behaviour mirror [NativeRequest], which
## solved the identical problems first. See the comments on [member _in_flight] and
## [method _PendingRequest._resolve] for why each is required.
##
## Redirects are never followed: a 3xx comes back as an [code]Answered[/code] status for
## the caller to act on. Following one would replay the caller's headers at whatever host
## the response named, so any credential in them would leave the origin the caller chose.
##
## Without a running [SceneTree] there is nowhere to park an [HTTPRequest] node and no
## watchdog clock, so [method send] resolves immediately with
## [code]TransportFailed[/code]. That is deliberate: a headless host must get an answer it
## can act on rather than a pushed error or a request that never returns.
class_name HttpClient extends RefCounted
uses HttpTransport

## Bounds the whole attempt when a caller does not supply its own timeout.
const DEFAULT_TIMEOUT_SECONDS: float = 30.0

## Keeps every client with a suspended [method send] alive independent of its caller.
##
## A caller typically discards its local variable once it holds the returned [Coroutine] —
## the coroutine's suspended state keeps only a raw pointer to its owner, not a strong
## reference. Without this registry the client would be freed mid-request, silently
## dropping the signal connections that settle it. Each entry is removed when its own
## [method send] resumes, so a client with two concurrent sends appears twice and is
## released only after both finish.
static var _in_flight: Array[HttpClient] = []

var _log: FoundryKitLog

func _init(log: FoundryKitLog) -> void:
	_log = log

## Performs one HTTP request. See [method HttpTransport.send] for the contract.
##
## Only the failure paths of this method are covered by the test suite — real network
## behaviour, and therefore the [code]Answered[/code] path, is not exercised there.
async func send(
		method: String,
		url: String,
		headers: PackedStringArray,
		body: PackedByteArray,
		timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS) -> HttpOutcome:
	_in_flight.append(self)
	var request: _PendingRequest = _PendingRequest.new(_log)
	var outcome: HttpOutcome = await request.perform(
			method, url, headers, body, timeout_seconds)
	_in_flight.erase(self)
	return outcome

## One attempt: one [HTTPRequest] node, one watchdog, one settlement.
##
## Separate from [HttpClient] because a transport is reused while a request is not. Nested
## rather than a second file because only one head type may live in a `.fs` file.
class _PendingRequest extends RefCounted:

	## Keeps a suspended request alive for the same reason [member HttpClient._in_flight]
	## does: the awaiting coroutine holds no strong reference to it, and a freed request
	## takes its [signal HTTPRequest.request_completed] connection with it.
	static var _pending: Array[_PendingRequest] = []

	signal _settled(outcome: HttpOutcome)

	var _log: FoundryKitLog
	var _has_settled: bool = false
	var _request_node: HTTPRequest? = null
	var _started_ticks_ms: int = 0

	func _init(log: FoundryKitLog) -> void:
		_log = log

	async func perform(
			method: String,
			url: String,
			headers: PackedStringArray,
			body: PackedByteArray,
			timeout_seconds: float) -> HttpOutcome:
		var upper_method: String = method.to_upper()
		if not _is_supported_method(upper_method):
			return await _settle_without_starting(
					HttpOutcome.TransportFailed("unsupported HTTP method \"%s\"" % method))

		var tree: SceneTree = Engine.get_main_loop() as SceneTree
		if tree == null:
			# No main loop means no node to host the request and no clock to bound it.
			# Answer rather than hang; the caller decides what an unreachable host means.
			_log.debug("http request cannot run without a scene tree")
			return await _settle_without_starting(
					HttpOutcome.TransportFailed("no scene tree is available"))

		_started_ticks_ms = Time.get_ticks_msec()
		var node: HTTPRequest = HTTPRequest.new()
		_request_node = node
		_pending.append(self)
		tree.root.add_child(node)
		node.timeout = timeout_seconds
		# Never follow redirects. [HTTPRequest] otherwise follows up to eight of them and
		# replays the caller's headers on each hop, including when Location names a
		# different origin — which would hand whatever credential a caller put in those
		# headers to a host it never chose to talk to. A redirect is reported to the caller
		# as the status it is, and the caller decides whether the new location is one it
		# trusts.
		node.max_redirects = 0
		node.request_completed.connect(_on_request_completed)

		# The watchdog bounds a network call, not game simulation — it must keep running
		# even while the game is paused or its time scale is zero. HTTPRequest.timeout
		# covers the same window, but only once the request has actually started.
		var timer: SceneTreeTimer = tree.create_timer(timeout_seconds, true, false, true)
		timer.timeout.connect(_on_timeout)

		var started: Error = node.request_raw(
				url, headers, _method_code(upper_method), body)
		if started != OK:
			_resolve(HttpOutcome.TransportFailed(
					"request could not be started (%d)" % started))

		var outcome: HttpOutcome = await _settled
		_pending.erase(self)
		return outcome

	## Resolves before any node or timer exists. Still routed through [method _resolve] so
	## the settling emission is deferred by one message-queue turn, exactly as a request
	## that started would be — see that method for why that matters.
	async func _settle_without_starting(outcome: HttpOutcome) -> HttpOutcome:
		_pending.append(self)
		_resolve(outcome)
		var settled: HttpOutcome = await _settled
		_pending.erase(self)
		return settled

	func _on_request_completed(
			result: int,
			response_code: int,
			_headers: PackedStringArray,
			body: PackedByteArray) -> void:
		if result == HTTPRequest.RESULT_SUCCESS:
			_resolve(HttpOutcome.Answered(response_code, body))
			return
		if result == HTTPRequest.RESULT_TIMEOUT:
			_resolve(HttpOutcome.TimedOut(_elapsed_seconds()))
			return
		if result == HTTPRequest.RESULT_REDIRECT_LIMIT_REACHED and response_code > 0:
			# Redirects are refused rather than followed (see [method perform]), so the
			# engine reports the 3xx this way. Report it as the answer it is, so a caller
			# can tell a moved endpoint from a host it could not reach. The body is empty
			# on this path — only the status and the engine's own headers are available.
			_resolve(HttpOutcome.Answered(response_code, body))
			return
		_resolve(HttpOutcome.TransportFailed(_describe_result(result)))

	func _on_timeout() -> void:
		# The watchdog stays connected until the request settles, so a request that already
		# answered or failed can still reach here. Check first, so a completed request is
		# never misreported as timed out.
		if _has_settled:
			return
		var elapsed_seconds: float = _elapsed_seconds()
		_log.warn("http request timed out after %.1fs" % elapsed_seconds)
		_resolve(HttpOutcome.TimedOut(elapsed_seconds))

	## Marks the request settled and releases its node immediately, but defers the signal
	## that resumes the awaiting coroutine. A request may settle before its caller has
	## reached its own `await` — an unsupported method settles synchronously, and so can a
	## request the engine refuses to start. Resolving synchronously in that case would
	## complete the coroutine and consume its one-shot completion signal before anyone is
	## listening, hanging the eventual `await` forever. This is the same hazard
	## [method NativeRequest._resolve] guards against, and the same fix.
	func _resolve(outcome: HttpOutcome) -> void:
		if _has_settled:
			return
		_has_settled = true
		_release_node()
		_settled.emit.call_deferred(outcome)

	func _release_node() -> void:
		var node: HTTPRequest? = _request_node
		if node == null:
			return
		_request_node = null
		if node.request_completed.is_connected(_on_request_completed):
			node.request_completed.disconnect(_on_request_completed)
		node.cancel_request()
		node.queue_free()

	func _elapsed_seconds() -> float:
		return float(Time.get_ticks_msec() - _started_ticks_ms) / 1000.0

	func _is_supported_method(upper_method: String) -> bool:
		match upper_method:
			"GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS":
				return true
		return false

	## Only ever called for a method [method _is_supported_method] accepted, so the final
	## return is the [code]GET[/code] case rather than a fallback for unknown input.
	func _method_code(upper_method: String) -> HTTPClient.Method:
		match upper_method:
			"POST":
				return HTTPClient.METHOD_POST
			"PUT":
				return HTTPClient.METHOD_PUT
			"PATCH":
				return HTTPClient.METHOD_PATCH
			"DELETE":
				return HTTPClient.METHOD_DELETE
			"HEAD":
				return HTTPClient.METHOD_HEAD
			"OPTIONS":
				return HTTPClient.METHOD_OPTIONS
		return HTTPClient.METHOD_GET

	func _describe_result(result: int) -> String:
		match result:
			HTTPRequest.RESULT_CANT_CONNECT:
				return "could not connect to the host"
			HTTPRequest.RESULT_CANT_RESOLVE:
				return "could not resolve the host name"
			HTTPRequest.RESULT_CONNECTION_ERROR:
				return "the connection was interrupted"
			HTTPRequest.RESULT_TLS_HANDSHAKE_ERROR:
				return "the TLS handshake failed"
			HTTPRequest.RESULT_NO_RESPONSE:
				return "the host closed the connection without responding"
			HTTPRequest.RESULT_REDIRECT_LIMIT_REACHED:
				return "the host redirected the request, which is not followed"
		return "the request failed (%d)" % result
