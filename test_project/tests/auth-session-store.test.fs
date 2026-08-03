namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.tests.support

## Covers the session authority and, above all, its single-flight refresh.
##
## The assertions that matter here are the [member FakeHttpClient.send_count] ones. A
## refresh that fans out into N backend calls still passes every happy-path assertion, and
## so does a refresh whose in-flight marker is never cleared — right up until the session
## needs renewing a second time and silently never can.
class_name AuthSessionStoreTests
extends RefCounted
uses Test

## exp = 1750000000.
const _EXPIRED_TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

const _REFRESH_TOKEN: String = "refresh-token-value"

var _transport: FakeHttpClient
var _config: BackendConfig
var _log: FoundryKitLog
var _store: SessionStore

func before_each() -> void:
	_transport = FakeHttpClient.new()
	_config = BackendConfig.new("https://api.example.com")
	_log = FoundryKitLog.new("test")
	_store = SessionStore.new(_log, BackendClient.new(_log, _transport, _config))

func test_a_new_store_holds_no_session() -> void:
	Expect.that(_store.has_session()).to_be_false()
	Expect.that(_store.access_token()).to_equal("")

func test_setting_a_session_makes_it_active() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	Expect.that(_store.has_session()).to_be_true()
	Expect.that(_store.access_token()).to_equal("access-one")

func test_only_the_most_recent_session_is_held() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_store.set_session(_session("access-two", "refresh-two"))
	Expect.that(_store.access_token()).to_equal("access-two")

func test_clearing_drops_the_session() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_store.clear()
	Expect.that(_store.has_session()).to_be_false()
	Expect.that(_store.access_token()).to_equal("")

func test_the_store_keeps_a_copy_of_the_session_it_is_given() -> void:
	# An AuthSession is mutable. If the store kept the caller's instance, changing a token
	# on it would move the store off the session it believes it holds without ever passing
	# through set_session, which is what the stale-reply guard is keyed on.
	var given: AuthSession = _session("access-one", _REFRESH_TOKEN)
	_store.set_session(given)
	given.access_token = "tampered"
	Expect.that(_store.access_token()).to_equal("access-one")

func test_the_session_handed_out_is_a_copy() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	var handed_out: AuthSession? = _store.session()
	Expect.that(handed_out != null).to_be_true()
	var taken: AuthSession = handed_out
	taken.access_token = "tampered"
	Expect.that(_store.access_token()).to_equal("access-one")

func test_a_refreshed_session_is_handed_out_as_a_copy() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var result: SessionResult = await _store.refresh()
	match result:
		SessionResult.Success(refreshed):
			refreshed.access_token = "tampered"
		SessionResult.Failure(_error):
			Expect.that("failure").to_equal("success")
	Expect.that(_store.access_token()).to_equal("fresh-access-token")

func test_each_caller_of_one_round_gets_its_own_session() -> void:
	# One round settles with one payload. Handing that same mutable session to every caller
	# would let the first to change a token on it change what the others are about to read.
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var first: Coroutine[SessionResult] = _store.refresh()
	var second: Coroutine[SessionResult] = _store.refresh()
	match await first:
		SessionResult.Success(session):
			session.access_token = "tampered"
		SessionResult.Failure(_error):
			Expect.that("failure").to_equal("success")
	Expect.that(_describe(await second)).to_equal("ok:fresh-access-token")
	Expect.that(_store.access_token()).to_equal("fresh-access-token")

func test_a_store_without_a_session_hands_out_null() -> void:
	Expect.that(_store.session() == null).to_be_true()

func test_two_stores_do_not_share_session_state() -> void:
	var other: SessionStore = SessionStore.new(
			_log, BackendClient.new(_log, FakeHttpClient.new(), _config))
	_store.set_session(_session("mine", _REFRESH_TOKEN))
	Expect.that(other.has_session()).to_be_false()
	other.set_session(_session("theirs", _REFRESH_TOKEN))
	Expect.that(_store.access_token()).to_equal("mine")
	Expect.that(other.access_token()).to_equal("theirs")

