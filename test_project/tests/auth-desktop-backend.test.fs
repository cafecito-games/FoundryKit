namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

## Covers the desktop OAuth flow end to end: browser hand-off, loopback callback, `state`
## verification and token exchange.
##
## The browser and the token endpoint are doubles; the loopback listener is the real one,
## driven over a real socket to 127.0.0.1 exactly as the existing listener suite does. The
## socket is half of what this flow is, and a faked listener would leave the interesting
## part — that the callback reaches the backend at all — unproven.
##
## Two assertions here exist because their absence is invisible: a `state` mismatch must
## leave [member FakeHttpClient.send_count] at zero, and every exit path must leave the
## listener's port released.
class_name AuthDesktopBackendTests
extends RefCounted
uses Test

const _DESKTOP_CLIENT_ID: String = "desktop-client-id.apps.googleusercontent.com"
const _WEB_CLIENT_ID: String = "web-client-id.apps.googleusercontent.com"
const _IOS_CLIENT_ID: String = "ios-client-id.apps.googleusercontent.com"

## An unsigned JWT carrying `sub`, `email` and `name`. Signatures are the backend's to
## verify, so the fixture needs none.
const _ID_TOKEN: String = (
		"eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9"
		+ ".eyJzdWIiOiJ1c2VyLTEyMyIsImVtYWlsIjoiYWRhQGV4YW1wbGUuY29tIiwibmFtZSI6IkFkYSBMb3Zl"
		+ "bGFjZSIsImV4cCI6MTc1MDAwMDAwMH0"
		+ ".sig")

## Long enough that a slow CI frame cannot turn a delivered callback into a timeout.
const _GENEROUS_TIMEOUT_SECONDS: float = 10.0

## Short enough that the watchdog test finishes promptly, long enough that it cannot fire
## before the callback would have had a chance to arrive.
const _BRIEF_TIMEOUT_SECONDS: float = 0.4

## Bounds the connect helper, so a defect surfaces as a failed assertion rather than a
## suite that never finishes.
const _MAX_POLLED_FRAMES: int = 1200

var _log: FoundryKitLog
var _transport: FakeHttpClient
var _browser: FakeBrowser
var _backend: DesktopAuthBackend

## Every listener the injected factory handed out, newest last, so a test can assert on
## the port the flow actually bound.
var _listeners: Array[LoopbackServer] = []

## Holds every client socket a test opened until that test ends. A [StreamPeerTCP] closes
## as soon as its last reference goes, so one left to a local would disconnect before the
## listener ever read the request.
var _clients: Array[StreamPeerTCP] = []

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_transport = FakeHttpClient.new()
	_browser = FakeBrowser.new()
	_listeners = []
	_clients = []
	_backend = _new_backend(_GENEROUS_TIMEOUT_SECONDS)

func after_each() -> void:
	for client: StreamPeerTCP in _clients:
		client.disconnect_from_host()
	_clients = []
	for listener: LoopbackServer in _listeners:
		listener.stop()
	_listeners = []

func _new_backend(timeout_seconds: float) -> DesktopAuthBackend:
	return DesktopAuthBackend.new(
			_log, _transport, _browser.open_url, _track_listener, timeout_seconds)

## The injected listener factory: a real [LoopbackServer], recorded so tests can inspect it.
func _track_listener() -> LoopbackServer:
	var listener: LoopbackServer = LoopbackServer.new(_log)
	_listeners.append(listener)
	return listener

func _configure() -> void:
	_backend.configure(
			ProviderConfig.Google(_WEB_CLIENT_ID, _IOS_CLIENT_ID, _DESKTOP_CLIENT_ID))

func _last_listener() -> LoopbackServer:
	return _listeners[_listeners.size() - 1]

