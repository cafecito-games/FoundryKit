namespace games.cafecito.foundrykit.auth

## Where the authentication backend lives and which paths it exposes.
##
## `AuthSubsystem` reports [code]AuthError.Configuration[/code] whenever no backend is
## configured; this type is what lets a consumer resolve that honestly, by supplying a
## real [member base_url], rather than by working around the check.
class_name BackendConfig extends RefCounted

## The backend's origin, e.g. [code]https://api.example.com[/code]. Empty means
## unconfigured — see [method is_configured].
var base_url: String

## Path the credential exchange endpoint is served at.
var exchange_path: String

## Path the token refresh endpoint is served at.
var refresh_path: String

## Path the sign-out endpoint is served at.
var sign_out_path: String

## Builds a backend configuration.
##
## [param base_url] defaults to empty, which [method is_configured] reports as
## unconfigured. The three paths default to a sensible versioned layout so a consumer
## only needs to override the ones their backend actually differs on.
func _init(
		base_url: String = "",
		exchange_path: String = "/v1/auth/exchange",
		refresh_path: String = "/v1/auth/refresh",
		sign_out_path: String = "/v1/auth/sign-out") -> void:
	self.base_url = base_url
	self.exchange_path = exchange_path
	self.refresh_path = refresh_path
	self.sign_out_path = sign_out_path

## Returns whether [member base_url] has been set to something usable.
##
## An empty base URL means there is genuinely nowhere to send a request — the caller
## should keep reporting [code]AuthError.Configuration[/code] rather than attempt a join
## against an empty string.
func is_configured() -> bool:
	return not base_url.is_empty()

## Joins [member base_url] with [param path], producing exactly one separating slash
## regardless of which side already carries one, and never dropping the slash when
## neither side does.
func url_for(path: String) -> String:
	var base_has_slash: bool = base_url.ends_with("/")
	var path_has_slash: bool = path.begins_with("/")
	if base_has_slash and path_has_slash:
		return base_url + path.substr(1)
	if base_has_slash or path_has_slash:
		return base_url + path
	return base_url + "/" + path
