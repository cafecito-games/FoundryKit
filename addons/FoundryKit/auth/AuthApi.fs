namespace games.cafecito.foundrykit.auth

## The public authentication contract.
##
## Game code depends on this trait so a test double can stand in for the real subsystem.
##
## Ten of the legacy API's twelve signals became return values. The two that remain are
## genuinely unsolicited — they are not answers to any call the game made:
## [signal session_expired] fires when a session lapses in the background, and
## [signal tokens_refreshed] when a refresh rotates tokens the game may be holding.
trait_name AuthApi

## Emitted when the active session expires outside any call the game made.
signal session_expired(error: AuthError)

## Emitted when a background refresh rotates the active session's tokens.
signal tokens_refreshed(session: AuthSession)

## Applies configuration for one provider.
abstract func configure(config: ProviderConfig) -> void

## Points the implementation at the backend that issues and renews sessions.
##
## On the contract rather than only on the concrete subsystem because a game must call it:
## without a [BackendConfig] carrying a base URL there is nowhere to exchange a credential,
## and game code holds this trait precisely so a double can stand in for the real subsystem.
## A configuration entry point reachable only through the concrete type would force every
## consumer's bootstrap back onto that type and defeat the substitution.
abstract func configure_backend(config: BackendConfig) -> void

## Returns whether a provider can be used on this platform.
abstract func is_available(provider: Provider) -> bool

## Returns whether a provider has the configuration it requires.
abstract func is_configured(provider: Provider) -> bool

## Runs an interactive sign-in and resolves with a backend session.
abstract async func sign_in(provider: Provider) -> SessionResult

## Attempts sign-in without UI for a returning player.
abstract async func sign_in_silent(provider: Provider) -> SessionResult

## Signs out of the native provider and clears the active session.
abstract async func sign_out(provider: Provider) -> CompletionResult

## Returns whether a session is currently active.
abstract func has_session() -> bool

## Returns the current access token, or an empty string when there is no session.
##
## Prefer [method valid_access_token] when the token is about to be used: this accessor
## does not refresh and may return an expired token.
abstract func access_token() -> String

## Returns an access token that is valid now, refreshing first if required.
abstract async func valid_access_token() -> TokenResult

## Forces a refresh of the active session.
abstract async func refresh_session() -> SessionResult

## Restores a session from platform secure storage.
abstract async func restore_session() -> SessionResult

## Clears the active session and removes it from secure storage.
abstract async func clear_session() -> CompletionResult

## Sends an authorized request to the configured backend.
abstract async func request(method: HttpMethod, path: String, body: Variant) -> ResponseResult
