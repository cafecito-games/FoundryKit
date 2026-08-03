namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core

## The concrete authentication subsystem behind [code]FoundryKit.auth[/code].
##
## Composes the three pieces the auth flow needs and owns the decisions none of them can
## make alone: a platform [AuthBackend] produces a [Credential], a [BackendClient] speaks
## to the configured backend, and a [SessionStore] holds the one session and renews it
## single-flight. This class exchanges the first for the third over the second, and keeps
## the session usable afterwards.
##
## [b]Configuration is a real state, not a stub.[/b] Without a [BackendConfig] carrying a
## base URL there is genuinely nowhere to exchange a credential or refresh a token, so
## every path that needs the backend reports [code]AuthError.Configuration[/code] naming
## what is missing rather than failing later with an error the consumer cannot act on.
##
## [b]One retry, never a loop.[/b] A 401 on an authorized request buys exactly one refresh
## and exactly one replay. The retry is written as a single call on the success branch of
## the refresh rather than as a loop with a counter, so the bound is structural: there is
## no counter to get wrong and no path back to the first attempt. The replay is bound to the
## session the first attempt was authorized under — see [method _replaced_mid_request].
##
## [b]Announcements are per event, not per caller.[/b] [SessionStore] emits nothing and
## reports [method SessionStore.rotation_count] instead, because a refresh is single-flight:
## N concurrent callers share one round and one rotation. This class remembers the last
## count it announced and whether the current session's loss has been announced, so one
## rotation produces one [signal AuthApi.tokens_refreshed] and one lapse produces one
## [signal AuthApi.session_expired], however many callers were joined to the round that
## caused it. For the same reason both are announced from every refresh path including
## [method refresh_session]: an explicit refresh and an internal one can be the same round,
## so there is no "which caller was it" to condition on.
class_name AuthSubsystem extends RefCounted
uses AuthApi

## Emitted when the active session expires outside any call the game made.
##
## Redeclared here rather than only on [AuthApi]. A signal declared in a trait that lives in
## another file is not flattened into the composing script's signal list, so
## [code]subsystem.session_expired.connect(...)[/code] fails at runtime with "invalid access
## to property or key" unless the composing class declares it too. A trait declared in the
## same file flattens correctly, which is what makes the difference easy to miss. The
## declaration on [AuthApi] stays: that is the contract every implementor owes, and this is
## the storage that satisfies it.
signal session_expired(error: AuthError)

## Emitted when a refresh rotates the active session's tokens.
##
## Redeclared for the same reason as [signal session_expired].
signal tokens_refreshed(session: AuthSession)

const _NO_BACKEND: String = "no authentication backend endpoint is configured"

## The status that means the credential presented was not accepted.
const _UNAUTHORIZED: int = 401

var _log: FoundryKitLog
var _backend: AuthBackend
var _guard: RequestGuard
var _transport: HttpTransport
var _config: BackendConfig
var _client: BackendClient
var _store: SessionStore

## The value of [method SessionStore.rotation_count] at the last
## [signal AuthApi.tokens_refreshed]. Rotations past this one have not been announced.
var _announced_rotation_count: int = 0

## Whether the loss of the current session has already been announced.
##
## Starts true because a subsystem holding no session has nothing to lose. Cleared whenever
## a session is installed, set again by every explicit sign-out so that ending a session on
## purpose is never reported as one lapsing.
var _lapse_announced: bool = true

## Counts the times the held session was replaced or dropped outright.
##
## A refresh does not advance it — renewing a session leaves it the same session. Signing
## in, restoring and signing out do, which is what lets a request that is already in flight
## tell that the session it was authorized under is no longer the one held.
var _session_generation: int = 0

## Builds the subsystem.
##
## [param transport], [param config] and [param backend] default to the production objects
## and exist so a test can drive the whole session layer against a scripted transport with
## no network and no platform — the same seam [AppleAuthBackend] opens for its native class.
## Passing none of them yields exactly what a consumer gets: the resolved platform backend,
## a real [HttpClient], and an unconfigured [BackendConfig].
func _init(
		log: FoundryKitLog,
		transport: HttpTransport? = null,
		config: BackendConfig? = null,
		backend: AuthBackend? = null) -> void:
	_log = log
	_guard = RequestGuard.new(log)

	# Resolved by assignment rather than through helpers returning the trait types: a
	# function declared to return a trait fails the runtime return check when handed a class
	# that merely composes it, while assignment to a trait-typed member does not.
	var injected_backend: AuthBackend? = backend
	if injected_backend == null:
		var factory: AuthBackendFactory = AuthBackendFactory.new(log)
		_backend = factory.resolve_current()
	else:
		_backend = injected_backend

	var injected_transport: HttpTransport? = transport
	if injected_transport == null:
		_transport = HttpClient.new(log)
	else:
		_transport = injected_transport

	var injected_config: BackendConfig? = config
	if injected_config == null:
		_config = BackendConfig.new()
	else:
		_config = injected_config

	_client = BackendClient.new(log, _transport, _config)
	_store = SessionStore.new(log, _client)
	_log.debug("resolved auth backend '%s'" % _backend.backend_name())

