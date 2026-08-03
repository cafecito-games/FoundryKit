namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

## Covers the subsystem that turns a native credential into a session and keeps it usable.
##
## Two families of assertion carry this file. The [member FakeHttpClient.send_count] ones
## bound the 401 retry: a subsystem that retries in a loop satisfies every outcome-based
## assertion here and hangs in production. The signal-count ones bound the announcements:
## a refresh is single-flight, so N joined callers see one rotation, and a subsystem that
## announces per caller instead of per rotation emits N times for one event.
##
## [member _auth] is built exactly as a consumer builds it — production factory, no backend
## configuration — and pins that an unconfigured install still reports
## [code]Configuration[/code]. [member _subsystem] is built through the injection seam.
class_name AuthSubsystemTests
extends RefCounted
uses Test

## exp = 1750000000, comfortably in the past.
const _EXPIRED_TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

const _REFRESH_TOKEN: String = "refresh-token-value"

var _auth: AuthSubsystem

var _log: FoundryKitLog
var _transport: FakeHttpClient
var _config: BackendConfig
var _backend: FakeAuthBackend
var _subsystem: AuthSubsystem

var _tokens_refreshed_count: int = 0
var _last_refreshed_token: String = ""
var _session_expired_count: int = 0
var _last_expiry: String = ""

func before_each() -> void:
	_auth = AuthSubsystem.new(FoundryKitLog.new("test").child("auth"))

	_log = FoundryKitLog.new("test").child("auth")
	_transport = FakeHttpClient.new()
	_config = BackendConfig.new("https://api.example.com")
	_backend = FakeAuthBackend.new()
	_subsystem = AuthSubsystem.new(_log, _transport, _config, _backend)
	_tokens_refreshed_count = 0
	_last_refreshed_token = ""
	_session_expired_count = 0
	_last_expiry = ""
	# Connected methods rather than inline lambdas: a lambda connected to a signal does not
	# reliably observe mutation of a captured variable, which is exactly what these counts do.
	_subsystem.tokens_refreshed.connect(_on_tokens_refreshed)
	_subsystem.session_expired.connect(_on_session_expired)

func _on_tokens_refreshed(session: AuthSession) -> void:
	_tokens_refreshed_count += 1
	_last_refreshed_token = session.access_token

func _on_session_expired(error: AuthError) -> void:
	_session_expired_count += 1
	_last_expiry = _describe_error(error)

# --- The unconfigured install, unchanged ------------------------------------------------

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

func test_an_unconfigured_request_reports_the_missing_configuration() -> void:
	var result: ResponseResult = await _auth.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("fail:configuration")

# --- Credential exchange ----------------------------------------------------------------

func test_a_successful_exchange_returns_and_installs_the_session() -> void:
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	var result: SessionResult = await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal("ok:issued-access-token")
	Expect.that(_subsystem.has_session()).to_be_true()
	Expect.that(_subsystem.access_token()).to_equal("issued-access-token")

func test_the_exchange_posts_the_credential_to_the_configured_path() -> void:
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_transport.last_method).to_equal("POST")
	Expect.that(_transport.last_url).to_equal(_config.url_for(_config.exchange_path))
	# Byte-exact, because this is the wire contract a backend is written against. The keys
	# come out alphabetically because JSON.stringify sorts them.
	Expect.that(_transport.last_body.get_string_from_utf8()).to_equal(
			"{\"audience\":\"client-id\",\"display_name\":\"Player One\"," \
			+ "\"email\":\"player@example.com\",\"id_token\":\"google-id-token\"," \
			+ "\"provider\":\"google\"}")

func test_the_exchange_request_carries_no_authorization_header() -> void:
	# There is no session yet, so there is no bearer credential to present. The credential
	# being exchanged travels in the body.
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_carries_authorization(_transport.last_headers)).to_be_false()