## Delivers one OAuth callback to the listener the flow under way is waiting on.
##
## Returns having written the request and without awaiting anything afterwards, so the
## caller reaches its own `await` on the pending sign-in in the same frame.
async func _deliver(query: String) -> void:
	var listener: LoopbackServer = _last_listener()
	var client: StreamPeerTCP = StreamPeerTCP.new()
	_clients.append(client)
	client.connect_to_host(LoopbackServer.BIND_ADDRESS, listener.port())
	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree == null:
		return
	var frames: int = 0
	client.poll()
	while client.get_status() == StreamPeerTCP.STATUS_CONNECTING and frames < _MAX_POLLED_FRAMES:
		await tree.process_frame
		frames += 1
		client.poll()
	client.put_data(("GET /?%s HTTP/1.1\r\n\r\n" % query).to_utf8_buffer())

## The `state` the backend put in the URL it handed the browser, ready to echo back.
func _state_sent() -> String:
	return _parameter_of(_query_of(_browser.last_url), "state")

## The body a Google token response carries on success.
func _token_body(id_token: String) -> PackedByteArray:
	return JSON.stringify({
		"access_token": "access-token-value",
		"expires_in": 3599,
		"id_token": id_token,
		"token_type": "Bearer",
	}).to_utf8_buffer()

func _query_of(url: String) -> String:
	var mark: int = url.find("?")
	if mark < 0:
		return ""
	return url.substr(mark + 1)

func _parameter_of(encoded: String, key: String) -> String:
	for pair: String in encoded.split("&", false):
		var separator: int = pair.find("=")
		if separator < 0:
			continue
		if pair.substr(0, separator).uri_decode() == key:
			return pair.substr(separator + 1).replace("+", " ").uri_decode()
	return ""

func _sent_parameter(key: String) -> String:
	return _parameter_of(_transport.last_body.get_string_from_utf8(), key)

func _describe(result: CredentialResult) -> String:
	match result:
		CredentialResult.Success(credential):
			return "ok:%s" % Credential.subject_of(credential)
		CredentialResult.Failure(error):
			return "fail:%s" % _error_name(error)
	return "unreachable"

## Renders a successful Google credential field by field, so `audience` — the field this
## epic fills in and epic B could not — is asserted rather than assumed.
func _google_fields(result: CredentialResult) -> String:
	match result:
		CredentialResult.Success(credential):
			match credential:
				Credential.Google(id_token, email, display_name, audience):
					return "%s|%s|%s|%s" % [id_token, email, display_name, audience]
				Credential.Apple(_identity_token, _authorization_code, _email, _full_name):
					return "apple"
				Credential.EmailPassword(_email, _password):
					return "email_password"
			return "unreachable"
		CredentialResult.Failure(error):
			return "fail:%s" % _error_name(error)
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
		AuthError.RequestFailed(status, _body):
			return "request_failed:%d" % status
		AuthError.InvalidResponse(_detail):
			return "invalid_response"
		AuthError.MissingField(field):
			return "missing_field:%s" % field
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
	Expect.that(_backend.backend_name()).to_equal("desktop")

func test_only_google_is_available() -> void:
	Expect.that(_backend.is_available(Provider.GOOGLE)).to_be_true()
	Expect.that(_backend.is_available(Provider.APPLE)).to_be_false()
	Expect.that(_backend.is_available(Provider.EMAIL_PASSWORD)).to_be_false()

func test_is_configured_requires_a_desktop_client_id() -> void:
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_false()
	_backend.configure(ProviderConfig.Google(_WEB_CLIENT_ID, _IOS_CLIENT_ID, ""))
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_false()
	_configure()
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_true()
	Expect.that(_backend.is_configured(Provider.APPLE)).to_be_false()
	Expect.that(_backend.is_configured(Provider.EMAIL_PASSWORD)).to_be_false()

func test_signing_in_unconfigured_reports_configuration_without_binding_a_port() -> void:
	var result: CredentialResult = await _backend.sign_in(Provider.GOOGLE)
	Expect.that(_describe(result)).to_equal("fail:configuration")
	Expect.that(_listeners.size()).to_equal(0)
	Expect.that(_browser.open_count).to_equal(0)
	Expect.that(_transport.send_count).to_equal(0)