func test_concurrent_refreshes_make_exactly_one_backend_call() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	# Every call must be started before any is awaited — awaiting one at a time would let
	# each finish before the next began, which no amount of single-flight logic is needed
	# to satisfy.
	var first: Coroutine[SessionResult] = _store.refresh()
	var second: Coroutine[SessionResult] = _store.refresh()
	var third: Coroutine[SessionResult] = _store.refresh()
	var results: Array[SessionResult] = [await first, await second, await third]
	Expect.that(_transport.send_count).to_equal(1)
	for result: SessionResult in results:
		Expect.that(_describe(result)).to_equal("ok:fresh-access-token")

func test_a_failed_refresh_lets_a_later_refresh_try_again() -> void:
	# The worst failure mode in this file: an in-flight marker left standing by a failure
	# blocks every future refresh for the lifetime of the process, silently.
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.TransportFailed("could not resolve the host name"))
	var failed: SessionResult = await _store.refresh()
	Expect.that(_describe(failed)).to_equal(
			"fail:request_failed:0:could not resolve the host name")
	Expect.that(_transport.send_count).to_equal(1)

	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var pending: Coroutine[SessionResult] = _store.refresh()
	# Checked before awaiting. A wedged marker makes this second call join a round that has
	# already settled and can never emit again, so awaiting it would hang the suite instead
	# of failing it. Reporting the call count here turns that into an ordinary failure.
	if _transport.send_count != 2:
		Expect.that(_transport.send_count).to_equal(2)
		return
	var recovered: SessionResult = await pending
	Expect.that(_describe(recovered)).to_equal("ok:fresh-access-token")
	Expect.that(_transport.send_count).to_equal(2)

func test_concurrent_refreshes_after_a_failed_one_still_collapse_into_one_call() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.TransportFailed("could not resolve the host name"))
	await _store.refresh()

	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var first: Coroutine[SessionResult] = _store.refresh()
	var second: Coroutine[SessionResult] = _store.refresh()
	var results: Array[SessionResult] = [await first, await second]
	Expect.that(_transport.send_count).to_equal(2)
	for result: SessionResult in results:
		Expect.that(_describe(result)).to_equal("ok:fresh-access-token")

func test_a_failed_refresh_keeps_the_existing_session() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.TimedOut(9.5))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("fail:timed_out:9.5")
	Expect.that(_store.access_token()).to_equal(_EXPIRED_TOKEN)
	Expect.that(_store.rotation_count()).to_equal(0)

func test_a_successful_refresh_replaces_the_session_and_counts_a_rotation() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("ok:fresh-access-token")
	Expect.that(_store.access_token()).to_equal("fresh-access-token")
	Expect.that(_store.refresh_token()).to_equal("fresh-refresh-token")
	Expect.that(_store.rotation_count()).to_equal(1)

func test_concurrent_refreshes_count_exactly_one_rotation() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var first: Coroutine[SessionResult] = _store.refresh()
	var second: Coroutine[SessionResult] = _store.refresh()
	await first
	await second
	Expect.that(_store.rotation_count()).to_equal(1)

func test_a_refresh_returning_both_tokens_unchanged_counts_no_rotation() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _json({
		"access_token": "access-one",
		"refresh_token": _REFRESH_TOKEN,
	})))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("ok:access-one")
	Expect.that(_store.rotation_count()).to_equal(0)

func test_a_rotated_refresh_token_counts_a_rotation_on_its_own() -> void:
	# Backends rotate the two tokens independently. One that hands back the same access
	# token with a new refresh token has still retired a credential its holder must stop
	# presenting, so that has to be reported.
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _json({
		"access_token": "access-one",
		"refresh_token": "rotated-refresh-token",
	})))
	await _store.refresh()
	Expect.that(_store.refresh_token()).to_equal("rotated-refresh-token")
	Expect.that(_store.rotation_count()).to_equal(1)

