namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

class_name AuthSubsystemTests
extends RefCounted
uses Test

var _auth: AuthSubsystem

func before_each() -> void:
	_auth = AuthSubsystem.new(FoundryKitLog.new("test").child("auth"))

func _session_failure_name(result: SessionResult) -> String:
	match result:
		SessionResult.Success(_session):
			return "unexpected_success"
		SessionResult.Failure(error):
			return _error_name(error)
	return "unreachable"

func _error_name(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(_p):
			return "unavailable"
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

func test_no_session_initially() -> void:
	Expect.that(_auth.has_session()).to_be_false()
	Expect.that(_auth.access_token()).to_equal("")

func test_availability_follows_the_backend() -> void:
	Expect.that(_auth.is_available(Provider.GOOGLE)).to_be_false()
	Expect.that(_auth.is_configured(Provider.GOOGLE)).to_be_false()

func test_configure_is_accepted_without_error() -> void:
	_auth.configure(ProviderConfig.Google("w", "i", "d"))
	Expect.that(_auth.is_configured(Provider.GOOGLE)).to_be_false()

func test_sign_in_surfaces_the_backend_unavailability() -> void:
	Expect.that(_session_failure_name(await _auth.sign_in(Provider.GOOGLE))).to_equal("unavailable")

func test_sign_in_silent_surfaces_the_backend_unavailability() -> void:
	Expect.that(_session_failure_name(await _auth.sign_in_silent(Provider.APPLE))) \
		.to_equal("unavailable")

func test_refresh_reports_missing_backend_and_restore_reports_storage() -> void:
	# refresh_session has no endpoint to call; restore_session delegates to the backend,
	# which has no secure storage. Two different absences, two different errors.
	Expect.that(_session_failure_name(await _auth.refresh_session())).to_equal("configuration")
	Expect.that(_session_failure_name(await _auth.restore_session())).to_equal("storage")

func test_valid_access_token_reports_missing_backend_configuration() -> void:
	var result: TokenResult = await _auth.valid_access_token()
	var described: String = ""
	match result:
		TokenResult.Success(_token):
			described = "unexpected_success"
		TokenResult.Failure(error):
			described = _error_name(error)
	Expect.that(described).to_equal("configuration")

func test_sign_out_and_clear_session_succeed_with_no_session() -> void:
	var signed_out: CompletionResult = await _auth.sign_out(Provider.GOOGLE)
	var cleared: CompletionResult = await _auth.clear_session()
	var described: String = ""
	match signed_out:
		CompletionResult.Success:
			match cleared:
				CompletionResult.Success:
					described = "both_ok"
				CompletionResult.Failure(_e):
					described = "clear_failed"
		CompletionResult.Failure(_error):
			described = "sign_out_failed"
	Expect.that(described).to_equal("both_ok")
