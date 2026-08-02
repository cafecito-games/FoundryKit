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
static func expiry_from(token: String) -> int:
	var claims: Dictionary = _claims_of(token)
	if claims.is_empty():
		return 0
	var expiry: Variant = claims.get("exp")
	if expiry is int:
		var as_int: int = expiry
		return as_int
	if expiry is float:
		var as_float: float = expiry
		return int(as_float)
	return 0

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
