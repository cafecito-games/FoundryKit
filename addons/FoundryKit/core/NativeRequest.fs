namespace games.cafecito.foundrykit.core

## Adapts a signal-based native call into a single awaited [NativeOutcome].
##
## FoundryKit's natives report results by emitting a success signal with
## subsystem-specific arguments, or a failure signal carrying (code, message). Consumers
## await a result instead, so this is the one place that translation happens.
##
## An instance handles exactly one request and settles exactly once.
class_name NativeRequest extends RefCounted

const DEFAULT_TIMEOUT_SECONDS: float = 120.0

signal _settled(outcome: NativeOutcome)

var _log: FoundryKitLog
var _has_settled: bool = false
var _payload_fields: Array[String] = []
var _target: Object? = null
var _success_signal: String = ""
var _failure_signal: String = ""
var _started_ticks_ms: int = 0

func _init(log: FoundryKitLog) -> void:
	_log = log

## Awaits whichever native signal fires first, or times out.
##
## [param payload_fields] names the success signal's arguments in order; they are zipped
## into the [code]Succeeded[/code] payload. Extra field names beyond the emitted argument
## count are omitted rather than filled with nulls.
async func await_outcome(
		target: Object,
		success_signal: String,
		payload_fields: Array[String],
		failure_signal: String,
		timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS) -> NativeOutcome:
	_target = target
	_success_signal = success_signal
	_failure_signal = failure_signal
	_payload_fields = payload_fields.duplicate()
	_started_ticks_ms = Time.get_ticks_msec()

	# A caller typically discards its reference to this request once it holds the returned
	# [Coroutine] — the coroutine's suspended state keeps only a raw pointer to its owner,
	# not a strong reference. Without this, the request would be freed while suspended,
	# silently dropping the signal connections below. `unreference()` mirrors this once
	# the request settles.
	reference()

	target.connect(success_signal, _on_native_success)
	target.connect(failure_signal, _on_native_failed)

	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree != null:
		var timer: SceneTreeTimer = tree.create_timer(timeout_seconds)
		timer.timeout.connect(_on_timeout)

	var outcome: NativeOutcome = await _settled
	unreference()
	return outcome

## Settles the request as abandoned. Called by [RequestGuard] when the app regains focus
## with the request still outstanding.
func abandon() -> void:
	_resolve(NativeOutcome.Abandoned)

## The rest parameter cannot be a typed array — the engine rejects
## `...values: Array[Variant]` with "Typed arrays are currently not supported for the
## rest parameter". Elements stay Variant and are copied into the payload unconverted.
func _on_native_success(...values: Array) -> void:
	var payload: Dictionary[String, Variant] = {}
	var count: int = mini(_payload_fields.size(), values.size())
	for index: int in range(count):
		payload[_payload_fields[index]] = values[index]
	_resolve(NativeOutcome.Succeeded(payload))

func _on_native_failed(code: int, message: String) -> void:
	_resolve(NativeOutcome.Failed(code, message))

func _on_timeout() -> void:
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
