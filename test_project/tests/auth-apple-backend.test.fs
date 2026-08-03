namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

class_name AppleAuthBackendTests
extends RefCounted
uses Test

const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

var _native: FakeGoogleNative
var _backend: AppleAuthBackend

func before_each() -> void:
	_native = FakeGoogleNative.new()
	_backend = AppleAuthBackend.new(FoundryKitLog.new("test"), _native)

func after_each() -> void:
	_native.free()

func _configure() -> void:
	_backend.configure(ProviderConfig.Google("web-id", "ios-id", "desktop-id"))

func _describe(result: CredentialResult) -> String:
	match result:
		CredentialResult.Success(credential):
			return "ok:%s:%s" % [_provider_name(Credential.provider_of(credential)),
					Credential.subject_of(credential)]
		CredentialResult.Failure(error):
			return "fail:%s" % _error_name(error)
	return "unreachable"

func _provider_name(provider: Provider) -> String:
	match provider:
		Provider.GOOGLE:
			return "google"
		Provider.APPLE:
			return "apple"
		Provider.EMAIL_PASSWORD:
			return "email_password"
	return "unreachable"

func _error_name(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(_provider):
			return "unavailable"
		AuthError.Configuration(_detail):
			return "configuration"
		AuthError.Storage(_detail):
			return "storage"
		AuthError.RequestFailed(_status, _body):
			return "request_failed"
		AuthError.InvalidResponse(_detail):
			return "invalid_response"
		AuthError.MissingField(_field):
			return "missing_field"
		AuthError.SessionExpired(_expired_at):
			return "session_expired"
		AuthError.TimedOut(_elapsed_seconds):
			return "timed_out"
	return "unreachable"

func _describe_completion(result: CompletionResult) -> String:
	match result:
		CompletionResult.Success:
			return "ok"
		CompletionResult.Failure(error):
			return "fail:%s" % _error_name(error)
	return "unreachable"

func _describe_session(result: SessionResult) -> String:
	match result:
		SessionResult.Success(_session):
			return "ok"
		SessionResult.Failure(error):
			return "fail:%s" % _error_name(error)
	return "unreachable"

func test_backend_name() -> void:
	Expect.that(_backend.backend_name()).to_equal("apple")

func test_unconfigured_is_not_configured() -> void:
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_false()

func test_configure_passes_the_web_client_id_to_the_native() -> void:
	_configure()
	Expect.that(_native.configured_web_client_id).to_equal("web-id")
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_true()

func test_only_google_is_available() -> void:
	Expect.that(_backend.is_available(Provider.GOOGLE)).to_be_true()
	Expect.that(_backend.is_available(Provider.APPLE)).to_be_false()
	Expect.that(_backend.is_available(Provider.EMAIL_PASSWORD)).to_be_false()

func test_sign_in_success_yields_a_google_credential() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_success(_native.last_request_token, _TOKEN, "a@b.c", "Ada")
	Expect.that(_describe(await pending)).to_equal("ok:google:user-123")
	Expect.that(_native.interactive_call_count).to_equal(1)

func test_sign_in_silent_uses_the_silent_native_entry_point() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in_silent(Provider.GOOGLE)
	_native.emit_success(_native.last_request_token, _TOKEN, "a@b.c", "Ada")
	Expect.that(_describe(await pending)).to_equal("ok:google:user-123")
	Expect.that(_native.silent_call_count).to_equal(1)
	Expect.that(_native.interactive_call_count).to_equal(0)

func test_each_sign_in_uses_a_fresh_correlation_token() -> void:
	_configure()
	var first_pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var first_token: String = _native.last_request_token
	_native.emit_success(first_token, _TOKEN, "a@b.c", "Ada")
	await first_pending
	var second_pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var second_token: String = _native.last_request_token
	_native.emit_success(second_token, _TOKEN, "a@b.c", "Ada")
	await second_pending
	Expect.that(first_token.is_empty()).to_be_false()
	Expect.that(first_token != second_token).to_be_true()

## A reply carrying an earlier request's token must not settle the current one — that is
## the whole point of generating a fresh token per call.
func test_a_stale_token_emission_does_not_settle_the_current_request() -> void:
	_configure()
	var first_pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var stale_token: String = _native.last_request_token
	_native.emit_success(stale_token, _TOKEN, "a@b.c", "Ada")
	await first_pending
	var second_pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var live_token: String = _native.last_request_token
	_native.emit_failure(stale_token, 9, "late reply from the abandoned request")
	_native.emit_success(live_token, _TOKEN, "c@d.e", "Grace")
	Expect.that(_describe(await second_pending)).to_equal("ok:google:user-123")

func test_native_failure_maps_through_auth_error() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_failure(_native.last_request_token, 4, "boom")
	Expect.that(_describe(await pending)).to_equal("fail:request_failed")

func test_a_success_without_an_id_token_reports_the_missing_field() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_success(_native.last_request_token, "", "a@b.c", "Ada")
	Expect.that(_describe(await pending)).to_equal("fail:missing_field")

func test_apple_and_email_providers_are_unavailable_in_this_epic() -> void:
	_configure()
	Expect.that(_describe(await _backend.sign_in(Provider.APPLE))).to_equal("fail:unavailable")
	Expect.that(_describe(await _backend.sign_in(Provider.EMAIL_PASSWORD))) \
			.to_equal("fail:unavailable")
	Expect.that(_native.interactive_call_count).to_equal(0)

## The headless suite runs with no native binaries, so this is the production-shaped path:
## a missing binary must resolve exactly like an unsupported platform, promptly.
func test_without_the_native_class_sign_in_resolves_unavailable_without_hanging() -> void:
	var bare: AppleAuthBackend = AppleAuthBackend.new(FoundryKitLog.new("test"))
	bare.configure(ProviderConfig.Google("web-id", "ios-id", "desktop-id"))
	Expect.that(bare.is_available(Provider.GOOGLE)).to_be_false()
	Expect.that(bare.is_configured(Provider.GOOGLE)).to_be_false()
	Expect.that(_describe(await bare.sign_in(Provider.GOOGLE))).to_equal("fail:unavailable")
	Expect.that(_describe(await bare.sign_in_silent(Provider.GOOGLE))).to_equal("fail:unavailable")
	Expect.that(_describe_completion(await bare.sign_out(Provider.GOOGLE))).to_equal("ok")

func test_sign_out_reaches_the_native_and_succeeds() -> void:
	_configure()
	Expect.that(_describe_completion(await _backend.sign_out(Provider.GOOGLE))).to_equal("ok")
	Expect.that(_native.last_request_token.is_empty()).to_be_false()

func test_storage_is_unavailable_until_keychain_lands() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var stored: CompletionResult = await _backend.store_session(
			AuthSession.new("a", "r", Provider.GOOGLE, raw, extras))
	Expect.that(_describe_completion(stored)).to_equal("fail:storage")
	Expect.that(_describe_session(await _backend.restore_session())).to_equal("fail:storage")
	Expect.that(_backend.has_stored_session()).to_be_false()
	Expect.that(_describe_completion(await _backend.clear_stored_session())).to_equal("ok")
