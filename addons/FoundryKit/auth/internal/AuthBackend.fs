namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth

## The contract every platform authentication backend implements.
##
## A trait, not an abstract base class. Each backend lives in its own file, and FoundryKit
## composes contracts with `uses` rather than inheriting them — no backend `extends`
## anything but `RefCounted`.
##
## Every asynchronous operation returns a result union. The legacy contract emitted paired
## success/failure signals, which forced every caller to connect two handlers and made it
## impossible to tell which request a signal belonged to.
##
## A backend produces a [Credential]; exchanging one for an [AuthSession] is the session
## layer's job, not a backend's.
trait_name AuthBackend

## Applies configuration for one provider. Called before any sign-in attempt.
abstract func configure(config: ProviderConfig) -> void

## Returns whether this backend can serve a provider on this platform at all.
abstract func is_available(provider: Provider) -> bool

## Returns whether a provider has received the configuration it requires.
abstract func is_configured(provider: Provider) -> bool

## Starts an interactive sign-in and resolves with a credential.
abstract async func sign_in(provider: Provider) -> CredentialResult

## Attempts sign-in without UI, for a returning player.
abstract async func sign_in_silent(provider: Provider) -> CredentialResult

## Signs out of the native provider. Does not touch the backend session.
abstract async func sign_out(provider: Provider) -> CompletionResult

## Writes a session to platform secure storage.
abstract async func store_session(session: AuthSession) -> CompletionResult

## Reads the stored session back.
abstract async func restore_session() -> SessionResult

## Returns whether secure storage currently holds a session.
abstract func has_stored_session() -> bool

## Removes the stored session.
abstract async func clear_stored_session() -> CompletionResult

## Identifies the backend in logs and diagnostics.
abstract func backend_name() -> String
