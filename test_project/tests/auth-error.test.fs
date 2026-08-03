namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

class_name AuthErrorTests
extends RefCounted
uses Test

## Renders an error without a wildcard branch, proving exhaustiveness holds.
func _describe(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(provider):
			return "unavailable:%d" % provider
		AuthError.Configuration(detail):
			return "configuration:%s" % detail
		AuthError.Storage(detail):
			return "storage:%s" % detail
		AuthError.RequestFailed(status, body):
			return "request_failed:%d:%s" % [status, body]
		AuthError.InvalidResponse(detail):
			return "invalid_response:%s" % detail
		AuthError.MissingField(field):
			return "missing_field:%s" % field
		AuthError.SessionExpired(expired_at):
			return "session_expired:%d" % expired_at
		AuthError.TimedOut(elapsed_seconds):
			return "timed_out:%s" % ("positive" if elapsed_seconds > 0.0 else "zero")
	return "unreachable"

func test_payload_less_cases_are_values() -> void:
	Expect.that(_describe(AuthError.Cancelled)).to_equal("cancelled")
	Expect.that(_describe(AuthError.NoCredential)).to_equal("no_credential")

func test_unavailable_carries_provider() -> void:
	Expect.that(_describe(AuthError.Unavailable(Provider.APPLE))).to_equal("unavailable:1")

func test_configuration_and_storage_carry_detail() -> void:
	Expect.that(_describe(AuthError.Configuration("missing web_client_id"))) \
		.to_equal("configuration:missing web_client_id")
	Expect.that(_describe(AuthError.Storage("keychain denied"))).to_equal("storage:keychain denied")

func test_request_failed_carries_status_and_body() -> void:
	Expect.that(_describe(AuthError.RequestFailed(503, "unavailable"))) \
		.to_equal("request_failed:503:unavailable")

func test_missing_field_and_invalid_response_carry_context() -> void:
	Expect.that(_describe(AuthError.MissingField("access_token"))).to_equal("missing_field:access_token")
	Expect.that(_describe(AuthError.InvalidResponse("not json"))).to_equal("invalid_response:not json")

func test_session_expired_carries_expiry() -> void:
	Expect.that(_describe(AuthError.SessionExpired(1750000000))).to_equal("session_expired:1750000000")

func test_from_native_maps_failure_timeout_and_unavailable() -> void:
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Failed(4, "boom"), Provider.GOOGLE))) \
		.to_equal("request_failed:4:boom")
	Expect.that(_describe(AuthError.from_native(NativeOutcome.TimedOut(2.5), Provider.GOOGLE))) \
		.to_equal("timed_out:positive")
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Unavailable("iOSGoogleSignIn"), Provider.APPLE))) \
		.to_equal("unavailable:1")

func test_from_native_maps_abandoned_to_cancelled_and_success_to_invalid() -> void:
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Abandoned, Provider.GOOGLE))) \
		.to_equal("cancelled")
	var payload: Dictionary[String, Variant] = {}
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Succeeded(payload), Provider.GOOGLE))) \
		.to_equal("invalid_response:native reported success where a failure was expected")
