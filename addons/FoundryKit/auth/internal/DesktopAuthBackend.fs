namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth

## The desktop OAuth 2.0 installed-app flow (RFC 8252) for Google sign-in on Linux and
## Windows, where there is no native provider SDK.
##
## This file currently carries only what epic D task 3 needs: building the authorization
## URL correctly, before anything opens a browser. The browser launch, the loopback wait,
## the token exchange and [AuthBackend] conformance are #120 — adding them here would mean
## stubbing the whole trait for behaviour this task does not exercise.
class_name DesktopAuthBackend extends RefCounted

## Google's OAuth 2.0 authorization endpoint for the installed-app flow. Reused unmodified
## from the legacy desktop backend.
const AUTHORIZATION_ENDPOINT: String = "https://accounts.google.com/o/oauth2/v2/auth"

## Google's token endpoint. Not called from this file yet — the exchange is #120 — recorded
## here so both endpoints travel together for whoever wires the rest of the flow in.
const TOKEN_ENDPOINT: String = "https://oauth2.googleapis.com/token"

## The OAuth scopes requested: enough to resolve identity, nothing more.
const _SCOPE: String = "openid email profile"

## Builds the URL the system browser is sent to for a Google sign-in.
##
## Every value is percent-encoded, [param redirect_uri] included — it carries a `:` and a
## `/`, and sending it unencoded is the classic bug in this flow. [param redirect_uri] must
## be the value [method LoopbackServer.redirect_uri] returned after
## [method LoopbackServer.start], not re-derived here, so the browser is told to answer on
## the port the listener actually bound. [param nonce] is independent of [param pkce] and
## is the caller's to generate — [PkcePair] carries only what RFC 7636 defines.
static func build_authorization_url(
		provider_config: ProviderConfig,
		pkce: PkcePair,
		redirect_uri: String,
		nonce: String) -> String:
	var keys: PackedStringArray = [
		"client_id",
		"redirect_uri",
		"response_type",
		"scope",
		"code_challenge",
		"code_challenge_method",
		"state",
		"nonce",
	]
	var values: PackedStringArray = [
		_desktop_client_id_of(provider_config),
		redirect_uri,
		"code",
		_SCOPE,
		pkce.code_challenge,
		"S256",
		pkce.state,
		nonce,
	]
	var encoded_parts: PackedStringArray = []
	for index: int in range(keys.size()):
		encoded_parts.append("%s=%s" % [keys[index].uri_encode(), values[index].uri_encode()])
	return "%s?%s" % [AUTHORIZATION_ENDPOINT, "&".join(encoded_parts)]

## Returns the desktop OAuth client ID out of a Google provider configuration.
##
## This flow only ever configures [enum Provider.GOOGLE]; the other cases return an empty
## string rather than the [code]_[/code] wildcard the union redesign forbids, so a caller
## that reaches this with the wrong provider gets an obviously-wrong URL instead of a
## crash, and adding a case here still breaks every switch that needs updating.
static func _desktop_client_id_of(provider_config: ProviderConfig) -> String:
	match provider_config:
		ProviderConfig.Google(_web_client_id, _ios_client_id, desktop_client_id):
			return desktop_client_id
		ProviderConfig.Apple(_service_id, _redirect_uri):
			return ""
		ProviderConfig.EmailPassword:
			return ""
	return ""
