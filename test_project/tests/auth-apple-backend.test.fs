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
var _secure_store: FakeSecureStore
var _backend: AppleAuthBackend

func before_each() -> void:
	_native = FakeGoogleNative.new()
	_secure_store = FakeSecureStore.new()
	_backend = AppleAuthBackend.new(FoundryKitLog.new("test"), _native, _secure_store)

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
		SessionResult.Success(session):
			return "ok:%s:%s" % [session.access_token, session.refresh_token]
		SessionResult.Failure(error):
			return "fail:%s" % _error_name(error)
	return "unreachable"

func _session_from(result: SessionResult) -> AuthSession:
	match result:
		SessionResult.Success(session):
			return session
		SessionResult.Failure(_error):
			Expect.that(false).to_be_true()
	return AuthSession.new()

func _stored_record_from(bytes: PackedByteArray) -> StoredSession:
	match StoredSession.from_bytes(bytes):
		StoredSessionOutcome.Parsed(record):
			return record
		StoredSessionOutcome.Malformed(_detail):
			Expect.that(false).to_be_true()
		StoredSessionOutcome.VersionUnsupported(_version):
			Expect.that(false).to_be_true()
	return StoredSession.new()

func _session_with_every_field() -> AuthSession:
	var raw: Dictionary[String, Variant] = {
		"profile": {"display_name": "Ada"},
		"issued_at": 1720000000.125,
	}
	var extras: Dictionary[String, Variant] = {
		"scope": "openid profile",
		"locale": "fr-CA",
	}
	return AuthSession.new("access-a", "refresh-r", Provider.APPLE, raw, extras)

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

func test_an_unrecognised_native_code_maps_through_auth_error() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_failure(_native.last_request_token, 99, "boom")
	Expect.that(_describe(await pending)).to_equal("fail:request_failed")

func test_the_generic_native_code_maps_through_auth_error() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_failure(_native.last_request_token, 3, "Sign-in failed.")
	Expect.that(_describe(await pending)).to_equal("fail:request_failed")

## The native's own vocabulary — cancellation, no stored credential, unavailable — must
## survive the crossing instead of collapsing into a request failure.
func test_the_native_cancellation_code_maps_to_cancelled() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_failure(_native.last_request_token, 0, "The player cancelled the sign-in flow.")
	Expect.that(_describe(await pending)).to_equal("fail:cancelled")

func test_the_native_no_credential_code_maps_to_no_credential() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in_silent(Provider.GOOGLE)
	_native.emit_failure(_native.last_request_token, 1, "Sign-in returned no credential.")
	Expect.that(_describe(await pending)).to_equal("fail:no_credential")

func test_the_native_unavailable_code_maps_to_unavailable() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_failure(_native.last_request_token, 2, "Sign-in is unavailable.")
	Expect.that(_describe(await pending)).to_equal("fail:unavailable")

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
	Expect.that(_describe_completion(await bare.store_session(
			AuthSession.new("a", "r"), "https://api.example.com"))).to_equal("fail:storage")
	Expect.that(_describe_session(await bare.restore_session(
			"https://api.example.com"))).to_equal("fail:storage")
	Expect.that(bare.has_stored_session()).to_be_false()
	Expect.that(_describe_completion(await bare.clear_stored_session())).to_equal("ok")

func test_sign_out_reaches_the_native_and_succeeds() -> void:
	_configure()
	Expect.that(_describe_completion(await _backend.sign_out(Provider.GOOGLE))).to_equal("ok")
	Expect.that(_native.last_request_token.is_empty()).to_be_false()

func test_store_session_binds_the_record_to_the_supplied_origin() -> void:
	var session: AuthSession = _session_with_every_field()
	var result: CompletionResult = await _backend.store_session(
			session, "https://issuer.example.com:8443")
	Expect.that(_describe_completion(result)).to_equal("ok")
	Expect.that(_secure_store.store_count).to_equal(1)
	var record: StoredSession = _stored_record_from(_secure_store.last_stored_bytes)
	Expect.that(record.origin).to_equal("https://issuer.example.com:8443")
	Expect.that(record.access_token).to_equal(session.access_token)
	Expect.that(record.refresh_token).to_equal(session.refresh_token)
	Expect.that(record.provider).to_equal(session.provider)
	Expect.that(record.raw).to_equal(session.raw)
	Expect.that(record.extras).to_equal(session.extras)

