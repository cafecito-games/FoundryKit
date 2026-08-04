namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal

## Covers the persisted session record and the origin binding it exists to carry.
##
## The negative cases matter more than the round trip here: a record this build cannot
## fully understand must be refused, because every way of guessing at one ends with tokens
## minted by one backend being presented to another.
class_name AuthStoredSessionTests
extends RefCounted
uses Test

const _ORIGIN: String = "https://api.example.com"

func _record() -> StoredSession:
	var raw: Dictionary[String, Variant] = {
		"scope": "openid email",
		"profile": {"name": "Ada", "ids": [1, 2, 3]},
	}
	var extras: Dictionary[String, Variant] = {
		"tenant": "acmé",
		"greeting": "こんにちは",
	}
	return StoredSession.new(_ORIGIN, "access-token", "refresh-token", Provider.APPLE, raw, extras)

## Names the outcome case without unwrapping it, so a test can assert which branch was
## taken without a payload it does not care about.
func _outcome_name(outcome: StoredSessionOutcome) -> String:
	match outcome:
		StoredSessionOutcome.Parsed(_record_value):
			return "parsed"
		StoredSessionOutcome.Malformed(_detail):
			return "malformed"
		StoredSessionOutcome.VersionUnsupported(_version):
			return "version_unsupported"
	return "unknown"

func _detail_of(outcome: StoredSessionOutcome) -> String:
	match outcome:
		StoredSessionOutcome.Parsed(_record_value):
			return ""
		StoredSessionOutcome.Malformed(detail):
			return detail
		StoredSessionOutcome.VersionUnsupported(_version):
			return ""
	return ""

func _unsupported_version_of(outcome: StoredSessionOutcome) -> int:
	match outcome:
		StoredSessionOutcome.Parsed(_record_value):
			return -1
		StoredSessionOutcome.Malformed(_detail):
			return -1
		StoredSessionOutcome.VersionUnsupported(version):
			return version
	return -1

func _parsed(outcome: StoredSessionOutcome) -> StoredSession:
	match outcome:
		StoredSessionOutcome.Parsed(record):
			return record
		StoredSessionOutcome.Malformed(detail):
			Expect.that("Malformed(%s)" % detail).to_equal("Parsed")
		StoredSessionOutcome.VersionUnsupported(version):
			Expect.that("VersionUnsupported(%d)" % version).to_equal("Parsed")
	return StoredSession.new("", "", "", Provider.GOOGLE, {}, {})

func _round_tripped() -> StoredSession:
	return _parsed(StoredSession.from_bytes(_record().to_bytes()))

func test_the_origin_survives_a_round_trip() -> void:
	Expect.that(_round_tripped().origin).to_equal(_ORIGIN)

func test_tokens_and_provider_survive_a_round_trip() -> void:
	var parsed: StoredSession = _round_tripped()
	Expect.that(parsed.access_token).to_equal("access-token")
	Expect.that(parsed.refresh_token).to_equal("refresh-token")
	Expect.that(parsed.provider).to_equal(Provider.APPLE)
	Expect.that(parsed.schema_version).to_equal(StoredSession.SCHEMA_VERSION)

## Every provider must have a wire name, in both directions. A provider added to the enum
## without one would serialize to a record no build could read back.
func test_every_provider_survives_a_round_trip() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	for candidate: Provider in [Provider.GOOGLE, Provider.APPLE, Provider.EMAIL_PASSWORD]:
		var record: StoredSession = StoredSession.new(_ORIGIN, "a", "r", candidate, raw, extras)
		Expect.that(_parsed(StoredSession.from_bytes(record.to_bytes())).provider).to_equal(candidate)

func test_a_nested_raw_dictionary_survives_a_round_trip() -> void:
	var parsed: StoredSession = _round_tripped()
	Expect.that(str(parsed.raw["scope"])).to_equal("openid email")
	var profile: Variant = parsed.raw["profile"]
	Expect.that(profile is Dictionary).to_be_true()
	var nested: Dictionary = profile
	Expect.that(str(nested["name"])).to_equal("Ada")
	var ids: Variant = nested["ids"]
	Expect.that(ids is Array).to_be_true()
	var id_values: Array = ids
	Expect.that(id_values.size()).to_equal(3)