func test_a_refresh_keeps_the_previous_refresh_token_when_the_backend_omits_one() -> void:
	# A backend that does not rotate refresh tokens returns only a new access token.
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _json({"access_token": "access-two"})))
	await _store.refresh()
	Expect.that(_store.refresh_token()).to_equal(_REFRESH_TOKEN)

func test_a_refresh_preserves_the_provider() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var result: SessionResult = await _store.refresh()
	match result:
		SessionResult.Success(session):
			Expect.that(session.provider).to_equal(Provider.APPLE)
		SessionResult.Failure(_error):
			Expect.that("failure").to_equal("success")

func test_refreshing_without_a_refresh_token_fails_fast() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, ""))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("fail:session_expired:1750000000")
	Expect.that(_transport.send_count).to_equal(0)

func test_refreshing_without_a_session_fails_fast() -> void:
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("fail:session_expired:0")
	Expect.that(_transport.send_count).to_equal(0)

func test_a_401_on_refresh_reports_the_session_as_expired() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("fail:session_expired:1750000000")
	Expect.that(_transport.send_count).to_equal(1)

func test_a_rejected_refresh_token_ends_the_session() -> void:
	# The refresh token is the only credential a refresh can present. Once the backend has
	# refused it, holding the session would leave has_session() answering yes while every
	# later refresh replays a credential that has already been retired.
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	await _store.refresh()
	Expect.that(_store.has_session()).to_be_false()
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("fail:session_expired:0")
	Expect.that(_transport.send_count).to_equal(1)

func test_a_rejection_arriving_after_a_replacement_leaves_the_new_session_alone() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var pending: Coroutine[SessionResult] = _store.refresh()
	_store.set_session(_session("access-b", "refresh-b"))
	await pending
	Expect.that(_store.access_token()).to_equal("access-b")

func test_a_response_without_an_access_token_is_a_missing_field() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _json({"token_type": "Bearer"})))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal("fail:missing_field:access_token")

func test_a_non_string_access_token_is_an_invalid_response() -> void:
	# Stringifying whatever arrived would turn a number into a plausible-looking credential
	# and then present it as a bearer token on every later request.
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _json({"access_token": 12345})))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal(
			"fail:invalid_response:the refresh response carried a non-string access_token")
	Expect.that(_store.access_token()).to_equal("access-one")

func test_a_non_string_refresh_token_is_an_invalid_response() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _json({
		"access_token": "access-two",
		"refresh_token": {"nested": true},
	})))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result)).to_equal(
			"fail:invalid_response:the refresh response carried a non-string refresh_token")
	Expect.that(_store.refresh_token()).to_equal(_REFRESH_TOKEN)

func test_a_response_that_is_not_json_is_an_invalid_response() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, "not json at all".to_utf8_buffer()))
	var result: SessionResult = await _store.refresh()
	Expect.that(_describe(result).begins_with("fail:invalid_response")).to_be_true()

