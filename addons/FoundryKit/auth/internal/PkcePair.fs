namespace games.cafecito.foundrykit.auth.internal

## Generates PKCE (RFC 7636) material for the desktop OAuth authorization code flow.
##
## [member code_verifier] and [member state] are independently generated, high-entropy,
## base64url strings. [member code_challenge] is the S256 transform of the verifier that
## lets the authorization server check the eventual token request without ever seeing the
## verifier itself.
class_name PkcePair extends RefCounted

## The number of random bytes backing [member code_verifier] and [member state].
##
## 32 bytes base64url-encodes to a 43-character string — the minimum length RFC 7636
## §4.1 allows, and comfortably within its 128-character maximum.
const _RANDOM_BYTE_COUNT: int = 32

## The high-entropy secret that only this client and the authorization server's token
## endpoint ever see directly. The authorization endpoint sees only [member code_challenge].
var code_verifier: String

## `BASE64URL(SHA256(ASCII(code_verifier)))`, sent to the authorization endpoint.
var code_challenge: String

## An independent random value round-tripped through the authorization request to defend
## against cross-site request forgery on the loopback redirect.
var state: String

## Builds a fresh [PkcePair] with a new verifier, its matching S256 challenge, and a new state.
static func generate() -> PkcePair:
	var pair: PkcePair = PkcePair.new()
	pair.code_verifier = _random_base64_url()
	pair.code_challenge = challenge_for(pair.code_verifier)
	pair.state = _random_base64_url()
	return pair

## Returns the RFC 7636 S256 code challenge for the given verifier:
## `BASE64URL(SHA256(ASCII(verifier)))`.
##
## Hashes the verifier's own ASCII bytes directly — never a re-decoded buffer — as the
## RFC's `ASCII(code_verifier)` requires.
static func challenge_for(verifier: String) -> String:
	var digest: PackedByteArray = verifier.sha256_buffer()
	return _base64_url_encode(digest)

static func _random_base64_url() -> String:
	var crypto: Crypto = Crypto.new()
	var random_bytes: PackedByteArray = crypto.generate_random_bytes(_RANDOM_BYTE_COUNT)
	return _base64_url_encode(random_bytes)

## Base64url per RFC 4648 §5: standard base64 with `+`/`/` substituted and `=` padding
## stripped, so the result is safe to place directly in a URL query.
static func _base64_url_encode(raw: PackedByteArray) -> String:
	var standard: String = Marshalls.raw_to_base64(raw)
	return standard.replace("+", "-").replace("/", "_").replace("=", "")