func test_the_exchange_preserves_the_provider_that_issued_the_credential() -> void:
	_backend.credential_result = CredentialResult.Success(_apple_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	var result: SessionResult = await _subsystem.sign_in(Provider.APPLE)
	match result:
		SessionResult.Success(session):
			Expect.that(session.provider).to_equal(Provider.APPLE)
		SessionResult.Failure(_error):
			Expect.that("failure").to_equal("success")

func test_a_silent_sign_in_exchanges_the_same_way() -> void:
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	var result: SessionResult = await _subsystem.sign_in_silent(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal("ok:issued-access-token")
	Expect.that(_transport.send_count).to_equal(1)

func test_a_failed_credential_never_reaches_the_backend() -> void:
	_backend.credential_result = CredentialResult.Failure(AuthError.Cancelled)
	var result: SessionResult = await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal("fail:cancelled")
	Expect.that(_transport.send_count).to_equal(0)
	Expect.that(_subsystem.has_session()).to_be_false()

func test_a_refused_credential_is_a_failed_request_not_an_expired_session() -> void:
	# A 401 from the exchange endpoint means the credential was refused. There is no session
	# to have expired, and reporting SessionExpired would invite a refresh with no token.
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: SessionResult = await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal(
			"fail:request_failed:401:the backend refused the credential")
	Expect.that(_subsystem.has_session()).to_be_false()

func test_an_exchange_without_an_access_token_is_a_missing_field() -> void:
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _json({"token_type": "Bearer"})))
	var result: SessionResult = await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal("fail:missing_field:access_token")
	Expect.that(_subsystem.has_session()).to_be_false()

func test_a_non_string_access_token_is_an_invalid_response() -> void:
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _json({"access_token": 12345})))
	var result: SessionResult = await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal(
			"fail:invalid_response:the exchange response carried a non-string access_token")

func test_an_exchange_that_is_not_json_is_an_invalid_response() -> void:
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, "not json at all".to_utf8_buffer()))
	var result: SessionResult = await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal(
			"fail:invalid_response:the exchange response was not a JSON object")

func test_an_exchange_transport_failure_is_reported_as_it_happened() -> void:
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.TransportFailed("could not resolve the host name"))
	var result: SessionResult = await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_describe_session(result)).to_equal(
			"fail:request_failed:0:could not resolve the host name")

# --- Token validity ---------------------------------------------------------------------

func test_valid_access_token_returns_a_token_that_has_not_expired() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	var result: TokenResult = await _subsystem.valid_access_token()
	Expect.that(_describe_token(result)).to_equal("ok:access-one")
	Expect.that(_transport.send_count).to_equal(0)

func test_valid_access_token_refreshes_an_expired_token() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var result: TokenResult = await _subsystem.valid_access_token()
	Expect.that(_describe_token(result)).to_equal("ok:fresh-access-token")
	Expect.that(_transport.send_count).to_equal(1)
	Expect.that(_transport.last_url).to_equal(_config.url_for(_config.refresh_path))

func test_valid_access_token_without_a_session_reports_the_session_expired() -> void:
	var result: TokenResult = await _subsystem.valid_access_token()
	Expect.that(_describe_token(result)).to_equal("fail:session_expired:0")
	Expect.that(_transport.send_count).to_equal(0)

func test_valid_access_token_reports_a_rejected_refresh_token() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: TokenResult = await _subsystem.valid_access_token()
	Expect.that(_describe_token(result)).to_equal("fail:session_expired:1750000000")
	Expect.that(_subsystem.has_session()).to_be_false()

# --- Explicit refresh -------------------------------------------------------------------

func test_refresh_session_delegates_to_the_session_store() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var result: SessionResult = await _subsystem.refresh_session()
	Expect.that(_describe_session(result)).to_equal("ok:fresh-access-token")
	Expect.that(_transport.last_method).to_equal("POST")
	Expect.that(_transport.last_url).to_equal(_config.url_for(_config.refresh_path))
	Expect.that(_transport.last_body.get_string_from_utf8()).to_equal(
			"{\"refresh_token\":\"%s\"}" % _REFRESH_TOKEN)
	Expect.that(_subsystem.access_token()).to_equal("fresh-access-token")

func test_refresh_session_without_a_session_reports_the_session_expired() -> void:
	var result: SessionResult = await _subsystem.refresh_session()
	Expect.that(_describe_session(result)).to_equal("fail:session_expired:0")
	Expect.that(_transport.send_count).to_equal(0)

# --- Authorized requests ----------------------------------------------------------------

func test_a_request_presents_the_access_token() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, "{}".to_utf8_buffer()))
	var result: ResponseResult = await _subsystem.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("ok:200")
	Expect.that(_transport.last_method).to_equal("GET")
	Expect.that(_transport.last_url).to_equal(_config.url_for("/me"))
	Expect.that(_bearer_of(_transport.last_headers)).to_equal("access-one")
	Expect.that(_transport.send_count).to_equal(1)

func test_a_request_that_is_not_refused_is_never_retried() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, "{}".to_utf8_buffer()))
	await _subsystem.request(HttpMethod.POST, "/score", {"value": 10})
	Expect.that(_transport.send_count).to_equal(1)