func test_the_refresh_posts_the_refresh_token_to_the_configured_path() -> void:
	_store.set_session(_session("access-one", _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	await _store.refresh()
	Expect.that(_transport.last_method).to_equal("POST")
	Expect.that(_transport.last_url).to_equal(_config.url_for(_config.refresh_path))
	Expect.that(_transport.last_body.get_string_from_utf8()).to_equal(
			"{\"refresh_token\":\"%s\"}" % _REFRESH_TOKEN)

func test_the_refresh_request_carries_no_authorization_header() -> void:
	# The access token being refreshed is expired by definition; presenting it would earn a
	# 401 from a backend that checks it. The refresh token in the body is the credential.
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	await _store.refresh()
	var carried: bool = false
	for header: String in _transport.last_headers:
		if header.to_lower().begins_with("authorization:"):
			carried = true
	Expect.that(carried).to_be_false()

func test_a_second_refresh_presents_the_rotated_refresh_token() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	await _store.refresh()
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	await _store.refresh()
	Expect.that(_transport.last_body.get_string_from_utf8()).to_equal(
			"{\"refresh_token\":\"fresh-refresh-token\"}")

func test_clearing_during_a_refresh_drops_the_refreshed_session() -> void:
	# Signing out mid-refresh must not be undone by the reply that was already in flight.
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var pending: Coroutine[SessionResult] = _store.refresh()
	_store.clear()
	var result: SessionResult = await pending
	Expect.that(_describe(result)).to_equal("fail:session_expired:0")
	Expect.that(_store.has_session()).to_be_false()
	Expect.that(_store.rotation_count()).to_equal(0)

func test_setting_a_session_during_a_refresh_wins_over_the_reply() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var pending: Coroutine[SessionResult] = _store.refresh()
	_store.set_session(_session("explicit-access-token", "explicit-refresh-token"))
	var result: SessionResult = await pending
	Expect.that(_describe(result)).to_equal("ok:explicit-access-token")
	Expect.that(_store.access_token()).to_equal("explicit-access-token")

func test_a_refresh_asked_after_a_replacement_does_not_join_the_previous_round() -> void:
	# The round already in flight is renewing the session that was just replaced. A caller
	# arriving afterwards is asking about the replacement, so taking that round's result
	# would report success for a session no request was ever made for — and would leave the
	# replacement, which may itself be expired, unrenewed.
	_store.set_session(_session(_EXPIRED_TOKEN, "refresh-a"))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var first: Coroutine[SessionResult] = _store.refresh()
	_store.set_session(_session("access-b", "refresh-b"))
	_transport.enqueue(HttpOutcome.Answered(200, _json({
		"access_token": "access-c",
		"refresh_token": "refresh-c",
	})))
	var second: Coroutine[SessionResult] = _store.refresh()
	Expect.that(_describe(await first)).to_equal("ok:access-b")
	Expect.that(_describe(await second)).to_equal("ok:access-c")
	Expect.that(_transport.send_count).to_equal(2)
	Expect.that(_transport.last_body.get_string_from_utf8()).to_equal(
			"{\"refresh_token\":\"refresh-b\"}")
	Expect.that(_store.access_token()).to_equal("access-c")

func test_a_refresh_asked_after_a_clear_does_not_join_the_previous_round() -> void:
	_store.set_session(_session(_EXPIRED_TOKEN, _REFRESH_TOKEN))
	_transport.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var first: Coroutine[SessionResult] = _store.refresh()
	_store.clear()
	var second: Coroutine[SessionResult] = _store.refresh()
	Expect.that(_describe(await first)).to_equal("fail:session_expired:0")
	Expect.that(_describe(await second)).to_equal("fail:session_expired:0")
	# The signed-out store has nothing to present, so the second call reaches no backend.
	Expect.that(_transport.send_count).to_equal(1)
	Expect.that(_store.has_session()).to_be_false()

func test_an_unconfigured_backend_fails_without_a_request() -> void:
	var store: SessionStore = SessionStore.new(
			_log, BackendClient.new(_log, _transport, BackendConfig.new()))
	store.set_session(_session("access-one", _REFRESH_TOKEN))
	var result: SessionResult = await store.refresh()
	Expect.that(_describe(result).begins_with("fail:configuration:")).to_be_true()
	Expect.that(_transport.send_count).to_equal(0)

## Builds a session on a fixed provider, so a refresh preserving it is observable.
func _session(access_token: String, refresh_token: String) -> AuthSession:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	return AuthSession.new(access_token, refresh_token, Provider.APPLE, raw, extras)

func _fresh_session_json() -> PackedByteArray:
	return _json({
		"access_token": "fresh-access-token",
		"refresh_token": "fresh-refresh-token",
	})

func _json(payload: Dictionary) -> PackedByteArray:
	return JSON.stringify(payload).to_utf8_buffer()

## Renders a result as a stable string, so a single assertion covers both the case and its
## payload.
func _describe(result: SessionResult) -> String:
	match result:
		SessionResult.Success(session):
			return "ok:" + session.access_token
		SessionResult.Failure(error):
			return "fail:" + _describe_error(error)
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