## Both assertions are security properties. The first mutation check removes the origin
## comparison and must fail here by returning `ok:foreign-access:foreign-refresh`; the
## second removes the erase and must fail on `erase_count`.
func test_restore_session_refuses_and_erases_a_foreign_origin() -> void:
	var foreign: StoredSession = StoredSession.from_session(
			AuthSession.new("foreign-access", "foreign-refresh"),
			"https://api-a.example.com")
	_secure_store.next_load_outcome = SecureLoadOutcome.Loaded(foreign.to_bytes())
	var result: SessionResult = await _backend.restore_session("https://api-b.example.com")
	Expect.that(_describe_session(result)).to_equal("fail:storage")
	Expect.that(_secure_store.erase_count).to_equal(1)

func test_restore_session_with_a_matching_origin_preserves_every_field() -> void:
	var original: AuthSession = _session_with_every_field()
	var record: StoredSession = StoredSession.from_session(
			original, "https://api.example.com")
	_secure_store.next_load_outcome = SecureLoadOutcome.Loaded(record.to_bytes())
	var restored: AuthSession = _session_from(
			await _backend.restore_session("https://api.example.com"))
	Expect.that(restored.access_token).to_equal(original.access_token)
	Expect.that(restored.refresh_token).to_equal(original.refresh_token)
	Expect.that(restored.provider).to_equal(original.provider)
	Expect.that(restored.raw).to_equal(original.raw)
	Expect.that(restored.extras).to_equal(original.extras)
	Expect.that(_secure_store.erase_count).to_equal(0)

func test_restore_session_erases_a_malformed_record() -> void:
	_secure_store.next_load_outcome = SecureLoadOutcome.Loaded(
			PackedByteArray([0xFF, 0x00]))
	Expect.that(_describe_session(
			await _backend.restore_session("https://api.example.com"))).to_equal("fail:storage")
	Expect.that(_secure_store.erase_count).to_equal(1)

func test_restore_session_erases_an_unsupported_record_version() -> void:
	var bytes: PackedByteArray = JSON.stringify({
		"schema_version": 99,
		"origin": "https://api.example.com",
	}).to_utf8_buffer()
	_secure_store.next_load_outcome = SecureLoadOutcome.Loaded(bytes)
	Expect.that(_describe_session(
			await _backend.restore_session("https://api.example.com"))).to_equal("fail:storage")
	Expect.that(_secure_store.erase_count).to_equal(1)

func test_restore_session_reports_a_store_load_failure_without_erasing() -> void:
	_secure_store.next_load_outcome = SecureLoadOutcome.Failed("Keychain locked")
	Expect.that(_describe_session(
			await _backend.restore_session("https://api.example.com"))).to_equal("fail:storage")
	Expect.that(_secure_store.erase_count).to_equal(0)

func test_has_stored_session_reflects_successful_store_and_clear_operations() -> void:
	await _backend.store_session(
			AuthSession.new("a", "r"), "https://api.example.com")
	Expect.that(_backend.has_stored_session()).to_be_true()
	await _backend.clear_stored_session()
	Expect.that(_backend.has_stored_session()).to_be_false()

func test_has_stored_session_is_false_when_the_store_is_unavailable() -> void:
	_secure_store.available = false
	var record: StoredSession = StoredSession.from_session(
			AuthSession.new("a", "r"), "https://api.example.com")
	_secure_store.next_load_outcome = SecureLoadOutcome.Loaded(record.to_bytes())
	Expect.that(_backend.has_stored_session()).to_be_false()

func test_clear_stored_session_delegates_to_the_store() -> void:
	Expect.that(_describe_completion(await _backend.clear_stored_session())).to_equal("ok")
	Expect.that(_secure_store.erase_count).to_equal(1)
