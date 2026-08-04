namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

## The backend used when no platform backend applies.
##
## Two situations resolve here and are deliberately indistinguishable: the platform has no
## native sign-in stack, and the consumer removed the auth binary from a partial install.
## Both mean "authentication is not available", and neither is an error condition — so
## every operation resolves promptly with a well-formed failure rather than hanging,
## erroring, or crashing.
##
## Signing out and clearing storage succeed: there is nothing to sign out of and nothing
## stored, which is the state the caller asked for.
class_name NullAuthBackend extends RefCounted
uses AuthBackend

const _STORAGE_DETAIL: String = "secure session storage is unavailable on this platform"

var _log: FoundryKitLog

func _init(log: FoundryKitLog) -> void:
	_log = log

func configure(_config: ProviderConfig) -> void:
	_log.debug("configure ignored: no authentication backend on this platform")

func is_available(_provider: Provider) -> bool:
	return false

func is_configured(_provider: Provider) -> bool:
	return false

async func sign_in(provider: Provider) -> CredentialResult:
	return _unavailable(provider)

async func sign_in_silent(provider: Provider) -> CredentialResult:
	return _unavailable(provider)

async func sign_out(_provider: Provider) -> CompletionResult:
	return CompletionResult.Success

async func store_session(_session: AuthSession, _origin: String) -> CompletionResult:
	return CompletionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

async func restore_session(_origin: String) -> SessionResult:
	return SessionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

func has_stored_session() -> bool:
	return false

async func clear_stored_session() -> CompletionResult:
	return CompletionResult.Success

func backend_name() -> String:
	return "null"

func _unavailable(provider: Provider) -> CredentialResult:
	_log.debug("sign-in unavailable for provider %d" % provider)
	return CredentialResult.Failure(AuthError.Unavailable(provider))