func test_apple_and_email_providers_are_unavailable_in_this_epic() -> void:
	_configure()
	Expect.that(_describe(await _backend.sign_in(Provider.APPLE))).to_equal("fail:unavailable")
	Expect.that(_describe(await _backend.sign_in(Provider.EMAIL_PASSWORD))) \
			.to_equal("fail:unavailable")
	Expect.that(_listeners.size()).to_equal(0)
	Expect.that(_browser.open_count).to_equal(0)

## There is no silent path without a stored session, and session storage is out of scope
## for this epic, so a silent attempt must fail promptly rather than open a browser.
func test_silent_sign_in_is_unavailable_for_every_provider() -> void:
	_configure()
	Expect.that(_describe(await _backend.sign_in_silent(Provider.GOOGLE))) \
			.to_equal("fail:unavailable")
	Expect.that(_describe(await _backend.sign_in_silent(Provider.APPLE))) \
			.to_equal("fail:unavailable")
	Expect.that(_browser.open_count).to_equal(0)
	Expect.that(_listeners.size()).to_equal(0)

func test_the_browser_is_sent_the_authorization_url_for_the_bound_port() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var redirect_uri: String = _last_listener().redirect_uri()
	Expect.that(_browser.open_count).to_equal(1)
	Expect.that(_browser.last_url.begins_with(DesktopAuthBackend.AUTHORIZATION_ENDPOINT)) \
			.to_be_true()
	Expect.that(_parameter_of(_query_of(_browser.last_url), "redirect_uri")) \
			.to_equal(redirect_uri)
	Expect.that(_parameter_of(_query_of(_browser.last_url), "client_id")) \
			.to_equal(_DESKTOP_CLIENT_ID)
	Expect.that(_state_sent().is_empty()).to_be_false()
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	await pending

func test_a_successful_callback_yields_a_google_credential() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	var result: CredentialResult = await pending
	Expect.that(_describe(result)).to_equal("ok:user-123")
	Expect.that(_google_fields(result)).to_equal(
			"%s|ada@example.com|Ada Lovelace|%s" % [_ID_TOKEN, _DESKTOP_CLIENT_ID])

## The audience is the one field epic B had to leave empty, because the native never
## returned it. Here the client ID the code was issued against is known exactly.
func test_the_credential_audience_is_the_desktop_client_id_not_the_web_one() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	var fields: String = _google_fields(await pending)
	Expect.that(fields.ends_with("|" + _DESKTOP_CLIENT_ID)).to_be_true()
	Expect.that(fields.contains(_WEB_CLIENT_ID)).to_be_false()

func test_the_exchange_posts_the_code_and_verifier_to_the_token_endpoint() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var redirect_uri: String = _last_listener().redirect_uri()
	var challenge: String = _parameter_of(_query_of(_browser.last_url), "code_challenge")
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	await pending
	Expect.that(_transport.send_count).to_equal(1)
	Expect.that(_transport.last_method).to_equal("POST")
	Expect.that(_transport.last_url).to_equal(DesktopAuthBackend.TOKEN_ENDPOINT)
	Expect.that(_transport.last_headers.has("Content-Type: application/x-www-form-urlencoded")) \
			.to_be_true()
	Expect.that(_sent_parameter("grant_type")).to_equal("authorization_code")
	Expect.that(_sent_parameter("code")).to_equal("auth-code")
	Expect.that(_sent_parameter("client_id")).to_equal(_DESKTOP_CLIENT_ID)
	Expect.that(_sent_parameter("redirect_uri")).to_equal(redirect_uri)
	# The verifier is the secret the challenge in the authorization URL was derived from.
	# Asserting the relation proves the exchange carries the same PKCE material the browser
	# was sent, without the test having to know how the pair was generated.
	Expect.that(PkcePair.challenge_for(_sent_parameter("code_verifier"))).to_equal(challenge)

