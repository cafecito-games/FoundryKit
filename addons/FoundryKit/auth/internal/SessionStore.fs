namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.auth

## Holds the one active [AuthSession] and renews it exactly once at a time.
##
## Everything above this class asks it for the session rather than keeping one, so there is
## a single answer to "am I signed in" and a single place a renewed session lands.
##
## [b]Single-flight refresh.[/b] A session usually expires while several calls are already
## under way, so every one of them asks to refresh at once. Only the first opens a round;
## the rest join it and are handed that round's result. The alternative is N refresh calls
## against a backend that rotates refresh tokens, where all but one are replaying a token
## the backend has already retired.
##
## Coalescing stops at the session boundary: a round renews the session that was held when
## it opened, so a caller that arrives after [method set_session] or [method clear] waits
## for that round and then opens its own rather than accepting a result belonging to a
## session it never asked about.
##
## [b]The failure that matters.[/b] The dangerous defect here is not a fan-out, it is a
## round that never closes: an in-flight marker left standing by a failed refresh blocks
## every later refresh for the lifetime of the process, with no error and no log — the
## session simply stops being renewable. The marker is therefore cleared in
## [method _finish_refresh], which every round reaches through the same path whether it
## succeeded or failed.
##
## Nothing here is emitted as a signal. This class reports what happened —
## [method rotation_count] included — and [AuthSubsystem] owns the decision of what to
## announce.
##
## State is per-instance. Two stores never see each other's session; the one static member
## is a lifetime registry, holding no session data.
class_name SessionStore extends RefCounted

## Keeps a store with a refresh in flight alive independent of its caller.
##
## [method _run_refresh] is started without being awaited, so nothing but this array holds
## a strong reference to the store while its reply is outstanding. Without it a caller that
## dropped its reference mid-refresh would take the store — and the deferred settlement
## that closes the round — down with it. Each round removes its own entry when it settles.
static var _in_flight: Array[SessionStore] = []

## Resumes [method refresh] for the round opener and every caller that joined it.
##
## Deliberately emitted from [method _finish_refresh] rather than from [method _run_refresh]
## so that the emission happens one message-queue turn after the reply arrives. A joiner
## that has not yet reached its own `await` when the reply lands would otherwise never see
## the emission and would wait forever. This is the same hazard [method NativeRequest._resolve]
## guards against, and the same fix.
signal _refresh_settled(result: SessionResult)

var _log: FoundryKitLog
var _client: BackendClient
var _session: AuthSession? = null
var _refresh_in_flight: bool = false
var _refresh_round: int = 0
var _rotation_count: int = 0

## Counts explicit changes to the held session, so a reply that arrives after one can be
## recognised as stale. See [method _install].
var _session_generation: int = 0

## The value of [member _session_generation] when the in-flight round opened, naming which
## session that round is renewing. See [method refresh].
var _refresh_generation: int = 0

## Builds a store over [param client]. The client is injected so every layer above can be
## driven against a scripted transport with no network.
func _init(log: FoundryKitLog, client: BackendClient) -> void:
	_log = log
	_client = client

## Returns whether a session is currently held.
func has_session() -> bool:
	return _session != null

## Returns the held session, or null when there is none.
func session() -> AuthSession?:
	return _session

## Returns the held access token, or an empty string when no session is held.
func access_token() -> String:
	var current: AuthSession? = _session
	if current == null:
		return ""
	var active: AuthSession = current
	return active.access_token

## Returns the held refresh token, or an empty string when no session is held.
func refresh_token() -> String:
	var current: AuthSession? = _session
	if current == null:
		return ""
	var active: AuthSession = current
	return active.refresh_token

## Counts the refreshes that replaced either token with a different one.
##
## This is how a caller reports rotation without this class emitting anything. Because
## refreshes are single-flight, N concurrent callers see the counter advance exactly once,
## so a caller that remembers the last count it announced announces each rotation once.
##
## Both tokens count, because a backend may rotate them independently: one that returns a
## still-valid access token alongside a new refresh token has rotated a credential its
## holder must not keep using, and counting only the access token would hide that.
## A refresh the backend answers with both tokens unchanged advances nothing — nothing
## rotated, so there is nothing to announce.
func rotation_count() -> int:
	return _rotation_count

## Makes [param new_session] the active session, replacing any previous one.
##
## Ranks above a refresh already in flight: a reply that lands afterwards is discarded
## rather than allowed to overwrite this decision.
func set_session(new_session: AuthSession) -> void:
	_session = new_session
	_session_generation += 1

## Drops the active session.
##
## Ranks above a refresh already in flight, so signing out is not undone moments later by a
## reply that was already on its way.
func clear() -> void:
	_session = null
	_session_generation += 1

## Renews the held session, collapsing concurrent callers into one backend call.
##
## The first caller opens a round; every caller that arrives while that round is
## outstanding joins it and receives its result. Callers are resumed in the order they
## arrived.
##
## Fails without touching the transport when there is nothing to present: no session at
## all, or a session carrying no refresh token. Both report
## [code]AuthError.SessionExpired[/code] — from the caller's point of view the session is
## over, and the only honest distinction, the expiry instant, is what the case carries.
##
## A 401 from the refresh endpoint is [code]SessionExpired[/code] too: the refresh token
## itself has been rejected, so no retry can succeed.
async func refresh() -> SessionResult:
	while _refresh_in_flight:
		# A round renews the session that was held when it opened, and only that one. A
		# caller arriving after set_session or clear replaced that session is asking about a
		# different one, so it must not take the round's result — that would report success
		# for a session no request was ever made for, and would leave a replacement session
		# unrefreshed. It waits for the round to close and then opens its own.
		var renews_my_session: bool = _refresh_generation == _session_generation
		var joined: SessionResult = await _refresh_settled
		if renews_my_session:
			return joined

	var current: AuthSession? = _session
	if current == null:
		return SessionResult.Failure(AuthError.SessionExpired(0))
	var active: AuthSession = current
	if active.refresh_token.is_empty():
		_log.debug("cannot refresh a session that carries no refresh token")
		return SessionResult.Failure(AuthError.SessionExpired(active.expires_at()))

	_refresh_round += 1
	_refresh_generation = _session_generation
	_refresh_in_flight = true
	_in_flight.append(self)
	_run_refresh(_refresh_round, active, _session_generation)

	var result: SessionResult = await _refresh_settled
	return result

