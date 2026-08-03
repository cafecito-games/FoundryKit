namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name CredentialTests
extends RefCounted
uses Test

const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func test_google_case_carries_token_email_name_and_audience() -> void:
	var credential: Credential = Credential.Google(_TOKEN, "a@b.c", "Ada", "web-id")
	var described: String = ""
	match credential:
		Credential.Google(id_token, email, display_name, audience):
			described = "%s|%s|%s|%d" % [email, display_name, audience, id_token.length()]
		Credential.Apple(_t, _c, _e, _n):
			described = "apple"
		Credential.EmailPassword(_e, _p):
			described = "email"
	Expect.that(described).to_equal("a@b.c|Ada|web-id|%d" % _TOKEN.length())

func test_apple_case_carries_identity_token_and_authorization_code() -> void:
	var credential: Credential = Credential.Apple(_TOKEN, "auth-code", "a@b.c", "Ada Lovelace")
	var described: String = ""
	match credential:
		Credential.Google(_t, _e, _n, _a):
			described = "google"
		Credential.Apple(_identity_token, authorization_code, email, full_name):
			described = "%s|%s|%s" % [authorization_code, email, full_name]
		Credential.EmailPassword(_e, _p):
			described = "email"
	Expect.that(described).to_equal("auth-code|a@b.c|Ada Lovelace")

func test_provider_of_maps_every_case() -> void:
	Expect.that(Credential.provider_of(Credential.Google(_TOKEN, "", "", ""))).to_equal(Provider.GOOGLE)
	Expect.that(Credential.provider_of(Credential.Apple(_TOKEN, "", "", ""))).to_equal(Provider.APPLE)
	Expect.that(Credential.provider_of(Credential.EmailPassword("a@b.c", "pw"))) \
		.to_equal(Provider.EMAIL_PASSWORD)

func test_subject_is_decoded_from_google_id_token() -> void:
	Expect.that(Credential.subject_of(Credential.Google(_TOKEN, "", "", ""))).to_equal("user-123")

func test_subject_is_decoded_from_apple_identity_token() -> void:
	Expect.that(Credential.subject_of(Credential.Apple(_TOKEN, "", "", ""))).to_equal("user-123")

func test_email_password_has_no_subject() -> void:
	Expect.that(Credential.subject_of(Credential.EmailPassword("a@b.c", "pw"))).to_equal("")