## The security assertion this whole issue exists for. A callback whose `state` does not
## match is an attacker's, and exchanging its code defeats the reason `state` is sent at
## all. The outcome alone does not prove it — only `send_count` does.
func test_a_state_mismatch_fails_without_exchanging_the_code() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=attacker-code&state=not-the-state-we-sent")
	Expect.that(_describe(await pending)).to_equal("fail:invalid_response")
	Expect.that(_transport.send_count).to_equal(0)

func test_a_callback_with_no_state_at_all_fails_without_exchanging_the_code() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=attacker-code")
	Expect.that(_describe(await pending)).to_equal("fail:invalid_response")
	Expect.that(_transport.send_count).to_equal(0)

## The player pressing Cancel is an ordinary outcome, not a fault, and a caller that shows
## an error dialog for it has been misinformed by this backend.
func test_an_error_callback_is_a_cancellation() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("error=access_denied&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:cancelled")
	Expect.that(_transport.send_count).to_equal(0)

## `state` is verified on an error callback too, not only on one carrying a code. Any local
## process can send `error=access_denied` to the port; reporting that as a cancellation
## would tell the caller the player pressed Cancel when the player never saw the screen.
func test_an_error_callback_with_a_foreign_state_is_not_a_cancellation() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("error=access_denied&state=not-the-state-we-sent")
	Expect.that(_describe(await pending)).to_equal("fail:invalid_response")

func test_an_error_callback_with_no_state_at_all_is_not_a_cancellation() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("error=access_denied")
	Expect.that(_describe(await pending)).to_equal("fail:invalid_response")

func test_a_callback_without_a_code_reports_the_missing_field() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:missing_field:code")
	Expect.that(_transport.send_count).to_equal(0)

func test_no_callback_within_the_window_times_out() -> void:
	_configure()
	_backend = _new_backend(_BRIEF_TIMEOUT_SECONDS)
	_configure()
	var result: CredentialResult = await _backend.sign_in(Provider.GOOGLE)
	Expect.that(_describe(result)).to_equal("fail:timed_out")
	Expect.that(_transport.send_count).to_equal(0)

func test_a_rejected_exchange_reports_the_status() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(400, "{\"error\":\"invalid_grant\"}".to_utf8_buffer()))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:request_failed:400")

func test_an_unreachable_token_endpoint_reports_a_request_failure() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.TransportFailed("could not resolve the host name"))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:request_failed:0")

func test_a_timed_out_exchange_reports_a_timeout() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.TimedOut(30.0))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:timed_out")

func test_an_unparseable_token_response_reports_an_invalid_response() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, "not json".to_utf8_buffer()))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:invalid_response")

func test_a_token_response_without_an_id_token_reports_the_missing_field() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body("")))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:missing_field:id_token")

## A host with no browser to open cannot show a consent screen, so waiting out the
## watchdog for a callback nobody will send is the wrong answer.
## Stringifying whatever arrives would turn `{"id_token": 123}` into a credential carrying
## "123", which fails much later and somewhere else — at the backend that tries to verify
## it. A token endpoint that answers with a non-string token has answered malformed.
func test_a_non_string_id_token_reports_an_invalid_response() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(
			200, "{\"id_token\":123,\"token_type\":\"Bearer\"}".to_utf8_buffer()))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await pending)).to_equal("fail:invalid_response")

func test_a_browser_that_will_not_open_fails_promptly() -> void:
	_configure()
	_browser.opens = false
	var result: CredentialResult = await _backend.sign_in(Provider.GOOGLE)
	Expect.that(_describe(result)).to_equal("fail:unavailable")
	Expect.that(_browser.open_count).to_equal(1)
	Expect.that(_transport.send_count).to_equal(0)

## The port-leak assertion. This is the exit path where nothing else releases the socket:
## the listener never settles a wait, so only the backend's own stop can free the port.
func test_a_browser_that_will_not_open_releases_the_port() -> void:
	_configure()
	_browser.opens = false
	await _backend.sign_in(Provider.GOOGLE)
	Expect.that(_listeners.size()).to_equal(1)
	Expect.that(_last_listener().port()).to_equal(0)

func test_the_port_is_released_after_a_successful_sign_in() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	await pending
	Expect.that(_last_listener().port()).to_equal(0)