func test_non_ascii_extras_survive_a_round_trip() -> void:
	var parsed: StoredSession = _round_tripped()
	Expect.that(str(parsed.extras["tenant"])).to_equal("acmé")
	Expect.that(str(parsed.extras["greeting"])).to_equal("こんにちは")

func test_a_round_tripped_record_rebuilds_the_session() -> void:
	var session: AuthSession = _round_tripped().to_session()
	Expect.that(session.access_token).to_equal("access-token")
	Expect.that(session.refresh_token).to_equal("refresh-token")
	Expect.that(session.provider).to_equal(Provider.APPLE)
	Expect.that(str(session.extras["tenant"])).to_equal("acmé")
	Expect.that(str(session.raw["scope"])).to_equal("openid email")

func test_a_record_built_from_a_session_carries_the_given_origin() -> void:
	var raw: Dictionary[String, Variant] = {"scope": "openid"}
	var extras: Dictionary[String, Variant] = {"tenant": "acme"}
	var session: AuthSession = AuthSession.new("a", "r", Provider.GOOGLE, raw, extras)
	var record: StoredSession = StoredSession.from_session(session, _ORIGIN)
	Expect.that(record.origin).to_equal(_ORIGIN)
	Expect.that(record.access_token).to_equal("a")
	Expect.that(str(record.raw["scope"])).to_equal("openid")
	Expect.that(str(record.extras["tenant"])).to_equal("acme")

## A record missing `origin` is malformed, not origin-less. An absent origin that
## deserialized to "" would compare equal to an unconfigured backend's origin and restore
## against it — which is the disclosure this epic exists to prevent.
func test_a_record_without_an_origin_is_malformed() -> void:
	var payload: Dictionary = {
		"schema_version": StoredSession.SCHEMA_VERSION,
		"access_token": "a",
		"refresh_token": "r",
		"provider": "google",
	}
	var bytes: PackedByteArray = JSON.stringify(payload).to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_a_record_with_an_empty_origin_is_malformed() -> void:
	var payload: Dictionary = {
		"schema_version": StoredSession.SCHEMA_VERSION,
		"origin": "",
		"access_token": "a",
		"refresh_token": "r",
		"provider": "google",
	}
	var bytes: PackedByteArray = JSON.stringify(payload).to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_an_unknown_schema_version_is_reported_not_guessed() -> void:
	var payload: Dictionary = {
		"schema_version": 99,
		"origin": _ORIGIN,
		"access_token": "a",
		"refresh_token": "r",
		"provider": "google",
	}
	var bytes: PackedByteArray = JSON.stringify(payload).to_utf8_buffer()
	var outcome: StoredSessionOutcome = StoredSession.from_bytes(bytes)
	Expect.that(_outcome_name(outcome)).to_equal("version_unsupported")
	Expect.that(_unsupported_version_of(outcome)).to_equal(99)

func test_a_record_without_a_schema_version_is_malformed() -> void:
	var payload: Dictionary = {"origin": _ORIGIN, "access_token": "a"}
	var bytes: PackedByteArray = JSON.stringify(payload).to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_empty_bytes_are_malformed() -> void:
	Expect.that(_outcome_name(StoredSession.from_bytes(PackedByteArray()))).to_equal("malformed")

func test_non_json_bytes_are_malformed() -> void:
	var bytes: PackedByteArray = "not a record at all".to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_truncated_bytes_are_malformed() -> void:
	var complete: PackedByteArray = _record().to_bytes()
	var truncated: PackedByteArray = complete.slice(0, complete.size() / 2)
	Expect.that(_outcome_name(StoredSession.from_bytes(truncated))).to_equal("malformed")

