namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name AuthSessionTests
extends RefCounted
uses Test

## exp = 1750000000
const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func _session() -> AuthSession:
	var raw: Dictionary[String, Variant] = {"scope": "openid"}
	var extras: Dictionary[String, Variant] = {"tenant": "acme"}
	return AuthSession.new("access", "refresh", Provider.APPLE, raw, extras)

func test_fields_are_stored() -> void:
	var session: AuthSession = _session()
	Expect.that(session.access_token).to_equal("access")
	Expect.that(session.refresh_token).to_equal("refresh")
	Expect.that(session.provider).to_equal(Provider.APPLE)

func test_to_dictionary_includes_reserved_keys_and_extras() -> void:
	var payload: Dictionary[String, Variant] = _session().to_dictionary()
	Expect.that(str(payload["access_token"])).to_equal("access")
	Expect.that(str(payload["refresh_token"])).to_equal("refresh")
	Expect.that(str(payload["tenant"])).to_equal("acme")

func test_round_trip_preserves_tokens_and_extras() -> void:
	var restored: AuthSession = AuthSession.from_dictionary(_session().to_dictionary(), Provider.APPLE)
	Expect.that(restored.access_token).to_equal("access")
	Expect.that(restored.refresh_token).to_equal("refresh")
	Expect.that(str(restored.extras["tenant"])).to_equal("acme")

func test_from_dictionary_excludes_reserved_keys_from_extras() -> void:
	var restored: AuthSession = AuthSession.from_dictionary(_session().to_dictionary(), Provider.APPLE)
	Expect.that(restored.extras.has("access_token")).to_be_false()
	Expect.that(restored.extras.has("refresh_token")).to_be_false()
	Expect.that(restored.extras.has("raw")).to_be_false()

func test_duplicate_is_a_deep_copy() -> void:
	var original: AuthSession = _session()
	var copy: AuthSession = original.duplicate_session()
	copy.raw["scope"] = "changed"
	Expect.that(str(original.raw["scope"])).to_equal("openid")

func test_expiry_is_read_from_the_access_token() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var session: AuthSession = AuthSession.new(_TOKEN, "refresh", Provider.GOOGLE, raw, extras)
	Expect.that(session.expires_at()).to_equal(1750000000)
	Expect.that(session.is_expired_at(1750000001)).to_be_true()
	Expect.that(session.is_expired_at(1749999999)).to_be_false()

func test_token_without_expiry_never_expires() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var session: AuthSession = AuthSession.new("opaque", "refresh", Provider.GOOGLE, raw, extras)
	Expect.that(session.expires_at()).to_equal(0)
	Expect.that(session.is_expired_at(9999999999)).to_be_false()