func test_a_401_refreshes_and_retries_exactly_once() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	_transport.enqueue(HttpOutcome.Answered(200, "{}".to_utf8_buffer()))
	var result: ResponseResult = await _subsystem.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("ok:200")
	Expect.that(_transport.send_count).to_equal(3)
	Expect.that(_bearer_of(_transport.last_headers)).to_equal("fresh-access-token")

func test_a_second_401_after_refresh_is_session_expired_not_a_loop() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: ResponseResult = await _subsystem.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("fail:session_expired")
	Expect.that(_transport.send_count).to_equal(3)
	Expect.that(_subsystem.has_session()).to_be_false()
	Expect.that(_session_expired_count).to_equal(1)

func test_a_401_whose_refresh_is_refused_makes_no_retry() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: ResponseResult = await _subsystem.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("fail:session_expired")
	Expect.that(_transport.send_count).to_equal(2)

func test_a_401_with_no_session_reaches_no_refresh() -> void:
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: ResponseResult = await _subsystem.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("fail:session_expired")
	Expect.that(_transport.send_count).to_equal(1)

func test_a_retry_is_abandoned_when_the_session_was_replaced_mid_request() -> void:
	# The first attempt was authorized as one player. If a second sign-in lands while the
	# refresh is settling, replaying would authorize the same mutating request as the player
	# who signed in instead — an operation asked for on one account executing against another.
	await _install_session("access-a", "refresh-a")
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var body: Dictionary[String, Variant] = {"amount": 100}
	var pending: Coroutine[ResponseResult] = _subsystem.request(
			HttpMethod.POST, "/transfer", body)
	# The refresh settles one message-queue turn later, so the request is suspended here and
	# a second sign-in can land before it replays.
	await _install_session("access-b", "refresh-b")
	Expect.that(_describe_response(await pending)).to_equal("fail:session_expired")
	Expect.that(_transport.send_count).to_equal(2)
	Expect.that(_subsystem.access_token()).to_equal("access-b")
	Expect.that(_session_expired_count).to_equal(0)

func test_a_request_body_is_sent_as_json() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, "{}".to_utf8_buffer()))
	await _subsystem.request(HttpMethod.POST, "/score", {"value": 10})
	Expect.that(_transport.last_body.get_string_from_utf8()).to_equal("{\"value\":10}")

# --- Announcements ----------------------------------------------------------------------

func test_a_rotation_announces_the_refreshed_tokens_once() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	await _subsystem.valid_access_token()
	Expect.that(_tokens_refreshed_count).to_equal(1)
	Expect.that(_last_refreshed_token).to_equal("fresh-access-token")

func test_concurrent_callers_announce_one_rotation_between_them() -> void:
	# One round, one rotation, one announcement. A subsystem that announces per caller
	# instead of per rotation emits three times here — and the store, which counts rotations
	# rather than emitting, cannot tell it apart on its own.
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var first: Coroutine[TokenResult] = _subsystem.valid_access_token()
	var second: Coroutine[TokenResult] = _subsystem.valid_access_token()
	var third: Coroutine[TokenResult] = _subsystem.valid_access_token()
	Expect.that(_describe_token(await first)).to_equal("ok:fresh-access-token")
	Expect.that(_describe_token(await second)).to_equal("ok:fresh-access-token")
	Expect.that(_describe_token(await third)).to_equal("ok:fresh-access-token")
	Expect.that(_transport.send_count).to_equal(1)
	Expect.that(_tokens_refreshed_count).to_equal(1)

func test_two_rotations_are_announced_twice() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	await _subsystem.refresh_session()
	_transport.enqueue(HttpOutcome.Answered(200, _json({
		"access_token": "second-access-token",
		"refresh_token": "second-refresh-token",
	})))
	await _subsystem.refresh_session()
	Expect.that(_tokens_refreshed_count).to_equal(2)
	Expect.that(_last_refreshed_token).to_equal("second-access-token")

func test_a_refresh_that_rotates_nothing_announces_nothing() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, _json({
		"access_token": _EXPIRED_TOKEN,
		"refresh_token": _REFRESH_TOKEN,
	})))
	await _subsystem.refresh_session()
	Expect.that(_tokens_refreshed_count).to_equal(0)

func test_the_announced_session_is_a_copy() -> void:
	# The listener is handed a mutable AuthSession. If it were the store's own instance, a
	# listener that changed a token on it would move the store off the session it holds.
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	_subsystem.tokens_refreshed.connect(_tamper_with)
	await _subsystem.refresh_session()
	Expect.that(_tokens_refreshed_count).to_equal(1)
	Expect.that(_subsystem.access_token()).to_equal("fresh-access-token")

