namespace games.cafecito.foundrykit.core

## Adapts a signal-based native call into a single awaited [NativeOutcome].
##
## FoundryKit's natives report results by emitting a success signal with
## subsystem-specific arguments, or a failure signal carrying (code, message). Consumers
## await a result instead, so this is the one place that translation happens.
##
## An instance handles exactly one request and settles exactly once.
##
## Each request may carry a per-call correlation token. When it does, the native echoes
## that token as the **first** argument of both its success and failure signals, and this
## adapter ignores any emission carrying a different one. That closes the window where a
## timed-out or [method abandon]ed request's late reply is mistaken for the result of a
## later request connected to the same target and signal names. A request started with an
## empty token opts out and accepts any emission, which is the correct behaviour for a
## native that predates the token protocol.
class_name NativeRequest extends RefCounted

const DEFAULT_TIMEOUT_SECONDS: float = 120.0

## Keeps every in-flight request alive independent of its caller's own reference.
##
## A caller typically discards its local variable once it holds the returned [Coroutine]
## — the coroutine's suspended state keeps only a raw pointer to its owner, not a strong
## reference. Without this registry the request would be freed while suspended, silently
## dropping the signal connections below. Each instance removes itself once settled.
static var _in_flight: Array[NativeRequest] = []

signal _settled(outcome: NativeOutcome)

var _log: FoundryKitLog
var _has_settled: bool = false
var _payload_fields: Array[String] = []
var _target: Object? = null
var _success_signal: String = ""
var _failure_signal: String = ""
var _started_ticks_ms: int = 0
var _correlation_token: String = ""

func _init(log: FoundryKitLog) -> void:
	_log = log

## Awaits whichever native signal fires first, or times out.
##
## [param payload_fields] names the success signal's arguments in order; they are zipped
## into the [code]Succeeded[/code] payload. Extra field names beyond the emitted argument
## count are omitted rather than filled with nulls.
##
## [param correlation_token] opts the request into emission filtering. When it is
## non-empty, both signals are expected to carry it as their first argument; emissions
## carrying anything else are ignored without settling, and the token itself is consumed
## rather than zipped into the payload. An empty token disables filtering entirely.
##
## Await the returned [Coroutine] promptly — do not hold it across a frame boundary
## before awaiting it. This engine's [code]await[/code] does not check whether a
## coroutine has already completed before connecting to its one-shot completion signal,
## so a coroutine that finishes before anyone awaits it hangs that later await forever.
## [method _resolve] defers its settling signal by one message-queue turn specifically to
## keep this method's own synchronous callers safe (see its doc comment); a caller that
## itself defers awaiting past that point is outside the safe window this method provides.
##
## The watchdog requires a running [SceneTree] ([method Engine.get_main_loop]). Without
## one, no timer is installed and this only resolves via the native's own signals or
## [method abandon] — there is no fallback timeout mechanism.
async func await_outcome(
		target: Object,
		success_signal: String,
		payload_fields: Array[String],
		failure_signal: String,
		timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
		correlation_token: String = "") -> NativeOutcome:
	_correlation_token = correlation_token
	_target = target
	_success_signal = success_signal
	_failure_signal = failure_signal
	_payload_fields = payload_fields.duplicate()
	_started_ticks_ms = Time.get_ticks_msec()

	_in_flight.append(self)

	target.connect(success_signal, _on_native_success)
	target.connect(failure_signal, _on_native_failed)

	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree != null:
		# The watchdog bounds a native OS-level call, not game simulation — it must keep
		# running even while the game is paused or its time scale is zero.
		var timer: SceneTreeTimer = tree.create_timer(
				timeout_seconds, true, false, true)
		timer.timeout.connect(_on_timeout)

	var outcome: NativeOutcome = await _settled
	_in_flight.erase(self)
	return outcome

## Settles the request as abandoned. Called by [RequestGuard] when the app regains focus
## with the request still outstanding.
func abandon() -> void:
	_resolve(NativeOutcome.Abandoned)

## Accepts an emission only when it answers this request.
##
## With a correlation token, the native echoes it as the **first** signal argument;
## a non-matching emission belongs to an earlier request that timed out or was
## abandoned, and must be ignored without settling — settling on it would hand one
## request's result to another.
##
## The rest parameter cannot be a typed array — the engine rejects
## `...values: Array[Variant]` with "Typed arrays are currently not supported for the
## rest parameter". Elements stay Variant and are copied into the payload unconverted.
func _on_native_success(...values: Array) -> void:
	var offset: int = 0
	if not _correlation_token.is_empty():
		if values.is_empty():
			return
		if str(values[0]) != _correlation_token:
			_log.debug("ignoring success emission for a different request")
			return
		offset = 1
	var payload: Dictionary[String, Variant] = {}
	var available: int = values.size() - offset
	var count: int = mini(_payload_fields.size(), available)
	for index: int in range(count):
		payload[_payload_fields[index]] = values[index + offset]
	_resolve(NativeOutcome.Succeeded(payload))

## Filters failure emissions by the same rule as [method _on_native_success]. It takes a
## rest parameter rather than (code, message) so it can absorb the leading token.
func _on_native_failed(...values: Array) -> void:
	var offset: int = 0
	if not _correlation_token.is_empty():
		if values.is_empty():
			return
		if str(values[0]) != _correlation_token:
			_log.debug("ignoring failure emission for a different request")
			return
		offset = 1
	if values.size() < offset + 2:
		return
	var code: int = 0
	var raw_code: Variant = values[offset]
	if raw_code is int:
		code = raw_code
	_resolve(NativeOutcome.Failed(code, str(values[offset + 1])))

func _on_timeout() -> void:
	# The watchdog timer stays connected until the request settles, so a request that
	# already succeeded, failed or was abandoned can still reach here. Check first so a
	# completed request is never misreported as timed out.
	if _has_settled:
		return
	var elapsed_seconds: float = float(Time.get_ticks_msec() - _started_ticks_ms) / 1000.0
	_log.warn("native request timed out after %.1fs" % elapsed_seconds)
	_resolve(NativeOutcome.TimedOut(elapsed_seconds))

## Marks the request settled and disconnects immediately, but defers the signal that
## resumes the awaiting coroutine. A caller may settle a request (via a synchronous
## native signal, or [method abandon]) before it has started awaiting the returned
## [Coroutine] — resolving synchronously in that case would complete the coroutine and
## consume its one-shot completion signal before anyone is listening, hanging the
## eventual [code]await[/code] forever. Deferring the emission guarantees the caller has
## reached its `await` by the time this fires.
func _resolve(outcome: NativeOutcome) -> void:
	if _has_settled:
		return
	_has_settled = true
	_disconnect_native()
	_settled.emit.call_deferred(outcome)

func _disconnect_native() -> void:
	if _target == null:
		return
	var target: Object = _target
	if target.is_connected(_success_signal, _on_native_success):
		target.disconnect(_success_signal, _on_native_success)
	if target.is_connected(_failure_signal, _on_native_failed):
		target.disconnect(_failure_signal, _on_native_failed)
	_target = null
