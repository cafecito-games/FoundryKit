namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

## The desktop OAuth 2.0 installed-app flow (RFC 8252) for Google sign-in on Linux and
## Windows, where there is no native provider SDK.
##
## The flow in order: bind a loopback port, build an authorization URL naming it, hand that
## URL to the system browser, wait for the one callback the browser is redirected to,
## compare its [code]state[/code] against the one that was sent, and exchange its code for
## an ID token with the PKCE verifier. None of that is visible to the caller — the whole
## sequence resolves to a single [CredentialResult].
##
## [b]The browser, the loopback listener and the transport are injected[/b], each as an
## optional trailing constructor argument defaulting to production behaviour, so the suite
## drives the flow without opening a browser or reaching the network. That is not only a
## testing convenience: a headless host has no browser, and a test that launched one would
## either fail or, far worse, succeed against a real authorization server.
##
## [b]The [code]state[/code] comparison is what makes a callback trustworthy.[/b] The
## loopback port is reachable by any process on the machine, so a callback arriving on it
## is not by itself evidence that this flow asked for it. A mismatch fails the sign-in
## [i]before[/i] anything else in the callback is acted on: exchanging an attacker's code
## and then reporting a failure would be the exact attack [code]state[/code] exists to
## prevent, and it is invisible to a test that only inspects the returned result.
##
## [b]The listener is stopped on exactly one path[/b] — after [method _run_flow] returns,
## whichever way it ended. A [TCPServer] left listening holds its port for the lifetime of
## the process, and a flow that leaks one per attempt exhausts the ephemeral range for
## every other program on the machine.
##
## Google only, interactive only. Sign in with Apple has no installed-app flow here, and
## there is no silent path without a stored session — session storage is a later epic, so
## both report unavailable rather than pretending to work.
class_name DesktopAuthBackend extends RefCounted
uses AuthBackend

## Google's OAuth 2.0 authorization endpoint for the installed-app flow. Reused unmodified
## from the legacy desktop backend.
const AUTHORIZATION_ENDPOINT: String = "https://accounts.google.com/o/oauth2/v2/auth"

## Google's token endpoint, where an authorization code plus the PKCE verifier becomes an
## ID token.
const TOKEN_ENDPOINT: String = "https://oauth2.googleapis.com/token"

## Bounds the token exchange. Matches [constant HttpClient.DEFAULT_TIMEOUT_SECONDS]: the
## exchange is an ordinary machine-to-machine call, unlike the callback wait, which is
## bounded by how long a player takes to read a consent screen.
const EXCHANGE_TIMEOUT_SECONDS: float = 30.0

## The OAuth scopes requested: enough to resolve identity, nothing more.
const _SCOPE: String = "openid email profile"

## The number of random bytes behind the OpenID Connect nonce.
##
## The nonce is echoed into the ID token, where whoever verifies that token's signature
## checks it. FoundryKit verifies no signatures — see [Jwt] — so it does not verify the
## nonce either; sending a fresh, unguessable one per attempt is this side's obligation.
const _NONCE_BYTE_COUNT: int = 16

const _STORAGE_DETAIL: String = "secure session storage is not implemented on this backend yet"

var _log: FoundryKitLog
var _transport: HttpTransport
var _open_browser: Callable
var _new_listener: Callable
var _callback_timeout_seconds: float
var _config: ProviderConfig = ProviderConfig.EmailPassword

## Builds a backend that exchanges codes over [param transport].
##
## [param open_browser] takes the authorization URL and returns whether it was handed off;
## [param new_listener] returns a fresh, unstarted [LoopbackServer]. Both fall back to
## production behaviour when null, which is what every caller outside a test passes.
## [param callback_timeout_seconds] bounds how long the player has to finish the consent
## screen.
func _init(
		log: FoundryKitLog,
		transport: HttpTransport,
		open_browser: Callable = Callable(),
		new_listener: Callable = Callable(),
		callback_timeout_seconds: float = LoopbackServer.DEFAULT_TIMEOUT_SECONDS) -> void:
	_log = log
	_transport = transport
	_open_browser = open_browser
	if _open_browser.is_null():
		_open_browser = _open_system_browser
	_new_listener = new_listener
	if _new_listener.is_null():
		_new_listener = _new_loopback_server
	_callback_timeout_seconds = callback_timeout_seconds

func backend_name() -> String:
	return "desktop"

func configure(config: ProviderConfig) -> void:
	match config:
		ProviderConfig.Google(_web_client_id, _ios_client_id, _desktop_client_id):
			_config = config
		ProviderConfig.Apple(_service_id, _redirect_uri):
			_log.debug("configure(Apple) ignored: this backend does not serve Apple")
		ProviderConfig.EmailPassword:
			_log.debug("configure(EmailPassword) ignored: not an OAuth provider")