## Performs the one backend call a round makes.
##
## Started without being awaited on purpose. The opener awaits [signal _refresh_settled]
## like every joiner, so all of them are resumed from one place in the order they called
## [method refresh]. Awaiting this directly instead would resume the opener ahead of that
## queue, and — when the transport answers without suspending — would complete the opener's
## coroutine before its caller ever reached `await`, which this engine resolves by waiting
## forever.
async func _run_refresh(round_id: int, session: AuthSession, generation: int) -> void:
	var body: Dictionary[String, Variant] = {"refresh_token": session.refresh_token}
	var response: ResponseResult = await _client.request(
			HttpMethod.POST, _client.config().refresh_path, body)
	var result: SessionResult = _session_of(response, session)
	_finish_refresh.call_deferred(round_id, result, session, generation)

## Closes a round: clears the in-flight marker first, then resumes everyone waiting on it.
##
## Clearing before the emission is what keeps a wedge impossible. A caller resumed by this
## emission that immediately asks to refresh again finds no marker and opens a fresh round,
## rather than joining one that has already settled and can never emit again.
##
## [param round_id] guards against a settlement arriving for a round that is no longer the
## current one, which no path produces today and which must not settle anything if one ever
## does.
func _finish_refresh(
		round_id: int,
		result: SessionResult,
		session: AuthSession,
		generation: int) -> void:
	if round_id != _refresh_round or not _refresh_in_flight:
		return
	_refresh_in_flight = false
	_in_flight.erase(self)
	_refresh_settled.emit(_install(result, session, generation))

## Applies a completed round to the store and returns what its callers see.
##
## A failure changes nothing: the previous session stays held, so a caller that can still
## use it is not signed out by one unreachable backend.
func _install(
		result: SessionResult,
		session: AuthSession,
		generation: int) -> SessionResult:
	match result:
		SessionResult.Failure(_error):
			return result
		SessionResult.Success(refreshed):
			if generation != _session_generation:
				# set_session or clear ran while the reply was in flight. That decision was
				# explicit and this reply was already on its way, so the reply loses: a
				# refreshed session must never resurrect one that was signed out.
				_log.debug("discarding a refreshed session the store no longer wants")
				return _current_result()
			_session = refreshed
			if _rotated(session, refreshed):
				_rotation_count += 1
			return result
	return result

## Returns whether a refresh handed back a different credential in either token.
##
## Backends rotate the two independently: some issue a new access token against a
## long-lived refresh token, others rotate the refresh token on every use. Either one
## replacing its predecessor is a rotation the holder of the old value needs to hear about.
func _rotated(previous: AuthSession, refreshed: AuthSession) -> bool:
	if refreshed.access_token != previous.access_token:
		return true
	return refreshed.refresh_token != previous.refresh_token

## Reports the session the store holds right now, for a caller whose own round was
## superseded.
func _current_result() -> SessionResult:
	var current: AuthSession? = _session
	if current == null:
		return SessionResult.Failure(AuthError.SessionExpired(0))
	var active: AuthSession = current
	return SessionResult.Success(active)

## Maps a backend reply onto a session result.
##
## Exhaustive over [ResponseResult] with no [code]_[/code] wildcard; the trailing return
## exists only because the analyser cannot see that the match is total.
func _session_of(response: ResponseResult, session: AuthSession) -> SessionResult:
	match response:
		ResponseResult.Failure(error):
			return SessionResult.Failure(error)
		ResponseResult.Success(answer):
			if answer.session_expired:
				_log.debug("the backend rejected the refresh token")
				return SessionResult.Failure(AuthError.SessionExpired(session.expires_at()))
			return _session_from_body(answer, session)
	return SessionResult.Failure(AuthError.InvalidResponse(
			"the backend client reported a result this store does not understand"))

## Builds the renewed session from a successful reply.
##
## A backend that does not rotate refresh tokens answers with an access token alone; the
## previous refresh token is kept in that case rather than being blanked, which would leave
## the session unrenewable from then on. The provider and the extras come from the session
## being renewed — a refresh reply restates neither.
func _session_from_body(response: AuthResponse, session: AuthSession) -> SessionResult:
	var parsed: Variant = response.json()
	if not (parsed is Dictionary):
		return SessionResult.Failure(AuthError.InvalidResponse(
				"the refresh response was not a JSON object"))
	var payload: Dictionary = parsed
	var access_token: String = str(payload.get("access_token", ""))
	if access_token.is_empty():
		return SessionResult.Failure(AuthError.MissingField("access_token"))
	var refreshed_token: String = str(payload.get("refresh_token", ""))
	if refreshed_token.is_empty():
		refreshed_token = session.refresh_token
	var raw: Dictionary[String, Variant] = {}
	for key: Variant in payload.keys():
		raw[str(key)] = payload[key]
	return SessionResult.Success(AuthSession.new(
			access_token, refreshed_token, session.provider, raw, session.extras))