## Returns the guard so the facade can route lifecycle notifications to it.
func request_guard() -> RequestGuard:
	return _guard

func configure(config: ProviderConfig) -> void:
	_backend.configure(config)

func is_available(provider: Provider) -> bool:
	return _backend.is_available(provider)

func is_configured(provider: Provider) -> bool:
	return _backend.is_configured(provider)

async func sign_in(provider: Provider) -> SessionResult:
	return await _exchange(await _backend.sign_in(provider))

async func sign_in_silent(provider: Provider) -> SessionResult:
	return await _exchange(await _backend.sign_in_silent(provider))

## Signs out of the native provider and drops the session.
##
## The session is dropped first and unconditionally: a native sign-out that fails does not
## make the session usable again, and leaving it held would report a signed-out player as
## signed in.
async func sign_out(provider: Provider) -> CompletionResult:
	_forget()
	return await _backend.sign_out(provider)

func has_session() -> bool:
	return _store.has_session()

func access_token() -> String:
	return _store.access_token()

## Returns an access token that is valid now, refreshing first when it is not.
##
## A token carrying no readable expiry is treated as valid: the backend is the authority on
## an opaque token, and a 401 on the next request is how its expiry surfaces.
async func valid_access_token() -> TokenResult:
	if not _is_backend_configured():
		return TokenResult.Failure(AuthError.Configuration(_NO_BACKEND))

	var current: AuthSession? = _store.session()
	if current == null:
		return TokenResult.Failure(AuthError.SessionExpired(0))
	var active: AuthSession = current
	if not active.is_expired_at(_now_unix_seconds()):
		return TokenResult.Success(active.access_token)

	var refreshed: SessionResult = await _refresh_and_announce()
	match refreshed:
		SessionResult.Failure(error):
			return TokenResult.Failure(error)
		SessionResult.Success(session):
			return TokenResult.Success(session.access_token)
	return TokenResult.Failure(AuthError.InvalidResponse(
			"the session store reported a result this subsystem does not understand"))

## Forces a refresh of the active session.
async func refresh_session() -> SessionResult:
	if not _is_backend_configured():
		return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))
	return await _refresh_and_announce()

## Restores a session from platform secure storage and makes it the active one.
async func restore_session() -> SessionResult:
	var result: SessionResult = await _backend.restore_session()
	match result:
		SessionResult.Failure(_error):
			return result
		SessionResult.Success(session):
			_install(session)
			return result
	return result

## Drops the active session and removes it from secure storage.
async func clear_session() -> CompletionResult:
	_forget()
	return await _backend.clear_stored_session()

## Sends an authorized request, refreshing and replaying it once if the backend refuses the
## token it presented.
##
## A second refusal ends the session rather than buying another refresh: the token just
## issued was rejected, so nothing another round could produce would be accepted either.
##
## Exhaustive over [SessionResult]; the trailing return exists only because the analyser
## cannot see that the match is total.
async func request(method: HttpMethod, path: String, body: Variant) -> ResponseResult:
	var generation: int = _session_generation
	var first: ResponseResult = await _client.request(method, path, body, _store.access_token())
	if not _is_refused(first):
		return first
	if generation != _session_generation:
		return _replaced_mid_request()

	_log.debug("the backend refused the presented token; refreshing once before retrying")
	var refreshed: SessionResult = await _refresh_and_announce()
	match refreshed:
		SessionResult.Failure(error):
			return ResponseResult.Failure(error)
		SessionResult.Success(_session):
			if generation != _session_generation:
				return _replaced_mid_request()
			var retried: ResponseResult = await _client.request(
					method, path, body, _store.access_token())
			return _result_of_retry(retried)
	return ResponseResult.Failure(AuthError.InvalidResponse(
			"the session store reported a result this subsystem does not understand"))

## Reports a request whose session was replaced while it was still in flight.
##
## The first attempt was authorized as one session; a replay would be authorized as whoever
## is signed in now. A mutating request made for one account would then execute against
## another, so it is abandoned instead. The session the request belonged to no longer
## exists, which is what the caller is told — and no lapse is announced, because nothing
## lapsed: a sign-in or a sign-out replaced it on purpose.
func _replaced_mid_request() -> ResponseResult:
	_log.debug("abandoning a request whose session was replaced while it was in flight")
	return ResponseResult.Failure(AuthError.SessionExpired(0))