func test_json_that_is_not_an_object_is_malformed() -> void:
	var bytes: PackedByteArray = "[1, 2, 3]".to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

## UTF-8 decoding stops at a NUL, so a valid record followed by one and arbitrary rubbish
## would decode to just the valid prefix. Bytes that are not wholly the record are not the
## record.
func test_bytes_with_a_trailing_nul_and_rubbish_are_malformed() -> void:
	var bytes: PackedByteArray = _record().to_bytes()
	bytes.append(0)
	bytes.append_array("{not json".to_utf8_buffer())
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_bytes_that_are_not_valid_utf8_are_malformed() -> void:
	var bytes: PackedByteArray = PackedByteArray([0xFF, 0xFE, 0xFD])
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

## A field of the wrong type is a defective record. Defaulting it away would return a
## session that has quietly lost its refresh token or the backend's own payload.
func test_a_field_of_the_wrong_type_is_malformed() -> void:
	var wrong_typed_values: Dictionary = {
		"origin": 7,
		"access_token": 7,
		"refresh_token": 7,
		"provider": 7,
		"raw": "not an object",
		"extras": "not an object",
	}
	for key: String in wrong_typed_values.keys():
		var payload: Dictionary = {
			"schema_version": StoredSession.SCHEMA_VERSION,
			"origin": _ORIGIN,
			"access_token": "a",
			"refresh_token": "r",
			"provider": "google",
			"raw": {},
			"extras": {},
		}
		payload[key] = wrong_typed_values[key]
		var bytes: PackedByteArray = JSON.stringify(payload).to_utf8_buffer()
		Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_a_record_with_an_unrecognised_provider_is_malformed() -> void:
	var payload: Dictionary = {
		"schema_version": StoredSession.SCHEMA_VERSION,
		"origin": _ORIGIN,
		"access_token": "a",
		"refresh_token": "r",
		"provider": "carrier_pigeon",
	}
	var bytes: PackedByteArray = JSON.stringify(payload).to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_a_record_without_an_access_token_is_malformed() -> void:
	var payload: Dictionary = {
		"schema_version": StoredSession.SCHEMA_VERSION,
		"origin": _ORIGIN,
		"refresh_token": "r",
		"provider": "google",
	}
	var bytes: PackedByteArray = JSON.stringify(payload).to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

## Every rejection detail names fields, never their values. This type is the one place in
## the addon that serializes tokens, so a detail string quoting the bytes it failed to
## parse would put a token into whatever log the caller writes it to.
func test_no_malformed_detail_repeats_a_token() -> void:
	var truncated: PackedByteArray = _record().to_bytes().slice(0, 40)
	var candidates: Array[PackedByteArray] = [
		PackedByteArray(),
		"not a record at all".to_utf8_buffer(),
		truncated,
		JSON.stringify({"schema_version": 1, "access_token": "access-token"}).to_utf8_buffer(),
	]
	for bytes: PackedByteArray in candidates:
		var detail: String = _detail_of(StoredSession.from_bytes(bytes))
		Expect.that(detail.contains("access-token")).to_be_false()
		Expect.that(detail.contains("refresh-token")).to_be_false()

func test_the_string_form_carries_no_token() -> void:
	var text: String = str(_record())
	Expect.that(text.contains("access-token")).to_be_false()
	Expect.that(text.contains("refresh-token")).to_be_false()
	Expect.that(text.contains(_ORIGIN)).to_be_true()

func test_the_record_deep_copies_the_dictionaries_it_is_given() -> void:
	var raw: Dictionary[String, Variant] = {"scope": "openid"}
	var extras: Dictionary[String, Variant] = {"tenant": "acme"}
	var record: StoredSession = StoredSession.new(_ORIGIN, "a", "r", Provider.GOOGLE, raw, extras)
	raw["scope"] = "changed"
	extras["tenant"] = "changed"
	Expect.that(str(record.raw["scope"])).to_equal("openid")
	Expect.that(str(record.extras["tenant"])).to_equal("acme")
