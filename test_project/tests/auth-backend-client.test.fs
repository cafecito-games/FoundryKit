namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.tests.support

class_name AuthBackendClientTests
extends RefCounted
uses Test

const _TOKEN: String = "access-token-value"

var _transport: FakeHttpClient
var _config: BackendConfig
var _log: FoundryKitLog
var _client: BackendClient

func before_each() -> void:
	_transport = FakeHttpClient.new()
	_config = BackendConfig.new("https://api.example.com")
	_log = FoundryKitLog.new("test")
	_log.set_level(LogLevel.DEBUG)
	_log.set_capture_enabled(true)
	_client = BackendClient.new(_log, _transport, _config)

func test_a_2xx_answer_is_a_successful_response() -> void:
	_transport.enqueue(HttpOutcome.Answered(200, "{\"ok\":true}".to_utf8_buffer()))
	var result: ResponseResult = await _client.request(HttpMethod.GET, "/me", null, _TOKEN)
	Expect.that(_describe(result)).to_equal("ok:200")
	match result:
		ResponseResult.Success(response):
			Expect.that(response.is_ok()).to_be_true()
			Expect.that(response.transport_ok).to_be_true()
			Expect.that(response.session_expired).to_be_false()
		ResponseResult.Failure(_error):
			Expect.that(false).to_be_true()

func test_a_401_is_a_success_marked_session_expired_not_a_failure() -> void:
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: ResponseResult = await _client.request(HttpMethod.GET, "/me", null, _TOKEN)
	match result:
		ResponseResult.Success(response):
			Expect.that(response.session_expired).to_be_true()
			Expect.that(response.status_code).to_equal(401)
			Expect.that(response.transport_ok).to_be_true()
			Expect.that(response.is_ok()).to_be_false()
		ResponseResult.Failure(_error):
			Expect.that("failure").to_equal("success with session_expired")

func test_another_non_2xx_status_fails_with_the_status_and_body() -> void:
	_transport.enqueue(HttpOutcome.Answered(500, "boom".to_utf8_buffer()))
	var result: ResponseResult = await _client.request(HttpMethod.GET, "/me", null, _TOKEN)
	Expect.that(_describe(result)).to_equal("fail:request_failed:500:boom")

func test_a_transport_failure_reports_status_zero_and_the_detail() -> void:
	_transport.enqueue(HttpOutcome.TransportFailed("could not resolve the host name"))
	var result: ResponseResult = await _client.request(HttpMethod.GET, "/me", null, _TOKEN)
	Expect.that(_describe(result)).to_equal(
			"fail:request_failed:0:could not resolve the host name")

func test_a_timeout_reports_the_elapsed_time() -> void:
	_transport.enqueue(HttpOutcome.TimedOut(12.5))
	var result: ResponseResult = await _client.request(HttpMethod.GET, "/me", null, _TOKEN)
	Expect.that(_describe(result)).to_equal("fail:timed_out:12.5")

func test_a_non_empty_token_produces_exactly_one_authorization_header() -> void:
	_transport.enqueue(HttpOutcome.Answered(204, PackedByteArray()))
	await _client.request(HttpMethod.GET, "/me", null, _TOKEN)
	var authorizations: Array[String] = _headers_named("Authorization")
	Expect.that(authorizations.size()).to_equal(1)
	Expect.that(authorizations[0]).to_equal("Authorization: Bearer " + _TOKEN)

func test_an_empty_token_produces_no_authorization_header() -> void:
	_transport.enqueue(HttpOutcome.Answered(204, PackedByteArray()))
	await _client.request(HttpMethod.GET, "/me", null, "")
	Expect.that(_headers_named("Authorization").size()).to_equal(0)

func test_the_token_is_never_logged() -> void:
	_transport.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	await _client.request(HttpMethod.POST, "/me", {"note": "hi"}, _TOKEN)
	var leaked: bool = false
	for line: String in _log.captured():
		if line.contains(_TOKEN):
			leaked = true
	Expect.that(leaked).to_be_false()

func test_every_method_maps_to_its_verb() -> void:
	Expect.that(await _verb_of(HttpMethod.GET)).to_equal("GET")
	Expect.that(await _verb_of(HttpMethod.POST)).to_equal("POST")
	Expect.that(await _verb_of(HttpMethod.PUT)).to_equal("PUT")
	Expect.that(await _verb_of(HttpMethod.PATCH)).to_equal("PATCH")
	Expect.that(await _verb_of(HttpMethod.DELETE)).to_equal("DELETE")

func test_the_url_is_built_through_the_backend_configuration() -> void:
	_config = BackendConfig.new("https://api.example.com/")
	_client = BackendClient.new(_log, _transport, _config)
	_transport.enqueue(HttpOutcome.Answered(204, PackedByteArray()))
	await _client.request(HttpMethod.GET, "/v1/auth/exchange", null, "")
	Expect.that(_transport.last_url).to_equal(
			_config.url_for("/v1/auth/exchange"))
	Expect.that(_transport.last_url).to_equal("https://api.example.com/v1/auth/exchange")

func test_a_dictionary_body_is_sent_as_json_with_a_content_type() -> void:
	_transport.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	await _client.request(HttpMethod.POST, "/me", {"name": "ada"}, "")
	Expect.that(_transport.last_body.get_string_from_utf8()).to_equal("{\"name\":\"ada\"}")
	Expect.that(_headers_named("Content-Type").size()).to_equal(1)

func test_a_null_body_is_sent_empty_without_a_content_type() -> void:
	_transport.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	await _client.request(HttpMethod.GET, "/me", null, "")
	Expect.that(_transport.last_body.is_empty()).to_be_true()
	Expect.that(_headers_named("Content-Type").size()).to_equal(0)

func test_an_unconfigured_backend_fails_without_a_request() -> void:
	_client = BackendClient.new(_log, _transport, BackendConfig.new())
	var result: ResponseResult = await _client.request(HttpMethod.GET, "/me", null, _TOKEN)
	Expect.that(_describe(result).begins_with("fail:configuration:")).to_be_true()
	Expect.that(_transport.send_count).to_equal(0)

## Returns the verb the transport was handed for [param method].
async func _verb_of(method: HttpMethod) -> String:
	_transport.reset()
	_transport.enqueue(HttpOutcome.Answered(204, PackedByteArray()))
	await _client.request(method, "/me", null, "")
	return _transport.last_method

## Returns every recorded header whose name matches [param name], case-insensitively.
func _headers_named(name: String) -> Array[String]:
	var matches: Array[String] = []
	var prefix: String = name.to_lower() + ":"
	for header: String in _transport.last_headers:
		if header.to_lower().begins_with(prefix):
			matches.append(header)
	return matches

## Renders a result as a stable string, so a single assertion covers both the case and
## its payload.
func _describe(result: ResponseResult) -> String:
	match result:
		ResponseResult.Success(response):
			return "ok:%d" % response.status_code
		ResponseResult.Failure(error):
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
		AuthError.InvalidResponse(_detail):
			return "invalid_response"
		AuthError.MissingField(_field):
			return "missing_field"
		AuthError.SessionExpired(_expired_at):
			return "session_expired"
		AuthError.TimedOut(elapsed_seconds):
			return "timed_out:%s" % elapsed_seconds
	return "unknown"
