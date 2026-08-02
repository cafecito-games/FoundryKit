namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name NativeOutcomeTests
extends RefCounted
uses Test

## Classifies an outcome without a wildcard branch, proving exhaustiveness holds.
func _describe(outcome: NativeOutcome) -> String:
	match outcome:
		NativeOutcome.Succeeded(payload):
			return "ok:%d" % payload.size()
		NativeOutcome.Failed(code, message):
			return "fail:%d:%s" % [code, message]
		NativeOutcome.TimedOut(elapsed_seconds):
			return "timeout:%.1f" % elapsed_seconds
		NativeOutcome.Abandoned:
			return "abandoned"
		NativeOutcome.Unavailable(missing_class):
			return "unavailable:%s" % missing_class
	return "unreachable"

func test_succeeded_carries_payload() -> void:
	var payload: Dictionary[String, Variant] = {"id_token": "abc", "email": "a@b.c"}
	Expect.that(_describe(NativeOutcome.Succeeded(payload))).to_equal("ok:2")

func test_failed_carries_code_and_message() -> void:
	Expect.that(_describe(NativeOutcome.Failed(3, "boom"))).to_equal("fail:3:boom")

func test_timed_out_carries_elapsed_seconds() -> void:
	Expect.that(_describe(NativeOutcome.TimedOut(1.5))).to_equal("timeout:1.5")

func test_abandoned_is_a_payload_less_value() -> void:
	Expect.that(_describe(NativeOutcome.Abandoned)).to_equal("abandoned")

func test_unavailable_carries_missing_class_name() -> void:
	Expect.that(_describe(NativeOutcome.Unavailable("iOSGoogleSignIn"))) \
		.to_equal("unavailable:iOSGoogleSignIn")

func test_succeeded_with_empty_payload_is_valid() -> void:
	var empty: Dictionary[String, Variant] = {}
	Expect.that(_describe(NativeOutcome.Succeeded(empty))).to_equal("ok:0")
