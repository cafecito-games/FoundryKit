namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth

## The persisted form of an [AuthSession], bound to the backend origin that issued it.
##
## [b][member origin] is why this type exists.[/b] The in-memory protection against
## restoring a session under a backend that did not mint it lives in [AuthSubsystem] and
## dies with the process; storage introduces exactly the case it cannot cover — start,
## configure a different backend, restore. So the origin travels inside the bytes that go
## to secure storage, which is the only form of it a restart cannot erase.
##
## That is also why an absent [member origin] is [code]Malformed[/code] rather than an
## empty string: [code]""[/code] compares equal to an unconfigured backend's origin, and a
## record that deserialized that way would restore against it.
##
## The wire format is versioned and read strictly. Anything this build cannot fully
## understand — a schema it does not know, a missing field, a provider it cannot name — is
## refused rather than guessed at, because every guess ends with tokens minted by one
## backend being presented to another.
##
## Pure: no I/O, no [code]await[/code]. Writing the bytes belongs to [SecureStore].
class_name StoredSession extends RefCounted

## The schema version this build writes and the only one it can read.
##
## Bump it when the meaning of a field changes. A record stamped with anything else is
## reported as [code]VersionUnsupported[/code], never reinterpreted.
const SCHEMA_VERSION: int = 1

## The provider vocabulary of the wire format.
##
## Names rather than ordinals, so reordering [Provider] cannot silently repoint stored
## records at a different provider.
const _PROVIDER_BY_NAME: Dictionary[String, Provider] = {
	"google": Provider.GOOGLE,
	"apple": Provider.APPLE,
	"email_password": Provider.EMAIL_PASSWORD,
}

## The schema this record was written under. Always [constant SCHEMA_VERSION]; a record
## read from any other schema never becomes a [StoredSession].
var schema_version: int = SCHEMA_VERSION
## The backend origin that issued the tokens, as [code]scheme://host[:port][/code].
var origin: String = ""
var access_token: String = ""
var refresh_token: String = ""
var provider: Provider = Provider.GOOGLE
var raw: Dictionary[String, Variant] = {}
var extras: Dictionary[String, Variant] = {}

func _init(
		p_origin: String = "",
		p_access_token: String = "",
		p_refresh_token: String = "",
		p_provider: Provider = Provider.GOOGLE,
		p_raw: Dictionary[String, Variant] = {},
		p_extras: Dictionary[String, Variant] = {}) -> void:
	origin = p_origin
	access_token = p_access_token
	refresh_token = p_refresh_token
	provider = p_provider
	raw = p_raw.duplicate(true)
	extras = p_extras.duplicate(true)

## Builds the record for [param session] as issued by [param origin_of_issuer].
##
## The origin is taken from the caller and nowhere else: the whole protection depends on
## the stored origin being the one the session was actually obtained from.
static func from_session(session: AuthSession, origin_of_issuer: String) -> StoredSession:
	return StoredSession.new(
			origin_of_issuer,
			session.access_token,
			session.refresh_token,
			session.provider,
			session.raw,
			session.extras)

## Rebuilds the session this record was made from.
##
## The caller is responsible for having compared [member origin] first — this method makes
## no judgement about which backend the session belongs to.
func to_session() -> AuthSession:
	return AuthSession.new(access_token, refresh_token, provider, raw, extras)

## Serializes the record for secure storage.
##
## [b]Numbers in [member raw] and [member extras] are JSON numbers[/b], so an integer
## beyond 2^53 does not survive the round trip exactly. That is the format the values
## arrived in — the backend's payload is read out of a JSON response by the same parser —
## so nothing is lost here that was not already lost upstream. A backend that needs an
## exact large integer preserved has to send it as a string, as JSON callers generally do.
func to_bytes() -> PackedByteArray:
	var payload: Dictionary = {
		"schema_version": schema_version,
		"origin": origin,
		"access_token": access_token,
		"refresh_token": refresh_token,
		"provider": _name_of(provider),
		"raw": raw,
		"extras": extras,
	}
	return JSON.stringify(payload).to_utf8_buffer()