func _tamper_with(session: AuthSession) -> void:
	session.access_token = "tampered"

func test_a_lapsed_session_is_announced_once() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	await _subsystem.valid_access_token()
	Expect.that(_session_expired_count).to_equal(1)
	Expect.that(_last_expiry).to_equal("session_expired:1750000000")
	# Asking again finds no session and must not announce the same loss a second time.
	await _subsystem.refresh_session()
	Expect.that(_session_expired_count).to_equal(1)
	Expect.that(_transport.send_count).to_equal(1)

func test_concurrent_callers_announce_one_lapse_between_them() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var first: Coroutine[TokenResult] = _subsystem.valid_access_token()
	var second: Coroutine[TokenResult] = _subsystem.valid_access_token()
	await first
	await second
	Expect.that(_transport.send_count).to_equal(1)
	Expect.that(_session_expired_count).to_equal(1)

func test_a_refresh_that_merely_failed_announces_nothing() -> void:
	# An unreachable backend is not a lapsed session: the session is still held and still
	# usable the moment the host comes back.
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.TimedOut(9.5))
	await _subsystem.refresh_session()
	Expect.that(_session_expired_count).to_equal(0)
	Expect.that(_subsystem.has_session()).to_be_true()

func test_signing_out_announces_nothing() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	await _subsystem.sign_out(Provider.GOOGLE)
	Expect.that(_subsystem.has_session()).to_be_false()
	Expect.that(_session_expired_count).to_equal(0)

func test_a_refresh_after_signing_out_announces_nothing() -> void:
	# A player who signed out has not had a session lapse. Refreshing afterwards finds none,
	# which is the state they asked for, and announcing it would report a deliberate
	# sign-out as an unsolicited expiry.
	await _install_session("access-one", _REFRESH_TOKEN)
	await _subsystem.sign_out(Provider.GOOGLE)
	await _subsystem.refresh_session()
	Expect.that(_session_expired_count).to_equal(0)
	Expect.that(_transport.send_count).to_equal(0)

func test_clearing_the_session_announces_nothing() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	await _subsystem.clear_session()
	Expect.that(_subsystem.has_session()).to_be_false()
	Expect.that(_session_expired_count).to_equal(0)

func test_a_lapse_after_a_new_sign_in_is_announced_again() -> void:
	await _install_session(_EXPIRED_TOKEN, _REFRESH_TOKEN)
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	await _subsystem.valid_access_token()
	Expect.that(_session_expired_count).to_equal(1)

	await _install_session(_EXPIRED_TOKEN, "second-refresh-token")
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	await _subsystem.valid_access_token()
	Expect.that(_session_expired_count).to_equal(2)

# --- Session replacement mid-call ---------------------------------------------------------

func test_a_sign_in_completing_after_a_sign_out_does_not_resurrect_the_session() -> void:
	# The player is in front of the native sheet when the game signs them out. The session
	# the sheet eventually produces must not put them back where they asked not to be.
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_backend.suspends = true
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	var pending: Coroutine[SessionResult] = _subsystem.sign_in(Provider.GOOGLE)
	await _subsystem.sign_out(Provider.GOOGLE)
	_backend.release()
	Expect.that(_describe_session(await pending)).to_equal("fail:cancelled")
	Expect.that(_subsystem.has_session()).to_be_false()

func test_a_restore_completing_after_a_sign_out_does_not_resurrect_the_session() -> void:
	_backend.restore_result = SessionResult.Success(_session("restored-token", _REFRESH_TOKEN))
	_backend.suspends = true
	var pending: Coroutine[SessionResult] = _subsystem.restore_session()
	await _subsystem.sign_out(Provider.GOOGLE)
	_backend.release()
	Expect.that(_describe_session(await pending)).to_equal("fail:cancelled")
	Expect.that(_subsystem.has_session()).to_be_false()

func test_valid_access_token_is_abandoned_when_the_session_is_replaced_mid_refresh() -> void:
	# The store answers a superseded round with whatever it holds now. Handing that token to
	# a caller who asked before the switch would give one player another player's credential.
	await _install_session(_EXPIRED_TOKEN, "refresh-a")
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var pending: Coroutine[TokenResult] = _subsystem.valid_access_token()
	await _install_session("access-b", "refresh-b")
	Expect.that(_describe_token(await pending)).to_equal("fail:session_expired:0")
	Expect.that(_subsystem.access_token()).to_equal("access-b")

