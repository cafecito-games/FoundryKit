namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core

class_name NullAuthBackendTests
extends RefCounted
uses Test

var _backend: NullAuthBackend

func before_each() -> void:
	var log: FoundryKitLog = FoundryKitLog.new("test")
	_backend = NullAuthBackend.new(log)

func _credential_failure_name(result: CredentialResult) -> String:
	match result:
		CredentialResult.Success(_credential):
			return "unexpected_success"
		CredentialResult.Failure(error):
			match error:
				AuthError.Unavailable(_provider):
					return "unavailable"
				AuthError.Cancelled:
					return "cancelled"
				AuthError.NoCredential:
					return "no_credential"
				AuthError.Configuration(_d):
					return "configuration"
				AuthError.Storage(_d):
					return "storage"
				AuthError.RequestFailed(_s, _b):
					return "request_failed"
				AuthError.InvalidResponse(_d):
					return "invalid_response"
				AuthError.MissingField(_f):
					return "missing_field"
				AuthError.SessionExpired(_e):
					return "session_expired"
				AuthError.TimedOut(_t):
					return "timed_out"
	return "unreachable"

func test_backend_name() -> void:
	Expect.that(_backend.backend_name()).to_equal("null")

func test_never_available() -> void:
	Expect.that(_backend.is_available(Provider.GOOGLE)).to_be_false()
	Expect.that(_backend.is_available(Provider.APPLE)).to_be_false()
	Expect.that(_backend.is_available(Provider.EMAIL_PASSWORD)).to_be_false()

func test_never_configured_even_after_configure() -> void:
	_backend.configure(ProviderConfig.Google("w", "i", "d"))
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_false()

func test_sign_in_resolves_unavailable() -> void:
	var result: CredentialResult = await _backend.sign_in(Provider.GOOGLE)
	Expect.that(_credential_failure_name(result)).to_equal("unavailable")

func test_sign_in_silent_resolves_unavailable() -> void:
	var result: CredentialResult = await _backend.sign_in_silent(Provider.APPLE)
	Expect.that(_credential_failure_name(result)).to_equal("unavailable")

func test_sign_out_succeeds() -> void:
	var result: CompletionResult = await _backend.sign_out(Provider.GOOGLE)
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok")

func test_storage_operations_fail_with_storage_error() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var stored: CompletionResult = await _backend.store_session(
			AuthSession.new("a", "r", Provider.GOOGLE, raw, extras))
	var described: String = ""
	match stored:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(error):
			match error:
				AuthError.Storage(_detail):
					described = "storage"
				AuthError.Cancelled, AuthError.NoCredential:
					described = "other"
				AuthError.Unavailable(_p):
					described = "other"
				AuthError.Configuration(_d):
					described = "other"
				AuthError.RequestFailed(_s, _b):
					described = "other"
				AuthError.InvalidResponse(_i):
					described = "other"
				AuthError.MissingField(_f):
					described = "other"
				AuthError.SessionExpired(_e):
					described = "other"
				AuthError.TimedOut(_t):
					described = "other"
	Expect.that(described).to_equal("storage")
	Expect.that(_backend.has_stored_session()).to_be_false()

func test_clear_stored_session_succeeds() -> void:
	var result: CompletionResult = await _backend.clear_stored_session()
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok")
