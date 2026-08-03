namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.auth

## Speaks authentication over a plain [HttpTransport].
##
## The one place that knows a bearer token belongs in an [code]Authorization[/code] header
## and that a 401 means the session is gone. [code]core/[/code] deliberately has no
## authorization vocabulary — [HttpOutcome] reports a 401 as nothing more than a status —
## so the translation lives here, on the auth side of that boundary.
##
## A 401 resolves to [code]ResponseResult.Success[/code] carrying an [AuthResponse] with
## [member AuthResponse.session_expired] set, **not** to a [code]Failure[/code]. The
## caller owns the decision of whether to refresh and retry; collapsing a 401 into an
## error here would take that decision away and make single-retry refresh unimplementable.
##
## The transport and the configuration are injected rather than resolved internally, so
## every layer above can be tested against a scripted double with no network.
class_name BackendClient extends RefCounted

## Bounds one backend call. Matches [constant HttpClient.DEFAULT_TIMEOUT_SECONDS].
const DEFAULT_TIMEOUT_SECONDS: float = 30.0

## The status that means the credential presented is no longer accepted.
const _UNAUTHORIZED: int = 401

var _log: FoundryKitLog
var _transport: HttpTransport
var _config: BackendConfig

## Builds a client over [param transport], addressing the backend [param config] names.
func _init(log: FoundryKitLog, transport: HttpTransport, config: BackendConfig) -> void:
	_log = log
	_transport = transport
	_config = config

## Returns the configuration this client addresses, so a caller can resolve endpoint
## paths without holding a second reference to it.
func config() -> BackendConfig:
	return _config

## Sends one request to [param path] on the configured backend and maps how it ended onto
## an auth result.
##
## [param body] is encoded as a JSON request body when it is not null; null sends an empty
## body and no [code]Content-Type[/code]. [param access_token] is injected as a bearer
## credential only when it is non-empty, so an unauthenticated endpoint such as credential
## exchange is not handed a stray header.
##
## Never logs [param access_token], in any form. A truncated or length-prefixed token is
## still a credential disclosure in a log a player can read or a crash reporter can ship.
async func request(
		method: HttpMethod,
		path: String,
		body: Variant,
		access_token: String = "",
		timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS) -> ResponseResult:
	if not _config.is_configured():
		return ResponseResult.Failure(AuthError.Configuration(
				"no backend base URL is configured"))

	var url: String = _config.url_for(path)
	var encoded_body: PackedByteArray = _encode_body(body)
	var headers: PackedStringArray = _headers_for(body, access_token)
	var verb: String = _verb_of(method)
	_log.debug("backend request %s %s" % [verb, url])

	var outcome: HttpOutcome = await _transport.send(
			verb, url, headers, encoded_body, timeout_seconds)
	return _result_of(outcome)

## Maps a transport outcome onto an auth result.
##
## The match is exhaustive over [HttpOutcome] with no [code]_[/code] wildcard, so adding a
## case to that union breaks here rather than silently taking a default branch. The
## trailing return exists only because the analyser cannot see that the match is total.
func _result_of(outcome: HttpOutcome) -> ResponseResult:
	match outcome:
		HttpOutcome.Answered(status_code, body):
			return _result_of_answer(status_code, body)
		HttpOutcome.TransportFailed(detail):
			# Status 0 says "there was no status" — the request never reached a server.
			return ResponseResult.Failure(AuthError.RequestFailed(0, detail))
		HttpOutcome.TimedOut(elapsed_seconds):
			return ResponseResult.Failure(AuthError.TimedOut(elapsed_seconds))
	return ResponseResult.Failure(AuthError.InvalidResponse(
			"the transport reported an outcome this client does not understand"))

## Maps an answered request onto an auth result.
##
## A 2xx and a 401 are both [code]Success[/code]: the request completed and the caller can
## act on what came back. Only a 401 sets [member AuthResponse.session_expired], which is
## the signal the caller uses to decide whether to refresh and retry.
func _result_of_answer(status_code: int, body: PackedByteArray) -> ResponseResult:
	var response: AuthResponse = AuthResponse.new()
	response.transport_ok = true
	response.status_code = status_code
	response.body = body

	if status_code == _UNAUTHORIZED:
		response.session_expired = true
		_log.debug("backend rejected the presented credential")
		return ResponseResult.Success(response)

	if response.is_ok():
		return ResponseResult.Success(response)

	return ResponseResult.Failure(AuthError.RequestFailed(
			status_code, body.get_string_from_utf8()))

## Builds the request headers.
##
## The [code]Authorization[/code] header is added only for a non-empty token: an empty
## bearer credential is not a weaker credential, it is a malformed header that some
## backends reject outright and others log.
func _headers_for(body: Variant, access_token: String) -> PackedStringArray:
	var headers: PackedStringArray = PackedStringArray()
	headers.append("Accept: application/json")
	if body != null:
		headers.append("Content-Type: application/json")
	if not access_token.is_empty():
		headers.append("Authorization: Bearer " + access_token)
	return headers

## Encodes [param body] as UTF-8 JSON, or an empty buffer when it is null.
func _encode_body(body: Variant) -> PackedByteArray:
	if body == null:
		return PackedByteArray()
	return JSON.stringify(body).to_utf8_buffer()

## Maps FoundryKit's [HttpMethod] onto the uppercase verb [HttpTransport] expects.
##
## Exhaustive over [HttpMethod]; the trailing return is the [constant HttpMethod.GET] case
## rather than a fallback for an unknown method.
func _verb_of(method: HttpMethod) -> String:
	match method:
		HttpMethod.POST:
			return "POST"
		HttpMethod.PUT:
			return "PUT"
		HttpMethod.PATCH:
			return "PATCH"
		HttpMethod.DELETE:
			return "DELETE"
		HttpMethod.GET:
			return "GET"
	return "GET"