func test_refresh_session_is_abandoned_when_the_session_is_replaced_mid_refresh() -> void:
	await _install_session(_EXPIRED_TOKEN, "refresh-a")
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var pending: Coroutine[SessionResult] = _subsystem.refresh_session()
	await _install_session("access-b", "refresh-b")
	Expect.that(_describe_session(await pending)).to_equal("fail:session_expired:0")
	Expect.that(_subsystem.access_token()).to_equal("access-b")

# --- Configuration the exchange needs -----------------------------------------------------

func test_the_configured_web_client_id_becomes_the_exchange_audience() -> void:
	# The native Google backend cannot report the audience, so it leaves it empty and this
	# layer fills it from configuration. A backend that validates it refuses the sign-in
	# otherwise.
	_subsystem.configure(ProviderConfig.Google("web-client-id", "ios-client-id", "desktop-id"))
	_backend.credential_result = CredentialResult.Success(Credential.Google(
			"google-id-token", "player@example.com", "Player One", ""))
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_audience_sent()).to_equal("web-client-id")

func test_an_audience_the_credential_carries_wins_over_configuration() -> void:
	_subsystem.configure(ProviderConfig.Google("web-client-id", "ios-client-id", "desktop-id"))
	_backend.credential_result = CredentialResult.Success(_google_credential())
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_audience_sent()).to_equal("client-id")

func test_configuring_another_provider_does_not_clear_the_google_audience() -> void:
	_subsystem.configure(ProviderConfig.Google("web-client-id", "ios-client-id", "desktop-id"))
	_subsystem.configure(ProviderConfig.Apple("service-id", "https://example.com/callback"))
	_backend.credential_result = CredentialResult.Success(Credential.Google(
			"google-id-token", "player@example.com", "Player One", ""))
	_transport.enqueue(HttpOutcome.Answered(200, _issued_session_json()))
	await _subsystem.sign_in(Provider.GOOGLE)
	Expect.that(_audience_sent()).to_equal("web-client-id")

# --- Storage delegation -----------------------------------------------------------------

func test_restore_session_installs_what_the_backend_returned() -> void:
	_backend.restore_result = SessionResult.Success(_session("restored-token", _REFRESH_TOKEN))
	var result: SessionResult = await _subsystem.restore_session()
	Expect.that(_describe_session(result)).to_equal("ok:restored-token")
	Expect.that(_subsystem.has_session()).to_be_true()
	Expect.that(_subsystem.access_token()).to_equal("restored-token")

func test_a_failed_restore_leaves_the_subsystem_without_a_session() -> void:
	_backend.restore_result = SessionResult.Failure(AuthError.Storage("nothing stored"))
	var result: SessionResult = await _subsystem.restore_session()
	Expect.that(_describe_session(result)).to_equal("fail:storage")
	Expect.that(_subsystem.has_session()).to_be_false()

func test_signing_out_reaches_the_native_backend() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	await _subsystem.sign_out(Provider.APPLE)
	Expect.that(_backend.sign_out_count).to_equal(1)

func test_clearing_the_session_reaches_the_native_storage() -> void:
	await _install_session("access-one", _REFRESH_TOKEN)
	await _subsystem.clear_session()
	Expect.that(_backend.clear_count).to_equal(1)

func test_the_request_guard_is_reachable() -> void:
	Expect.that(_subsystem.request_guard().is_active()).to_be_false()

# --- Helpers ----------------------------------------------------------------------------

## Installs a session without a single transport call, so every send count in a test
## describes only the calls that test is about.
async func _install_session(access_token: String, refresh_token: String) -> void:
	_backend.restore_result = SessionResult.Success(_session(access_token, refresh_token))
	await _subsystem.restore_session()

func _session(access_token: String, refresh_token: String) -> AuthSession:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	return AuthSession.new(access_token, refresh_token, Provider.GOOGLE, raw, extras)

func _google_credential() -> Credential:
	return Credential.Google(
			"google-id-token", "player@example.com", "Player One", "client-id")

func _apple_credential() -> Credential:
	return Credential.Apple(
			"apple-identity-token", "apple-authorization-code", "player@example.com", "Player One")

func _issued_session_json() -> PackedByteArray:
	return _json({
		"access_token": "issued-access-token",
		"refresh_token": "issued-refresh-token",
	})

func _fresh_session_json() -> PackedByteArray:
	return _json({
		"access_token": "fresh-access-token",
		"refresh_token": "fresh-refresh-token",
	})