## Returns whether this backend can serve [param provider] here.
##
## Google is always available: this flow needs only a browser and a loopback port, and
## neither is knowable before an attempt is made. A host that turns out to have no browser
## fails that attempt rather than being predicted here.
func is_available(provider: Provider) -> bool:
	match provider:
		Provider.GOOGLE:
			return true
		Provider.APPLE, Provider.EMAIL_PASSWORD:
			return false
	return false

func is_configured(provider: Provider) -> bool:
	match provider:
		Provider.GOOGLE:
			return not _desktop_client_id_of(_config).is_empty()
		Provider.APPLE, Provider.EMAIL_PASSWORD:
			return false
	return false

## Runs one interactive sign-in.
##
## The listener is created here and stopped here, on the single line every outcome of
## [method _run_flow] passes through, so no exit path added later can forget to release the
## port. [method LoopbackServer.stop] is idempotent and safe after the wait has already
## settled, which is what makes one unconditional call correct rather than merely tidy.
async func sign_in(provider: Provider) -> CredentialResult:
	if provider != Provider.GOOGLE:
		return _unavailable(provider)
	if not is_configured(Provider.GOOGLE):
		return CredentialResult.Failure(AuthError.Configuration(
				"no desktop OAuth client ID is configured for Google"))
	var listener: LoopbackServer = _create_listener()
	var result: CredentialResult = await _run_flow(listener)
	listener.stop()
	return result

## Reports unavailable for every provider.
##
## A silent sign-in means presenting a credential the player already granted, and this
## backend keeps nothing between runs — secure storage on Linux and Windows is a later
## epic. Falling back to the interactive flow would open a browser behind a caller that
## asked specifically for no UI.
async func sign_in_silent(provider: Provider) -> CredentialResult:
	return _unavailable(provider)

## Succeeds without doing anything.
##
## There is no native session to end: this flow holds no provider state between attempts,
## and revoking a granted refresh token is the backend's call to make, not this one's.
async func sign_out(_provider: Provider) -> CompletionResult:
	return CompletionResult.Success

async func store_session(_session: AuthSession) -> CompletionResult:
	return CompletionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

async func restore_session() -> SessionResult:
	return SessionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

func has_stored_session() -> bool:
	return false

async func clear_stored_session() -> CompletionResult:
	return CompletionResult.Success

## Builds the URL the system browser is sent to for a Google sign-in.
##
## Every value is percent-encoded, [param redirect_uri] included — it carries a `:` and a
## `/`, and sending it unencoded is the classic bug in this flow. [param redirect_uri] must
## be the value [method LoopbackServer.redirect_uri] returned after
## [method LoopbackServer.start], not re-derived here, so the browser is told to answer on
## the port the listener actually bound. [param nonce] is independent of [param pkce] and
## is the caller's to generate — [PkcePair] carries only what RFC 7636 defines.
static func build_authorization_url(
		provider_config: ProviderConfig,
		pkce: PkcePair,
		redirect_uri: String,
		nonce: String) -> String:
	var keys: PackedStringArray = [
		"client_id",
		"redirect_uri",
		"response_type",
		"scope",
		"code_challenge",
		"code_challenge_method",
		"state",
		"nonce",
	]
	var values: PackedStringArray = [
		_desktop_client_id_of(provider_config),
		redirect_uri,
		"code",
		_SCOPE,
		pkce.code_challenge,
		"S256",
		pkce.state,
		nonce,
	]
	return "%s?%s" % [AUTHORIZATION_ENDPOINT, _form_encoded(keys, values)]