func test_the_port_is_released_after_a_state_mismatch() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=attacker-code&state=not-the-state-we-sent")
	await pending
	Expect.that(_last_listener().port()).to_equal(0)

func test_the_port_is_released_after_a_cancellation() -> void:
	_configure()
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("error=access_denied&state=" + _state_sent().uri_encode())
	await pending
	Expect.that(_last_listener().port()).to_equal(0)

func test_the_port_is_released_after_a_failed_exchange() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.TransportFailed("could not resolve the host name"))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	await pending
	Expect.that(_last_listener().port()).to_equal(0)

func test_the_port_is_released_after_a_timeout() -> void:
	_configure()
	_backend = _new_backend(_BRIEF_TIMEOUT_SECONDS)
	_configure()
	await _backend.sign_in(Provider.GOOGLE)
	Expect.that(_last_listener().port()).to_equal(0)

## One listener serves one wait, so a second attempt must not reuse the first — a listener
## already awaited refuses immediately rather than waiting for a callback.
func test_each_sign_in_binds_a_fresh_listener() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var first: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=auth-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await first)).to_equal("ok:user-123")

	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var second: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	await _deliver("code=second-code&state=" + _state_sent().uri_encode())
	Expect.that(_describe(await second)).to_equal("ok:user-123")
	Expect.that(_listeners.size()).to_equal(2)

## Two sign-ins must not share PKCE material: a verifier reused across attempts turns a
## captured code from one attempt into a usable code in the next.
func test_each_sign_in_uses_fresh_pkce_material() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var first: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var first_state: String = _state_sent()
	var first_challenge: String = _parameter_of(_query_of(_browser.last_url), "code_challenge")
	var first_nonce: String = _parameter_of(_query_of(_browser.last_url), "nonce")
	await _deliver("code=auth-code&state=" + first_state.uri_encode())
	await first

	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var second: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var second_state: String = _state_sent()
	await _deliver("code=second-code&state=" + second_state.uri_encode())
	await second

	Expect.that(first_state == second_state).to_be_false()
	Expect.that(first_challenge
			== _parameter_of(_query_of(_browser.last_url), "code_challenge")).to_be_false()
	Expect.that(first_nonce.is_empty()).to_be_false()
	Expect.that(first_nonce
			== _parameter_of(_query_of(_browser.last_url), "nonce")).to_be_false()

## One sign-in is one OAuth transaction against one client ID. Reading the configuration
## again after the callback would let a reconfiguration between the authorization request
## and the exchange send two different client IDs inside a single transaction, which the
## authorization server rejects — and would stamp the credential with an audience its ID
## token does not name.
func test_reconfiguring_mid_flight_does_not_change_the_attempt_in_progress() -> void:
	_configure()
	_transport.enqueue(HttpOutcome.Answered(200, _token_body(_ID_TOKEN)))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var state: String = _state_sent()
	_backend.configure(ProviderConfig.Google("other-web-id", "other-ios-id", "other-desktop-id"))
	await _deliver("code=auth-code&state=" + state.uri_encode())
	var result: CredentialResult = await pending
	Expect.that(_sent_parameter("client_id")).to_equal(_DESKTOP_CLIENT_ID)
	Expect.that(_google_fields(result).ends_with("|" + _DESKTOP_CLIENT_ID)).to_be_true()

func test_sign_out_succeeds_because_there_is_nothing_native_to_sign_out_of() -> void:
	_configure()
	Expect.that(_describe_completion(await _backend.sign_out(Provider.GOOGLE))).to_equal("ok")

func test_session_storage_is_not_implemented_on_this_backend() -> void:
	Expect.that(_describe_completion(await _backend.store_session(AuthSession.new()))) \
			.to_equal("fail:storage")
	Expect.that(_describe_session(await _backend.restore_session())).to_equal("fail:storage")
	Expect.that(_backend.has_stored_session()).to_be_false()
	Expect.that(_describe_completion(await _backend.clear_stored_session())).to_equal("ok")