## Returns what a replayed request resolves to.
##
## The only case this changes is a second refusal, which ends the session. Everything else
## is the backend's own answer and is passed through untouched.
func _result_of_retry(retried: ResponseResult) -> ResponseResult:
	if not _is_refused(retried):
		return retried
	var expired_at: int = _current_expiry()
	_forget_lapsed()
	_announce_lapse(AuthError.SessionExpired(expired_at))
	return ResponseResult.Failure(AuthError.SessionExpired(expired_at))

## Exchanges a credential for a backend session.
##
## Exhaustive over [CredentialResult]; the trailing return exists only because the analyser
## cannot see that the match is total.
async func _exchange(credential_result: CredentialResult) -> SessionResult:
	match credential_result:
		CredentialResult.Failure(error):
			return SessionResult.Failure(error)
		CredentialResult.Success(credential):
			return await _exchange_credential(credential)
	return SessionResult.Failure(AuthError.InvalidResponse(
			"the backend reported a credential result this subsystem does not understand"))

## Posts one credential to the exchange endpoint and installs whatever session came back.
##
## The request carries no bearer credential: there is no session yet, and the credential
## being exchanged travels in the body.
async func _exchange_credential(credential: Credential) -> SessionResult:
	var provider: Provider = Credential.provider_of(credential)
	_log.debug("exchanging a credential for provider %d" % provider)
	var response: ResponseResult = await _client.request(
			HttpMethod.POST, _client.config().exchange_path, _exchange_body(credential))
	var result: SessionResult = _session_of(response, provider)
	match result:
		SessionResult.Failure(_error):
			return result
		SessionResult.Success(session):
			_install(session)
			return result
	return result

## Builds the JSON body one credential is exchanged with.
##
## Each provider sends the fields it actually has rather than a common shape padded with
## empties, so a backend can tell an absent field from one the provider never issues.
## [code]provider[/code] is a stable name rather than the enum's ordinal: the wire contract
## must not shift if a case is ever inserted into [Provider].
##
## Exhaustive over [Credential]; the trailing return exists only because the analyser cannot
## see that the match is total.
func _exchange_body(credential: Credential) -> Dictionary[String, Variant]:
	match credential:
		Credential.Google(id_token, email, display_name, audience):
			var google_body: Dictionary[String, Variant] = {
				"provider": "google",
				"id_token": id_token,
				"email": email,
				"display_name": display_name,
				"audience": audience,
			}
			return google_body
		Credential.Apple(identity_token, authorization_code, email, full_name):
			var apple_body: Dictionary[String, Variant] = {
				"provider": "apple",
				"identity_token": identity_token,
				"authorization_code": authorization_code,
				"email": email,
				"full_name": full_name,
			}
			return apple_body
		Credential.EmailPassword(email, password):
			var password_body: Dictionary[String, Variant] = {
				"provider": "email_password",
				"email": email,
				"password": password,
			}
			return password_body
	var empty_body: Dictionary[String, Variant] = {}
	return empty_body

## Maps an exchange reply onto a session result.
##
## A refusal is [code]RequestFailed[/code], not [code]SessionExpired[/code]: no session
## existed to expire, and reporting one would invite a refresh with no token to present.
##
## Exhaustive over [ResponseResult]; the trailing return exists only because the analyser
## cannot see that the match is total.
func _session_of(response: ResponseResult, provider: Provider) -> SessionResult:
	match response:
		ResponseResult.Failure(error):
			return SessionResult.Failure(error)
		ResponseResult.Success(answer):
			if answer.session_expired:
				_log.debug("the backend refused the credential")
				return SessionResult.Failure(AuthError.RequestFailed(
						_UNAUTHORIZED, "the backend refused the credential"))
			return _session_from_body(answer, provider)
	return SessionResult.Failure(AuthError.InvalidResponse(
			"the backend client reported a result this subsystem does not understand"))