## Runs the flow and reports how it ended, without ever releasing the listener.
##
## Releasing is [method sign_in]'s job, deliberately: every return below is an exit path,
## and spreading the stop across them is how one of them comes to be forgotten.
async func _run_flow(listener: LoopbackServer) -> CredentialResult:
	if not listener.start():
		return CredentialResult.Failure(AuthError.Unavailable(Provider.GOOGLE))
	# Read once and reused for both the authorization request and the exchange: the
	# authorization server compares the two, so they must be identical, and the port is not
	# known until the listener has bound.
	var redirect_uri: String = listener.redirect_uri()
	var pkce: PkcePair = PkcePair.generate()
	# Snapshotted, not reread. One sign-in is one OAuth transaction against one client ID,
	# and there are two suspension points between here and the credential — a
	# [method configure] landing in either of them would otherwise send one client ID to the
	# authorization endpoint and a different one to the token endpoint.
	var config: ProviderConfig = _config
	var url: String = build_authorization_url(config, pkce, redirect_uri, _new_nonce())

	if not _hand_to_browser(url):
		# No browser means no consent screen, so waiting out the watchdog for a callback
		# nobody will send is the wrong answer. Report it as this provider being
		# unavailable on this host, which is what it is.
		_log.warn("the system browser could not be opened for the desktop sign-in flow")
		return CredentialResult.Failure(AuthError.Unavailable(Provider.GOOGLE))

	var outcome: LoopbackOutcome = await listener.await_callback(_callback_timeout_seconds)
	match outcome:
		LoopbackOutcome.Received(query):
			return await _credential_from_callback(query, config, pkce, redirect_uri)
		LoopbackOutcome.TimedOut(elapsed_seconds):
			return CredentialResult.Failure(AuthError.TimedOut(elapsed_seconds))
		LoopbackOutcome.Failed(detail):
			# Status 0 says "there was no status" — no callback ever completed. The same
			# convention [BackendClient] uses for a request that never reached a server.
			return CredentialResult.Failure(AuthError.RequestFailed(0, detail))
	return CredentialResult.Failure(AuthError.Unavailable(Provider.GOOGLE))

## Interprets one delivered callback, and acts on it only if it can be trusted.
##
## [b][code]state[/code] is compared before anything else is read[/b] — before the code, and
## before [code]error[/code]. Any process on the machine can reach the loopback port, so
## nothing a callback says is evidence this flow asked for it until its state matches.
## Exchanging first and failing afterwards would hand an attacker's code to the token
## endpoint under this client's identity; reporting an unauthenticated
## [code]error[/code] as a cancellation would tell the caller the player pressed Cancel
## when the player never saw a screen. RFC 6749 §4.1.2.1 requires the server to echo
## [code]state[/code] on an error response for exactly this reason.
##
## A matching [code]error[/code] is a cancellation. The RFC allows several codes there, but
## every one of them reaching a loopback redirect means the same thing to the player: the
## sign-in did not happen. A caller shown a fault dialog for someone pressing Cancel has
## been misinformed by this backend.
async func _credential_from_callback(
		query: Dictionary[String, String],
		config: ProviderConfig,
		pkce: PkcePair,
		redirect_uri: String) -> CredentialResult:
	var returned_state: String = ""
	if query.has("state"):
		returned_state = query["state"]
	if returned_state != pkce.state:
		_log.warn("an authorization callback carried a state this flow did not send")
		return CredentialResult.Failure(AuthError.InvalidResponse(
				"the authorization callback carried a state this flow did not send"))

	if query.has("error"):
		_log.debug("the authorization callback reported \"%s\"" % query["error"])
		return CredentialResult.Failure(AuthError.Cancelled)

	var code: String = ""
	if query.has("code"):
		code = query["code"]
	if code.is_empty():
		return CredentialResult.Failure(AuthError.MissingField("code"))

	return await _exchange_code(code, config, pkce, redirect_uri)

## Exchanges an authorization code for an ID token.
##
## Form-encoded rather than JSON, because RFC 6749 §4.1.3 defines the token request that
## way. No client secret is sent: a client secret shipped inside a game is not a secret,
## which is precisely why PKCE replaces it here — the verifier proves this is the same
## client that made the authorization request.
async func _exchange_code(
		code: String,
		config: ProviderConfig,
		pkce: PkcePair,
		redirect_uri: String) -> CredentialResult:
	var keys: PackedStringArray = [
		"client_id",
		"code",
		"code_verifier",
		"grant_type",
		"redirect_uri",
	]
	var values: PackedStringArray = [
		_desktop_client_id_of(config),
		code,
		pkce.code_verifier,
		"authorization_code",
		redirect_uri,
	]
	var headers: PackedStringArray = [
		"Accept: application/json",
		"Content-Type: application/x-www-form-urlencoded",
	]
	var outcome: HttpOutcome = await _transport.send(
			"POST",
			TOKEN_ENDPOINT,
			headers,
			_form_encoded(keys, values).to_utf8_buffer(),
			EXCHANGE_TIMEOUT_SECONDS)
	match outcome:
		HttpOutcome.Answered(status_code, body):
			return _credential_from_token_response(status_code, body, config)
		HttpOutcome.TransportFailed(detail):
			return CredentialResult.Failure(AuthError.RequestFailed(0, detail))
		HttpOutcome.TimedOut(elapsed_seconds):
			return CredentialResult.Failure(AuthError.TimedOut(elapsed_seconds))
	return CredentialResult.Failure(AuthError.InvalidResponse(
			"the transport reported an outcome this backend does not understand"))

