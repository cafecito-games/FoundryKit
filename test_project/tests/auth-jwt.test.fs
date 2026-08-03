namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth.internal

class_name JwtTests
extends RefCounted
uses Test

## header.payload.signature — payload decodes to {"sub":"user-123","exp":1750000000}
const _VALID: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

## Payload decodes to {"sub":"user-123"} — a well-formed JWT carrying no `exp` claim.
const _WITHOUT_EXP: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyJ9.sig"

## Payload decodes to {"exp":0} — expired at the Unix epoch.
const _EXP_ZERO: String = "eyJhbGciOiJSUzI1NiJ9.eyJleHAiOjB9.sig"

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

func test_readable_expiry_is_reported() -> void:
	Expect.that(Jwt.has_expiry(_VALID)).to_be_true()

## `exp: 0` is a real claim meaning "expired at the epoch"; its absence means "unknown".
## Returning 0 for both made them indistinguishable — see #57.
func test_absent_exp_reports_no_expiry() -> void:
	Expect.that(Jwt.has_expiry(_WITHOUT_EXP)).to_be_false()
	Expect.that(Jwt.expiry_from(_WITHOUT_EXP)).to_equal(0)

func test_explicit_zero_exp_reports_an_expiry() -> void:
	Expect.that(Jwt.has_expiry(_EXP_ZERO)).to_be_true()
	Expect.that(Jwt.expiry_from(_EXP_ZERO)).to_equal(0)

func test_opaque_token_reports_no_expiry() -> void:
	Expect.that(Jwt.has_expiry("not-a-jwt")).to_be_false()
	Expect.that(Jwt.has_expiry("")).to_be_false()

func test_non_numeric_exp_reports_no_expiry() -> void:
	# {"exp":"soon"} -> eyJleHAiOiJzb29uIn0
	Expect.that(Jwt.has_expiry("aaa.eyJleHAiOiJzb29uIn0.sig")).to_be_false()

func test_string_claims_are_decoded() -> void:
	# {"email":"ada@example.com","name":"Ada Lovelace"}
	var token: String = "aaa.eyJlbWFpbCI6ImFkYUBleGFtcGxlLmNvbSIsIm5hbWUiOiJBZGEgTG92ZWxhY2UifQ.sig"
	Expect.that(Jwt.string_claim_from(token, "email")).to_equal("ada@example.com")
	Expect.that(Jwt.string_claim_from(token, "name")).to_equal("Ada Lovelace")

func test_an_absent_or_undecodable_string_claim_is_empty() -> void:
	Expect.that(Jwt.string_claim_from(_VALID, "email")).to_equal("")
	Expect.that(Jwt.string_claim_from("", "email")).to_equal("")
	Expect.that(Jwt.string_claim_from("not-a-jwt", "email")).to_equal("")

## A claim that is present but is not a string is about to be shown to a player, so it is
## reported absent rather than rendered.
func test_a_non_string_claim_is_reported_absent() -> void:
	# {"name":["Ada"]} -> eyJuYW1lIjpbIkFkYSJdfQ
	Expect.that(Jwt.string_claim_from("aaa.eyJuYW1lIjpbIkFkYSJdfQ.sig", "name")).to_equal("")