func _json(payload: Dictionary) -> PackedByteArray:
	return JSON.stringify(payload).to_utf8_buffer()

## Returns the audience the most recent request body carried, or an empty string.
func _audience_sent() -> String:
	var parser: JSON = JSON.new()
	if parser.parse(_transport.last_body.get_string_from_utf8()) != OK:
		return ""
	if not (parser.data is Dictionary):
		return ""
	var payload: Dictionary = parser.data
	var audience: Variant = payload.get("audience")
	if not (audience is String):
		return ""
	var sent: String = audience
	return sent

func _carries_authorization(headers: PackedStringArray) -> bool:
	for header: String in headers:
		if header.to_lower().begins_with("authorization:"):
			return true
	return false

## Returns the bearer credential the last request presented, or an empty string.
func _bearer_of(headers: PackedStringArray) -> String:
	for header: String in headers:
		if header.begins_with("Authorization: Bearer "):
			return header.substr("Authorization: Bearer ".length())
	return ""

## Keeps the pre-existing unconfigured assertions phrased exactly as they were.
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

## Renders a result as a stable string, so one assertion covers both the case and its
## payload.
func _describe_session(result: SessionResult) -> String:
	match result:
		SessionResult.Success(session):
			return "ok:" + session.access_token
		SessionResult.Failure(error):
			return "fail:" + _describe_error(error)
	return "unreachable"

func _describe_token(result: TokenResult) -> String:
	match result:
		TokenResult.Success(token):
			return "ok:" + token
		TokenResult.Failure(error):
			return "fail:" + _describe_error(error)
	return "unreachable"

func _describe_response(result: ResponseResult) -> String:
	match result:
		ResponseResult.Success(response):
			return "ok:%d" % response.status_code
		ResponseResult.Failure(error):
			return "fail:" + _error_name(error)
	return "unreachable"

func _describe_error(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(_provider):
			return "unavailable"
		AuthError.Configuration(detail):
			return "configuration:" + detail
		AuthError.Storage(_detail):
			return "storage"
		AuthError.RequestFailed(status, body):
			return "request_failed:%d:%s" % [status, body]
		AuthError.InvalidResponse(detail):
			return "invalid_response:" + detail
		AuthError.MissingField(field):
			return "missing_field:" + field
		AuthError.SessionExpired(expired_at):
			return "session_expired:%d" % expired_at
		AuthError.TimedOut(elapsed_seconds):
			return "timed_out:%s" % elapsed_seconds
	return "unknown"

## A scripted [AuthBackend]: no native class, no platform, no waiting.
##
## Nested rather than a support file because only one head type may live in a `.fs` file
## and nothing outside this suite needs it.
class FakeAuthBackend extends RefCounted uses AuthBackend:

	## What the next interactive or silent sign-in resolves with.
	var credential_result: CredentialResult = CredentialResult.Failure(AuthError.NoCredential)

	## What the next [method restore_session] resolves with.
	var restore_result: SessionResult = SessionResult.Failure(AuthError.Storage("nothing stored"))

	var sign_out_count: int = 0
	var clear_count: int = 0

	## When true, sign-in and restore park until [method release] is called.
	##
	## Native sheets and secure storage take arbitrarily long in production; this is what
	## lets a test land a sign-out in the middle of one.
	var suspends: bool = false

	signal _released()

	## Resumes whatever is parked, one message-queue turn later so a caller that has not yet
	## reached its own await cannot miss the emission.
	func release() -> void:
		_released.emit.call_deferred()

	func configure(_config: ProviderConfig) -> void:
		return

	func is_available(_provider: Provider) -> bool:
		return true

	func is_configured(_provider: Provider) -> bool:
		return true

	async func sign_in(_provider: Provider) -> CredentialResult:
		if suspends:
			await _released
		return credential_result

	async func sign_in_silent(_provider: Provider) -> CredentialResult:
		if suspends:
			await _released
		return credential_result

	async func sign_out(_provider: Provider) -> CompletionResult:
		sign_out_count += 1
		return CompletionResult.Success

	async func store_session(_session: AuthSession) -> CompletionResult:
		return CompletionResult.Success

	async func restore_session() -> SessionResult:
		if suspends:
			await _released
		return restore_result

	func has_stored_session() -> bool:
		return false

	async func clear_stored_session() -> CompletionResult:
		clear_count += 1
		return CompletionResult.Success

	func backend_name() -> String:
		return "fake"
