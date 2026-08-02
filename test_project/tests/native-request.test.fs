namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

class_name NativeRequestTests
extends RefCounted
uses Test

const _FIELDS: Array[String] = ["id_token", "email"]

var _log: FoundryKitLog
var _native: FakeNative

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_native = FakeNative.new()

func after_each() -> void:
	_native.free()

func _start(timeout_seconds: float) -> Coroutine[NativeOutcome]:
	var request: NativeRequest = NativeRequest.new(_log)
	return request.await_outcome(
			_native, "operation_success", _FIELDS, "operation_failed", timeout_seconds)

## Renders an outcome as a comparable string, so assertions never need a failure helper.
func _describe(outcome: NativeOutcome) -> String:
	match outcome:
		NativeOutcome.Succeeded(payload):
			return "ok:%s:%s" % [str(payload.get("id_token", "")), str(payload.get("email", ""))]
		NativeOutcome.Failed(code, message):
			return "fail:%d:%s" % [code, message]
		NativeOutcome.TimedOut(_elapsed_seconds):
			return "timeout"
		NativeOutcome.Abandoned:
			return "abandoned"
		NativeOutcome.Unavailable(missing_class):
			return "unavailable:%s" % missing_class
	return "unreachable"

## Returns the number of payload keys, or -1 when the outcome is not a success.
func _payload_size(outcome: NativeOutcome) -> int:
	match outcome:
		NativeOutcome.Succeeded(payload):
			return payload.size()
		NativeOutcome.Failed(_code, _message):
			return -1
		NativeOutcome.TimedOut(_elapsed_seconds):
			return -1
		NativeOutcome.Abandoned:
			return -1
		NativeOutcome.Unavailable(_missing_class):
			return -1
	return -1

## A headless runner with no [SceneTree] main loop never fires the watchdog timer that
## backs the timeout path, which would hang these tests forever. Gate timeout-dependent
## assertions behind this check instead of deleting them, so the coverage gap is visible
## rather than silent. (This project's runner does provide a [SceneTree], but the check
## stays as a guard against environments that do not.)
func _has_scene_tree() -> bool:
	return Engine.get_main_loop() as SceneTree != null

func test_success_signal_resolves_succeeded_with_named_payload() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_success("token-value", "user@example.com")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("ok:token-value:user@example.com")

func test_failure_signal_resolves_failed_with_code_and_message() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_failure(4, "storage unavailable")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("fail:4:storage unavailable")

func test_no_signal_within_timeout_resolves_timed_out() -> void:
	if not _has_scene_tree():
		return
	var outcome: NativeOutcome = await _start(0.05)
	Expect.that(_describe(outcome)).to_equal("timeout")

func test_late_second_signal_is_ignored() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_success("first", "a@b.c")
	var outcome: NativeOutcome = await pending
	_native.emit_failure(9, "too late")
	Expect.that(_describe(outcome)).to_equal("ok:first:a@b.c")

func test_connections_are_released_after_settling() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_success("a", "b")
	await pending
	Expect.that(_native.success_connection_count()).to_equal(0)
	Expect.that(_native.failure_connection_count()).to_equal(0)

func test_connections_are_released_after_timeout() -> void:
	if not _has_scene_tree():
		return
	await _start(0.05)
	Expect.that(_native.success_connection_count()).to_equal(0)
	Expect.that(_native.failure_connection_count()).to_equal(0)

func test_fewer_signal_arguments_than_fields_leaves_missing_keys_absent() -> void:
	var request: NativeRequest = NativeRequest.new(_log)
	var pending: Coroutine[NativeOutcome] = request.await_outcome(
			_native,
			"operation_success",
			["id_token", "email", "display_name"],
			"operation_failed",
			5.0)
	_native.emit_success("token", "user@example.com")
	var outcome: NativeOutcome = await pending
	# Only two arguments were emitted, so "display_name" must be absent rather than null.
	Expect.that(_payload_size(outcome)).to_equal(2)
	Expect.that(_describe(outcome)).to_equal("ok:token:user@example.com")