## Builds the credential a token response describes.
##
## [code]audience[/code] is the desktop client ID, and it is known here rather than
## guessed: the code was issued to that client and the token endpoint was asked for it by
## name, so the ID token it returns names it as the audience. [AppleAuthBackend] has to
## leave the same field empty because its native never reports one.
##
## [code]email[/code] and [code]name[/code] are read out of the ID token unverified, which
## is all FoundryKit ever does with a token — whoever accepts this credential is the
## authority on what it says.
func _credential_from_token_response(
		status_code: int,
		body: PackedByteArray,
		config: ProviderConfig) -> CredentialResult:
	var text: String = body.get_string_from_utf8()
	if status_code < 200 or status_code >= 300:
		return CredentialResult.Failure(AuthError.RequestFailed(status_code, text))
	var parser: JSON = JSON.new()
	if parser.parse(text) != OK:
		return CredentialResult.Failure(AuthError.InvalidResponse(
				"the token response was not valid JSON"))
	if not (parser.data is Dictionary):
		return CredentialResult.Failure(AuthError.InvalidResponse(
				"the token response was not a JSON object"))
	var payload: Dictionary = parser.data
	# Read as a string rather than stringified. `{"id_token": 123}` is a malformed answer
	# from the token endpoint, and coercing it would produce a credential carrying "123"
	# that only fails later, at whichever backend tries to verify it.
	var raw_id_token: Variant = payload.get("id_token", "")
	if not (raw_id_token is String):
		return CredentialResult.Failure(AuthError.InvalidResponse(
				"the token response carried a non-string id_token"))
	var id_token: String = raw_id_token
	if id_token.is_empty():
		return CredentialResult.Failure(AuthError.MissingField("id_token"))
	return CredentialResult.Success(Credential.Google(
			id_token,
			Jwt.string_claim_from(id_token, "email"),
			Jwt.string_claim_from(id_token, "name"),
			_desktop_client_id_of(config)))

## Returns a fresh, unstarted listener from the injected factory.
##
## One listener per attempt, never reused: [method LoopbackServer.await_callback] is
## callable once, and a second attempt handed a spent listener would be refused rather than
## wait for its callback.
func _create_listener() -> LoopbackServer:
	var created: Variant = _new_listener.call()
	var listener: LoopbackServer = created
	return listener

## Hands [param url] to the injected browser opener and reports whether it took it.
##
## A [Callable] answers with a [Variant], so anything that is not a plain [code]true[/code]
## is read as a refusal rather than trusted — the shape [AppleAuthBackend] uses for values
## crossing an untyped boundary.
func _hand_to_browser(url: String) -> bool:
	var reported: Variant = _open_browser.call(url)
	return reported is bool and reported == true

func _new_loopback_server() -> LoopbackServer:
	return LoopbackServer.new(_log)

func _open_system_browser(url: String) -> bool:
	return OS.shell_open(url) == OK

## Generates the OpenID Connect nonce for one attempt.
##
## Hex rather than base64url so it carries no encoding rules of its own; what the nonce
## needs is unguessability and freshness, not compactness.
func _new_nonce() -> String:
	var crypto: Crypto = Crypto.new()
	return crypto.generate_random_bytes(_NONCE_BYTE_COUNT).hex_encode()

func _unavailable(provider: Provider) -> CredentialResult:
	_log.debug("desktop sign-in unavailable for provider %d" % provider)
	return CredentialResult.Failure(AuthError.Unavailable(provider))

## Percent-encodes parallel keys and values into one
## [code]application/x-www-form-urlencoded[/code] string, used for both the authorization
## query and the token request body.
##
## Positional pairs rather than a dictionary because the order of the parameters must be
## stable: a URL that varies run to run cannot be asserted by equality.
static func _form_encoded(keys: PackedStringArray, values: PackedStringArray) -> String:
	var parts: PackedStringArray = []
	for index: int in range(keys.size()):
		parts.append("%s=%s" % [keys[index].uri_encode(), values[index].uri_encode()])
	return "&".join(parts)

## Returns the desktop OAuth client ID out of a Google provider configuration.
##
## This flow only ever configures [enum Provider.GOOGLE]; the other cases return an empty
## string rather than the [code]_[/code] wildcard the union redesign forbids, so a caller
## that reaches this with the wrong provider gets an obviously-wrong URL instead of a
## crash, and adding a case here still breaks every switch that needs updating.
static func _desktop_client_id_of(provider_config: ProviderConfig) -> String:
	match provider_config:
		ProviderConfig.Google(_web_client_id, _ios_client_id, desktop_client_id):
			return desktop_client_id
		ProviderConfig.Apple(_service_id, _redirect_uri):
			return ""
		ProviderConfig.EmailPassword:
			return ""
	return ""
