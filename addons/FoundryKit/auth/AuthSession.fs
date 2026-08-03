namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.auth.internal

## A backend-issued authentication session.
##
## The backend owns session semantics; FoundryKit holds the tokens and whatever else the
## backend returned. [member raw] is the backend's own payload, preserved verbatim so a
## consumer can read fields FoundryKit does not model. [member extras] is everything
## alongside the reserved keys when a session is restored from storage.
class_name AuthSession extends RefCounted

const _RESERVED_KEYS: Array[String] = ["access_token", "refresh_token", "provider", "raw"]

var access_token: String = ""
var refresh_token: String = ""
var provider: Provider = Provider.GOOGLE
var raw: Dictionary[String, Variant] = {}
var extras: Dictionary[String, Variant] = {}

func _init(
		p_access_token: String = "",
		p_refresh_token: String = "",
		p_provider: Provider = Provider.GOOGLE,
		p_raw: Dictionary[String, Variant] = {},
		p_extras: Dictionary[String, Variant] = {}) -> void:
	access_token = p_access_token
	refresh_token = p_refresh_token
	provider = p_provider
	raw = p_raw.duplicate(true)
	extras = p_extras.duplicate(true)

## Returns the access token's `exp` claim, or 0 when it carries none.
##
## An opaque (non-JWT) access token has no readable expiry. Treating it as
## never-expiring is deliberate: the backend is the authority, and a 401 will surface
## expiry through [code]AuthError.SessionExpired[/code] instead.
func expires_at() -> int:
	return Jwt.expiry_from(access_token)

## Returns whether the session is expired at [param now_unix_seconds].
func is_expired_at(now_unix_seconds: int) -> bool:
	var expiry: int = expires_at()
	if expiry == 0:
		return false
	return now_unix_seconds > expiry

## Returns an independent copy; mutating it cannot affect this session.
func duplicate_session() -> AuthSession:
	return AuthSession.new(access_token, refresh_token, provider, raw, extras)

## Flattens the session for secure storage.
func to_dictionary() -> Dictionary[String, Variant]:
	var payload: Dictionary[String, Variant] = extras.duplicate(true)
	payload["access_token"] = access_token
	payload["refresh_token"] = refresh_token
	payload["provider"] = provider
	payload["raw"] = raw.duplicate(true)
	return payload

## Rebuilds a session from storage. Reserved keys become fields; everything else is extras.
static func from_dictionary(
		payload: Dictionary[String, Variant],
		p_provider: Provider) -> AuthSession:
	var restored_extras: Dictionary[String, Variant] = payload.duplicate(true)
	for key: String in _RESERVED_KEYS:
		restored_extras.erase(key)
	var restored_raw: Dictionary[String, Variant] = {}
	var stored_raw: Variant = payload.get("raw")
	if stored_raw is Dictionary:
		var stored: Dictionary = stored_raw
		for key: Variant in stored.keys():
			restored_raw[str(key)] = stored[key]
	return AuthSession.new(
			str(payload.get("access_token", "")),
			str(payload.get("refresh_token", "")),
			p_provider,
			restored_raw,
			restored_extras)
