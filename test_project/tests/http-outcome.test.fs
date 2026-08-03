namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name HttpOutcomeTests
extends RefCounted
uses Test

func _describe(outcome: HttpOutcome) -> String:
	match outcome:
		HttpOutcome.Answered(status_code, body):
			return "answered:%d:%d" % [status_code, body.size()]
		HttpOutcome.TransportFailed(detail):
			return "transport:%s" % detail
		HttpOutcome.TimedOut(elapsed_seconds):
			return "timeout:%.0f" % elapsed_seconds
	return "unreachable"

func test_answered_carries_status_and_body() -> void:
	Expect.that(_describe(HttpOutcome.Answered(200, "hi".to_utf8_buffer()))) \
		.to_equal("answered:200:2")

func test_a_401_is_just_a_status() -> void:
	# core/ has no notion of authorization; only auth/ interprets 401.
	Expect.that(_describe(HttpOutcome.Answered(401, PackedByteArray()))) \
		.to_equal("answered:401:0")

func test_transport_failure_carries_detail() -> void:
	Expect.that(_describe(HttpOutcome.TransportFailed("dns"))).to_equal("transport:dns")

func test_timeout_carries_elapsed() -> void:
	Expect.that(_describe(HttpOutcome.TimedOut(30.0))).to_equal("timeout:30")