## Builds the issued session from a successful exchange reply.
##
## Both tokens must arrive as JSON strings when they arrive at all. Stringifying whatever
## turned up instead would turn a number, an object or a null into a plausible-looking
## credential and then present it as a bearer token on every later request.
##
## The refresh token is optional: a backend that issues none leaves the session
## unrenewable, which [SessionStore] reports honestly the first time a refresh is asked
## for. An access token is not optional — without one there is no session at all.
func _session_from_body(response: AuthResponse, provider: Provider) -> SessionResult:
	var parsed: Variant = response.json()
	if not (parsed is Dictionary):
		return SessionResult.Failure(AuthError.InvalidResponse(
				"the exchange response was not a JSON object"))
	var payload: Dictionary = parsed

	var raw_access_token: Variant = payload.get("access_token")
	if raw_access_token == null:
		return SessionResult.Failure(AuthError.MissingField("access_token"))
	if not (raw_access_token is String):
		return SessionResult.Failure(AuthError.InvalidResponse(
				"the exchange response carried a non-string access_token"))
	var access_token: String = raw_access_token
	if access_token.is_empty():
		return SessionResult.Failure(AuthError.MissingField("access_token"))

	var refresh_token: String = ""
	var raw_refresh_token: Variant = payload.get("refresh_token")
	if raw_refresh_token != null:
		if not (raw_refresh_token is String):
			return SessionResult.Failure(AuthError.InvalidResponse(
					"the exchange response carried a non-string refresh_token"))
		refresh_token = raw_refresh_token

	var raw: Dictionary[String, Variant] = {}
	for key: Variant in payload.keys():
		raw[str(key)] = payload[key]
	var extras: Dictionary[String, Variant] = {}
	return SessionResult.Success(AuthSession.new(
			access_token, refresh_token, provider, raw, extras))

## Refreshes through the store and announces whatever changed as a result.
##
## Every internal refresh path goes through here, so there is one place a rotation or a
## lapse can be announced from and one place that decides whether it already has been.
async func _refresh_and_announce() -> SessionResult:
	var result: SessionResult = await _store.refresh()
	_announce_rotation()
	_announce_lapse_if_lost(result)
	return result

## Announces a rotation the store recorded and this subsystem has not reported yet.
##
## Keyed on [method SessionStore.rotation_count] rather than on "a refresh returned
## successfully", because a single-flight round hands one success to every caller joined to
## it. Comparing counts makes the announcement per rotation, which is what actually happened.
func _announce_rotation() -> void:
	var rotations: int = _store.rotation_count()
	if rotations == _announced_rotation_count:
		return
	_announced_rotation_count = rotations
	var current: AuthSession? = _store.session()
	if current == null:
		return
	var rotated: AuthSession = current
	tokens_refreshed.emit(rotated)

## Announces the loss of a session when a refresh ended with the store holding none.
##
## A refresh that merely failed leaves the session held and announces nothing: an
## unreachable backend has not signed anybody out. Only a refusal of the refresh token
## empties the store, and that is what a lapse is.
##
## Exhaustive over [SessionResult]; the trailing return exists only because the analyser
## cannot see that the match is total.
func _announce_lapse_if_lost(result: SessionResult) -> void:
	if _store.has_session():
		return
	match result:
		SessionResult.Failure(error):
			_announce_lapse(error)
		SessionResult.Success(_session):
			return
	return

## Emits [signal AuthApi.session_expired] at most once per session.
##
## The guard is what keeps N callers joined to one refused round from announcing one lapse
## N times, and what keeps a later call that simply finds no session from announcing it
## again.
func _announce_lapse(error: AuthError) -> void:
	if _lapse_announced:
		return
	_lapse_announced = true
	session_expired.emit(error)

## Makes [param session] the active one and re-arms both announcements for it.
func _install(session: AuthSession) -> void:
	_store.set_session(session)
	_session_generation += 1
	_announced_rotation_count = _store.rotation_count()
	_lapse_announced = false

## Ends the session on purpose. Nothing lapsed, so nothing is announced.
func _forget() -> void:
	_store.clear()
	_session_generation += 1
	_lapse_announced = true

## Ends the session because the backend refused it, leaving the lapse still to announce.
func _forget_lapsed() -> void:
	_store.clear()
	_session_generation += 1

## Returns the expiry instant of the session held right now, or 0 when none is held.
func _current_expiry() -> int:
	var current: AuthSession? = _store.session()
	if current == null:
		return 0
	var active: AuthSession = current
	return active.expires_at()

## Returns whether the backend refused the credential a request presented.
##
## [BackendClient] reports a 401 as a success carrying
## [member AuthResponse.session_expired], because whether a refusal is worth retrying is
## the caller's decision. Here it is.
##
## Exhaustive over [ResponseResult]; the trailing return exists only because the analyser
## cannot see that the match is total.
func _is_refused(response: ResponseResult) -> bool:
	match response:
		ResponseResult.Success(answer):
			return answer.session_expired
		ResponseResult.Failure(_error):
			return false
	return false

## Returns whether a backend endpoint has been configured at all.
func _is_backend_configured() -> bool:
	return _client.config().is_configured()

## Returns the current wall-clock time in whole seconds since the epoch, which is the unit
## a JWT `exp` claim is stated in.
func _now_unix_seconds() -> int:
	return int(Time.get_unix_time_from_system())
