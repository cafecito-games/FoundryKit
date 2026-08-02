namespace games.cafecito.foundrykit.auth

## The result of an authorized backend HTTP request.
##
## [member transport_ok] distinguishes "the request never reached the server" from "the
## server answered with an error status" — a distinction callers need, because only the
## latter carries a meaningful [member status_code].
class_name AuthResponse extends RefCounted

## Whether the request completed at the transport level, regardless of status.
var transport_ok: bool = false
var status_code: int = 0
var body: PackedByteArray = PackedByteArray()
## Set by the session layer when the backend reported the session is no longer valid.
var session_expired: bool = false

## Returns whether the request both completed and returned a 2xx status.
func is_ok() -> bool:
	if not transport_ok:
		return false
	return status_code >= 200 and status_code < 300

## Returns the body parsed as a JSON object, or null when it is empty or not an object.
func json() -> Variant:
	var text: String = body.get_string_from_utf8()
	if text.is_empty():
		return null
	var parser: JSON = JSON.new()
	if parser.parse(text) != OK:
		return null
	if not (parser.data is Dictionary):
		return null
	return parser.data