## Reads bytes written by [method to_bytes], refusing anything it cannot fully account for.
##
## Every rejection names the field at fault and never quotes a value, because the values
## here are tokens and the detail ends up wherever the caller logs it.
static func from_bytes(bytes: PackedByteArray) -> StoredSessionOutcome:
	if bytes.is_empty():
		return StoredSessionOutcome.Malformed("the record is empty")
	var text: String = bytes.get_string_from_utf8()
	if text.is_empty() or text.to_utf8_buffer() != bytes:
		# Decoding is lossy on anything that is not well-formed UTF-8, and it stops dead at
		# an embedded NUL — so a valid record followed by a NUL and arbitrary rubbish would
		# otherwise decode to just the valid part and parse. Re-encoding and comparing is
		# what makes "these exact bytes are the record" true rather than "these bytes begin
		# with something that looks like one".
		return StoredSessionOutcome.Malformed("the record is not valid UTF-8 text")
	var parser: JSON = JSON.new()
	if parser.parse(text) != OK:
		# The parser's own message can quote the input it choked on, and the input here
		# carries tokens. Report only that it did not parse.
		return StoredSessionOutcome.Malformed("the record is not valid JSON")
	if not (parser.data is Dictionary):
		return StoredSessionOutcome.Malformed("the record is not a JSON object")
	var payload: Dictionary = parser.data

	# The version is settled before any other field is read: a record from another schema
	# says nothing this build may interpret about the fields it appears to carry.
	var version: int = _whole_number_of(payload.get("schema_version"))
	if version <= 0:
		return StoredSessionOutcome.Malformed("the record carries no schema_version")
	if version != SCHEMA_VERSION:
		return StoredSessionOutcome.VersionUnsupported(version)

	# Every field is required, at the right type. [method to_bytes] writes all seven, so a
	# schema-1 record missing one was not written by this build, and defaulting it away
	# would hand back a session that has quietly lost the ability to refresh or lost the
	# backend payload a consumer reads. An empty value is a different matter — an empty
	# refresh token is what a backend that issues none produces — so emptiness is judged
	# per field below, not here.
	for text_key: String in ["origin", "access_token", "refresh_token", "provider"]:
		if not _carries(payload, text_key, TYPE_STRING):
			return StoredSessionOutcome.Malformed("the record's %s is missing or not a string" % text_key)
		# A control character cannot occur unescaped in valid JSON, and a permissive parser
		# that accepts one anyway would let a CR/LF through into a token that
		# [BackendClient] concatenates into the `Authorization` header — where it stops
		# being part of the token and starts being another header.
		if _has_a_control_character(_text_of(payload, text_key)):
			return StoredSessionOutcome.Malformed(
					"the record's %s contains a control character" % text_key)
	for dictionary_key: String in ["raw", "extras"]:
		if not _carries(payload, dictionary_key, TYPE_DICTIONARY):
			return StoredSessionOutcome.Malformed("the record's %s is missing or not an object" % dictionary_key)

	var stored_origin: String = _text_of(payload, "origin")
	if stored_origin.is_empty():
		# An empty origin would match an unconfigured backend. Refuse the record instead.
		return StoredSessionOutcome.Malformed("the record carries no origin")

	var access: String = _text_of(payload, "access_token")
	if access.is_empty():
		return StoredSessionOutcome.Malformed("the record carries no access_token")

	var provider_name: String = _text_of(payload, "provider")
	if not _PROVIDER_BY_NAME.has(provider_name):
		return StoredSessionOutcome.Malformed("the record names an unrecognised provider")
	var stored_provider: Provider = _PROVIDER_BY_NAME[provider_name]

	var record: StoredSession = StoredSession.new(
			stored_origin,
			access,
			_text_of(payload, "refresh_token"),
			stored_provider,
			_dictionary_of(payload, "raw"),
			_dictionary_of(payload, "extras"))
	return StoredSessionOutcome.Parsed(record)

## Names fields and sizes, never values — this type holds tokens.
func _to_string() -> String:
	return "StoredSession(schema_version=%d, origin=%s, provider=%s, raw_keys=%d, extras_keys=%d)" % [
		schema_version,
		origin,
		_name_of(provider),
		raw.size(),
		extras.size(),
	]

## The wire name of [param value], or the empty string for a provider the format cannot
## express — which makes the record unreadable rather than mislabelled.
static func _name_of(value: Provider) -> String:
	match value:
		Provider.GOOGLE:
			return "google"
		Provider.APPLE:
			return "apple"
		Provider.EMAIL_PASSWORD:
			return "email_password"
	return ""

## Returns whether [param text] holds a C0 control character or DEL.
##
## None of them are legal unescaped in a JSON string, and none of them belong in an origin,
## a token or a provider name.
static func _has_a_control_character(text: String) -> bool:
	for index: int in text.length():
		var code_point: int = text.unicode_at(index)
		if code_point < 0x20 or code_point == 0x7F:
			return true
	return false

## Returns whether [param key] is present and of [param expected_type].
static func _carries(payload: Dictionary, key: String, expected_type: int) -> bool:
	if not payload.has(key):
		return false
	return typeof(payload[key]) == expected_type

## The string at [param key], or the empty string when it is absent or not a string.
##
## Absent and empty are deliberately the same answer: every field read through this one is
## refused when it is empty, so there is nothing for the distinction to decide.
static func _text_of(payload: Dictionary, key: String) -> String:
	var value: Variant = payload.get(key, "")
	if value is String:
		var text: String = value
		return text
	return ""

## The value as a whole number, or 0 when it is absent, not a number, or fractional.
##
## A schema version is 1 or greater, so 0 is unambiguously "not a version this build can
## read" without a nullable return.
static func _whole_number_of(value: Variant) -> int:
	if value is int:
		var whole: int = value
		return whole
	if value is float:
		var number: float = value
		if number != floor(number):
			return 0
		return int(number)
	return 0

## The dictionary at [param key] with string keys, or an empty one when it is absent or of
## another type. Nested values are carried through as [Variant], so a nested object
## survives intact.
static func _dictionary_of(payload: Dictionary, key: String) -> Dictionary[String, Variant]:
	var result: Dictionary[String, Variant] = {}
	var value: Variant = payload.get(key)
	if not (value is Dictionary):
		return result
	var source: Dictionary = value
	for entry_key: Variant in source.keys():
		result[str(entry_key)] = source[entry_key]
	return result
