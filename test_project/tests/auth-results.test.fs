namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name AuthResultsTests
extends RefCounted
uses Test

const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func test_credential_result_matches_both_cases() -> void:
	var ok: CredentialResult = CredentialResult.Success(Credential.Google(_TOKEN, "", "", ""))
	var described: String = ""
	match ok:
		CredentialResult.Success(credential):
			described = "ok:%d" % Credential.provider_of(credential)
		CredentialResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok:0")

func test_session_result_carries_session() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var result: SessionResult = SessionResult.Success(
			AuthSession.new("a", "r", Provider.APPLE, raw, extras))
	var described: String = ""
	match result:
		SessionResult.Success(session):
			described = session.access_token
		SessionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("a")

func test_token_result_carries_token() -> void:
	var result: TokenResult = TokenResult.Success("bearer-abc")
	var described: String = ""
	match result:
		TokenResult.Success(token):
			described = token
		TokenResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("bearer-abc")

func test_response_result_carries_response() -> void:
	var response: AuthResponse = AuthResponse.new()
	response.transport_ok = true
	response.status_code = 204
	var result: ResponseResult = ResponseResult.Success(response)
	var described: int = 0
	match result:
		ResponseResult.Success(value):
			described = value.status_code
		ResponseResult.Failure(_error):
			described = -1
	Expect.that(described).to_equal(204)

func test_completion_result_success_is_payload_less() -> void:
	var result: CompletionResult = CompletionResult.Success
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok")

func test_every_failure_carries_the_error() -> void:
	var result: CompletionResult = CompletionResult.Failure(AuthError.Cancelled)
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(error):
			described = _describe_auth_error(error)
	Expect.that(described).to_equal("cancelled")

## Returns "cancelled" for [constant AuthError.Cancelled] and "other" for every other
## case, listed individually because a bind cannot be combined with multiple patterns.
func _describe_auth_error(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "other"
		AuthError.Unavailable(_provider):
			return "other"
		AuthError.Configuration(_detail):
			return "other"
		AuthError.Storage(_detail):
			return "other"
		AuthError.RequestFailed(_status, _body):
			return "other"
		AuthError.InvalidResponse(_detail):
			return "other"
		AuthError.MissingField(_field):
			return "other"
		AuthError.SessionExpired(_expired_at):
			return "other"
		AuthError.TimedOut(_elapsed_seconds):
			return "other"
	return "other"
