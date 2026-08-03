namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal

## Covers building the Google authorization URL, before anything opens a browser or a
## socket. See `docs/superpowers/plans/2026-08-03-auth-epic-d-desktop-oauth.md`, Task 3.
class_name AuthDesktopUrlTests
extends RefCounted
uses Test

const _DESKTOP_CLIENT_ID: String = "desktop-client-id.apps.googleusercontent.com"
const _WEB_CLIENT_ID: String = "web-client-id.apps.googleusercontent.com"
const _IOS_CLIENT_ID: String = "ios-client-id.apps.googleusercontent.com"

## Matches the RFC 7636 Appendix B vector, so the challenge in the expected URL is proven
## against the standard rather than against `PkcePair` itself.
const _FIXED_VERIFIER: String = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
const _FIXED_CHALLENGE: String = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
const _FIXED_STATE: String = "fixed-state-value"
const _FIXED_NONCE: String = "fixed-nonce-value"
const _FIXED_PORT: int = 54321

func _config() -> ProviderConfig:
	return ProviderConfig.Google(_WEB_CLIENT_ID, _IOS_CLIENT_ID, _DESKTOP_CLIENT_ID)

func _pkce() -> PkcePair:
	var pair: PkcePair = PkcePair.new()
	pair.code_verifier = _FIXED_VERIFIER
	pair.code_challenge = _FIXED_CHALLENGE
	pair.state = _FIXED_STATE
	return pair

func test_authorization_url_matches_the_expected_string_exactly() -> void:
	var redirect_uri: String = "http://127.0.0.1:%d/" % _FIXED_PORT
	var url: String = DesktopAuthBackend.build_authorization_url(
			_config(), _pkce(), redirect_uri, _FIXED_NONCE)
	var expected: String = (
			"https://accounts.google.com/o/oauth2/v2/auth"
			+ "?client_id=desktop-client-id.apps.googleusercontent.com"
			+ "&redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2F"
			+ "&response_type=code"
			+ "&scope=openid%20email%20profile"
			+ "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"
			+ "&code_challenge_method=S256"
			+ "&state=fixed-state-value"
			+ "&nonce=fixed-nonce-value")
	Expect.that(url).to_equal(expected)

func test_uses_the_desktop_client_id_never_web_or_ios() -> void:
	var redirect_uri: String = "http://127.0.0.1:%d/" % _FIXED_PORT
	var url: String = DesktopAuthBackend.build_authorization_url(
			_config(), _pkce(), redirect_uri, _FIXED_NONCE)
	Expect.that(url.contains(_DESKTOP_CLIENT_ID.uri_encode())).to_be_true()
	Expect.that(url.contains(_WEB_CLIENT_ID)).to_be_false()
	Expect.that(url.contains(_IOS_CLIENT_ID)).to_be_false()

func test_redirect_uri_carries_the_actual_bound_port_and_is_percent_encoded() -> void:
	var redirect_uri: String = "http://127.0.0.1:%d/" % _FIXED_PORT
	var url: String = DesktopAuthBackend.build_authorization_url(
			_config(), _pkce(), redirect_uri, _FIXED_NONCE)
	Expect.that(url.contains(redirect_uri)).to_be_false()
	Expect.that(url.contains(redirect_uri.uri_encode())).to_be_true()
	Expect.that(url.contains(str(_FIXED_PORT))).to_be_true()
