namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name AuthResponseTests
extends RefCounted
uses Test

func _response(status: int, text: String) -> AuthResponse:
	var response: AuthResponse = AuthResponse.new()
	response.transport_ok = true
	response.status_code = status
	response.body = text.to_utf8_buffer()
	return response

func test_http_method_values_are_explicit() -> void:
	Expect.that(HttpMethod.GET).to_equal(0)
	Expect.that(HttpMethod.POST).to_equal(1)
	Expect.that(HttpMethod.PUT).to_equal(2)
	Expect.that(HttpMethod.PATCH).to_equal(3)
	Expect.that(HttpMethod.DELETE).to_equal(4)

func test_two_hundred_is_ok() -> void:
	Expect.that(_response(200, "{}").is_ok()).to_be_true()

func test_two_ninety_nine_is_ok_and_three_hundred_is_not() -> void:
	Expect.that(_response(299, "").is_ok()).to_be_true()
	Expect.that(_response(300, "").is_ok()).to_be_false()

func test_transport_failure_is_never_ok() -> void:
	var response: AuthResponse = _response(200, "{}")
	response.transport_ok = false
	Expect.that(response.is_ok()).to_be_false()

func test_json_body_is_parsed() -> void:
	var parsed: Variant = _response(200, '{"token":"abc"}').json()
	Expect.that(parsed is Dictionary).to_be_true()
	var claims: Dictionary = parsed
	Expect.that(str(claims["token"])).to_equal("abc")

func test_empty_body_yields_null() -> void:
	Expect.that(_response(204, "").json() == null).to_be_true()

func test_non_json_body_yields_null() -> void:
	Expect.that(_response(200, "not json").json() == null).to_be_true()

func test_json_array_yields_null_because_a_dictionary_is_expected() -> void:
	Expect.that(_response(200, "[1,2]").json() == null).to_be_true()
