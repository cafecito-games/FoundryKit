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
## no counter to get wrong and no path back to the first attempt.
##
## [b]A call never uses, hands out, or destroys a session it did not begin under.[/b] Native
## sheets, secure storage and the network all take arbitrarily long, and a player can sign in
## again or sign out while any of them is outstanding. Every call that spans an await takes
## the session generation first and checks it — see [method _session_replaced_since] — so
## that across a replacement:
##
## - no request is ever authorized with a token from a session it did not begin under, and no
##   completed sign-in or restore reinstates a session an explicit sign-out has ended;
## - no session, token or credential belonging to one sign-in is handed to a caller that
##   asked during another;
## - no session is ended because a different session's credential was refused.
##
## What it deliberately does not do is withhold a reply the backend already produced. A
## response fetched with one session's token is that session's data, and it is returned to
## the one caller that asked for it — [method request] is a return value, not a write into
## any shared state. Reporting a completed request as expired instead would tell the caller a
## mutation it already performed had failed, and would invite it to perform that mutation
## twice, while undoing nothing: the request has already executed on the backend.
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

## Counts sign-in attempts, so a restore that began earlier can tell that the game has since
## asked to sign in explicitly.
##
## A sign-in outranks a restore: it names the account the player wants, while a restore only
## reports the one that happened to be stored. Without this, a slow secure-storage read
## completing after an explicit sign-in leaves the stored account active and discards the
## chosen one. Deliberately separate from [member _session_generation], which no sign-in
## advances until it actually installs a session — a sign-in the player then cancels must not
## abandon the requests that were in flight when it opened.
var _sign_in_generation: int = 0

## Counts the times the configured backend origin was moved.
##
## Deliberately separate from [member _session_generation], because the two answer different
## questions: that one asks whether the session a call began under is still held, this one
## whether the backend it began against is still the one configured. A sign-in suspended in
## front of a native sheet still exchanges its credential when the session changed meanwhile
## and then discards the result — but it must not exchange it at all once the backend has
## moved, because that posts a credential obtained for one service to another.
var _backend_generation: int = 0

## The Google web client ID the exchange presents as the credential's audience.
##
## Learned from [method configure]; see [method _resolved_audience] for why the exchange,
## not the native backend, is what supplies it.
var _google_audience: String = ""

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

## Applies configuration for one provider, keeping whatever of it the exchange needs.
func configure(config: ProviderConfig) -> void:
	_google_audience = _audience_of(config, _google_audience)
	_backend.configure(config)

## Points this subsystem at the backend that issues and renews sessions.
##
## Keeps the values, not the object. [BackendClient] holds the very [BackendConfig] instance
## this subsystem was built with, so copying into that instance reconfigures every layer at
## once; rebuilding the client instead would take the [SessionStore] built over it — and the
## session it holds — with it. Keeping the caller's instance would also let a consumer that
## edits it later move the backend under requests already running against the old one.
##
## [b]A session belongs to the origin it was obtained under.[/b] Tokens issued by one backend
## are not credentials at another: presenting them would disclose one service's access token,
## or its refresh token, to a second service that has no business holding either. So naming a
## different origin — including naming one for the first time while a session restored from
## secure storage is already held, whose issuer this subsystem has no record of — ends the
## session through [method _forget]. That also advances the session generation, which is what
## stops a refresh, a retry or a restore already in flight from completing across the move.
## Nothing is announced: the consumer moved the backend on purpose, so the session did not
## lapse, it was ended.
##
## The practical consequence is that a game configures the backend before it restores. Doing
## it the other way round discards the restored session, which is the honest outcome — the
## alternative is presenting stored tokens to a backend that may not have issued them.
##
## Correcting a path on the same origin is not a move and leaves the session alone, and
## neither is a base URL that differs only by a trailing slash: [method BackendConfig.url_for]
## resolves both forms to the same endpoint, so treating them as a move would sign a player
## out over punctuation.
func configure_backend(config: BackendConfig) -> void:
	var previous_origin: String = _origin_of(_config.base_url)
	_config.base_url = config.base_url
	_config.exchange_path = config.exchange_path
	_config.refresh_path = config.refresh_path
	_config.sign_out_path = config.sign_out_path
	_log.debug("configured the auth backend at '%s'" % _config.base_url)
	if previous_origin == _origin_of(_config.base_url):
		return
	_backend_generation += 1
	_log.debug("dropping the session held against the previous auth backend")
	_forget()

