namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth.internal

class_name JwtTests
extends RefCounted
uses Test

## header.payload.signature — payload decodes to {"sub":"user-123","exp":1750000000}
const _VALID: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func test_subject_is_decoded() -> void:
	Expect.that(Jwt.subject_from(_VALID)).to_equal("user-123")

func test_expiry_is_decoded() -> void:
	Expect.that(Jwt.expiry_from(_VALID)).to_equal(1750000000)

func test_empty_token_yields_empty_subject_and_zero_expiry() -> void:
	Expect.that(Jwt.subject_from("")).to_equal("")
	Expect.that(Jwt.expiry_from("")).to_equal(0)

func test_wrong_segment_count_is_rejected() -> void:
	Expect.that(Jwt.subject_from("only.two")).to_equal("")
	Expect.that(Jwt.expiry_from("a.b.c.d")).to_equal(0)

func test_non_json_payload_is_rejected() -> void:
	Expect.that(Jwt.subject_from("aaa.bm90LWpzb24.sig")).to_equal("")

func test_payload_without_claims_yields_defaults() -> void:
	# {} base64url-encoded is "e30"
	Expect.that(Jwt.subject_from("aaa.e30.sig")).to_equal("")
	Expect.that(Jwt.expiry_from("aaa.e30.sig")).to_equal(0)

func test_base64url_alphabet_is_translated() -> void:
	# Payload uses - and _ which must map to + and / before decoding.
	# {"sub":"a-b_c"} -> eyJzdWIiOiJhLWJfYyJ9
	Expect.that(Jwt.subject_from("aaa.eyJzdWIiOiJhLWJfYyJ9.sig")).to_equal("a-b_c")

func test_float_expiry_is_truncated_to_int() -> void:
	# {"exp":1750000000.9} -> eyJleHAiOjE3NTAwMDAwMDAuOX0
	Expect.that(Jwt.expiry_from("aaa.eyJleHAiOjE3NTAwMDAwMDAuOX0.sig")).to_equal(1750000000)
