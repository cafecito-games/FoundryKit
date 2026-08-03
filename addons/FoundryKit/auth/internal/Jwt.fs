namespace games.cafecito.foundrykit.auth.internal

## Reads claims out of a JWT without verifying its signature.
##
## Verification is the backend's job. FoundryKit decodes only to learn the subject and
## expiry it needs for session bookkeeping, and treats every malformed input as absent
## rather than as an error — a token this code cannot read is one the backend will reject
## anyway, and failing here would mask the clearer backend failure.
class_name Jwt extends RefCounted

## Returns the `sub` claim, or an empty string when it is absent or undecodable.
static func subject_from(token: String) -> String:
	var claims: Dictionary = _claims_of(token)
	if claims.is_empty():
		return ""
	return str(claims.get("sub", ""))

## Returns the `exp` claim as a Unix timestamp, or 0 when absent or undecodable.
##
## Accepts both int and float encodings; JSON parsers commonly widen large integers.
##
## A returned 0 is ambiguous by design: it is both the value of a literal
## [code]exp: 0[/code] — expired at the Unix epoch — and the fallback for a token that
## carries no readable expiry. Ask [method has_expiry] first when the difference matters.
static func expiry_from(token: String) -> int:
	var expiry: Variant = _expiry_claim_of(token)
	if expiry is int:
		var as_int: int = expiry
		return as_int
	if expiry is float:
		var as_float: float = expiry
		return int(as_float)
	return 0

## Returns whether the token carries a readable `exp` claim at all.
##
## [method expiry_from] cannot express this: it returns 0 both for an absent claim and
## for a literal `exp: 0`, which are different facts. A caller deciding whether a token
## can expire must ask this first.
static func has_expiry(token: String) -> bool:
	return _expiry_claim_of(token) != null

## Returns the raw `exp` claim, or null when the token carries no numeric one.
static func _expiry_claim_of(token: String) -> Variant:
	var claims: Dictionary = _claims_of(token)
	if claims.is_empty():
		return null
	var expiry: Variant = claims.get("exp")
	if expiry is int or expiry is float:
		return expiry
	return null

static func _claims_of(token: String) -> Dictionary:
	var segments: PackedStringArray = token.split(".")
	if segments.size() != 3:
		return {}
	var payload_json: String = _decode_segment(segments[1])
	if payload_json.is_empty():
		return {}
	var parser: JSON = JSON.new()
	if parser.parse(payload_json) != OK:
		return {}
	if not (parser.data is Dictionary):
		return {}
	var claims: Dictionary = parser.data
	return claims

static func _decode_segment(segment: String) -> String:
	var padded: String = segment.replace("-", "+").replace("_", "/")
	match padded.length() % 4:
		0:
			pass
		2:
			padded += "=="
		3:
			padded += "="
		_:
			# A remainder of 1 is not producible by valid base64.
			return ""
	return Marshalls.base64_to_utf8(padded)