## Returns the comparable form of [param base_url].
##
## A trailing slash is dropped because [method BackendConfig.url_for] already collapses the
## doubled separator it would produce: two base URLs differing only there address every path
## identically, so they are the same origin and moving between them is not a move.
func _origin_of(base_url: String) -> String:
	return base_url.rstrip("/")

## Returns an independent copy of the backend configuration in force.
##
## A copy for the same reason [method configure_backend] takes one: a caller that mutated
## what this handed back would move the backend without ever passing through configuration.
func backend_config() -> BackendConfig:
	return BackendConfig.new(
			_config.base_url,
			_config.exchange_path,
			_config.refresh_path,
			_config.sign_out_path)

func is_available(provider: Provider) -> bool:
	return _backend.is_available(provider)

func is_configured(provider: Provider) -> bool:
	return _backend.is_configured(provider)

## Runs an interactive sign-in and exchanges the credential it produces.
##
## The session generation is taken before the native flow starts, so a sign-out or a second
## sign-in that lands while the player is still in front of the native sheet is not undone
## by the session this flow eventually produces.
async func sign_in(provider: Provider) -> SessionResult:
	_sign_in_generation += 1
	var generation: int = _session_generation
	var backend_generation: int = _backend_generation
	return await _exchange(await _backend.sign_in(provider), generation, backend_generation)

## Attempts sign-in without UI and exchanges the credential it produces.
async func sign_in_silent(provider: Provider) -> SessionResult:
	_sign_in_generation += 1
	var generation: int = _session_generation
	var backend_generation: int = _backend_generation
	return await _exchange(
			await _backend.sign_in_silent(provider), generation, backend_generation)

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

	var generation: int = _session_generation
	var refreshed: SessionResult = await _refresh_and_announce()
	if _session_replaced_since(generation):
		return TokenResult.Failure(AuthError.SessionExpired(0))
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
	var generation: int = _session_generation
	var refreshed: SessionResult = await _refresh_and_announce()
	if _session_replaced_since(generation):
		return SessionResult.Failure(AuthError.SessionExpired(0))
	return refreshed

## Restores a session from platform secure storage and makes it the active one.
##
## A restore that completes after an explicit sign-out is discarded rather than installed:
## secure storage can take arbitrarily long, and reinstating a session the player has since
## ended would sign them back in behind their back. A restore that completes after a sign-in
## has started is discarded for the same reason: the player named an account, and the stored
## one is not it.
async func restore_session() -> SessionResult:
	var generation: int = _session_generation
	var sign_in_generation: int = _sign_in_generation
	var result: SessionResult = await _backend.restore_session()
	match result:
		SessionResult.Failure(_error):
			return result
		SessionResult.Success(session):
			if _session_replaced_since(generation) or sign_in_generation != _sign_in_generation:
				return _superseded()
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
## A reply that arrives after the session was replaced is still returned to this caller — it
## is the data this call asked for, fetched with the credential it was authorized with. What
## a replacement does change is that the request is not replayed under the new session, and a
## refusal is not allowed to end it.
##
## Exhaustive over [SessionResult]; the trailing return exists only because the analyser
## cannot see that the match is total.
async func request(method: HttpMethod, path: String, body: Variant) -> ResponseResult:
	var generation: int = _session_generation
	var first: ResponseResult = await _client.request(method, path, body, _store.access_token())
	if not _is_refused(first):
		return first
	if _session_replaced_since(generation):
		return _replaced_mid_request()

	_log.debug("the backend refused the presented token; refreshing once before retrying")
	var refreshed: SessionResult = await _refresh_and_announce()
	match refreshed:
		SessionResult.Failure(error):
			return ResponseResult.Failure(error)
		SessionResult.Success(_session):
			if _session_replaced_since(generation):
				return _replaced_mid_request()
			var rotations: int = _store.rotation_count()
			var retried: ResponseResult = await _client.request(
					method, path, body, _store.access_token())
			return _result_of_retry(retried, generation, rotations)
	return ResponseResult.Failure(AuthError.InvalidResponse(
			"the session store reported a result this subsystem does not understand"))

## Returns the audience a Google exchange presents.
##
## [AppleAuthBackend] leaves the credential's audience empty on purpose — the native does
## not return it — and names this the layer that supplies it. A backend that validates the
## audience refuses an otherwise valid sign-in without one, so the configured web client ID
## fills the gap. A credential that does carry an audience keeps it: the desktop OAuth flow
## knows which client ID it authorized against, and that is more specific than configuration.
func _resolved_audience(audience: String) -> String:
	if audience.is_empty():
		return _google_audience
	return audience

## Returns the audience to keep after applying [param config].
##
## Only the Google case carries one. Every other configuration leaves the current value
## alone rather than clearing it, so configuring Apple afterwards does not silently
## unconfigure Google.
##
## Exhaustive over [ProviderConfig]; the trailing return exists only because the analyser
## cannot see that the match is total.
func _audience_of(config: ProviderConfig, current: String) -> String:
	match config:
		ProviderConfig.Google(web_client_id, _ios_client_id, _desktop_client_id):
			return web_client_id
		ProviderConfig.Apple(_service_id, _redirect_uri):
			return current
		ProviderConfig.EmailPassword:
			return current
	return current

## Returns whether the held session was replaced or dropped since [param generation].
##
## Every call that spans an await takes the generation first and checks it before handing
## anything back, so a call made for one player can never return the session, the token or
## the reply belonging to whoever signed in while it was suspended.
func _session_replaced_since(generation: int) -> bool:
	return generation != _session_generation

## Returns what a flow resolves to when an explicit session change superseded it.
##
## [code]Cancelled[/code] rather than an error naming the backend: nothing failed. The
## player, or the game on their behalf, asked for something else while this was in flight,
## and the result is being dropped on purpose.
func _superseded() -> SessionResult:
	_log.debug("discarding a session an explicit sign-in or sign-out has superseded")
	return SessionResult.Failure(AuthError.Cancelled)

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
## The only case this changes is a second refusal, which ends the session — and only when the
## credential it refused is still the one held, in both senses: the session has not been
## replaced, and the token has not been rotated out from under the replay. A reply that arrives after an explicit
## sign-in or sign-out refused a credential that no longer matters. Everything else is the
## backend's own answer and is passed through untouched.
func _result_of_retry(
		retried: ResponseResult, generation: int, rotations: int) -> ResponseResult:
	if not _is_refused(retried):
		return retried
	if _store.rotation_count() != rotations:
		# The token this replay presented was rotated while it was on the wire. A refusal of a
		# superseded token says nothing about the successor now held, and ending the session
		# over it would sign out a player holding a credential that still works.
		_log.debug("ignoring a refusal of a token that has since been rotated")
		return ResponseResult.Failure(AuthError.SessionExpired(_current_expiry()))
	if _session_replaced_since(generation):
		# The refusal belongs to the session this request was authorized under, which is no
		# longer the one held. Clearing now would end the session that replaced it — signing
		# out a player whose own credential was never refused.
		return _replaced_mid_request()
	var expired_at: int = _current_expiry()
	_forget_lapsed()
	_announce_lapse(AuthError.SessionExpired(expired_at))
	return ResponseResult.Failure(AuthError.SessionExpired(expired_at))

## Exchanges a credential for a backend session.
##
## Exhaustive over [CredentialResult]; the trailing return exists only because the analyser
## cannot see that the match is total.
async func _exchange(
		credential_result: CredentialResult,
		generation: int,
		backend_generation: int) -> SessionResult:
	match credential_result:
		CredentialResult.Failure(error):
			return SessionResult.Failure(error)
		CredentialResult.Success(credential):
			return await _exchange_credential(credential, generation, backend_generation)
	return SessionResult.Failure(AuthError.InvalidResponse(
			"the backend reported a credential result this subsystem does not understand"))

## Posts one credential to the exchange endpoint and installs whatever session came back.
##
## The request carries no bearer credential: there is no session yet, and the credential
## being exchanged travels in the body.
##
## The backend generation is checked before the request rather than after it, unlike every
## other guard here. Those decide what to do with an answer already obtained; this one decides
## whether to disclose the credential at all, and there is no undoing that once it is sent.
async func _exchange_credential(
		credential: Credential, generation: int, backend_generation: int) -> SessionResult:
	if backend_generation != _backend_generation:
		_log.debug("not exchanging a credential at a backend configured since it was obtained")
		return SessionResult.Failure(AuthError.Cancelled)
	var provider: Provider = Credential.provider_of(credential)
	_log.debug("exchanging a credential for provider %d" % provider)
	var response: ResponseResult = await _client.request(
			HttpMethod.POST, _client.config().exchange_path, _exchange_body(credential))
	var result: SessionResult = _session_of(response, provider)
	match result:
		SessionResult.Failure(_error):
			return result
		SessionResult.Success(session):
			if _session_replaced_since(generation):
				return _superseded()
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
				"audience": _resolved_audience(audience),
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
