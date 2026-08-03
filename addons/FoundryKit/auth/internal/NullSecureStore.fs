namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth

## The [SecureStore] used when no platform secure storage applies.
##
## Two situations resolve here and are deliberately indistinguishable: the platform has no
## native secure-storage stack, and the consumer removed the storage binary from a partial
## install. Both mean "secure storage is not available", and neither is an error condition
## — so every operation resolves promptly with a well-formed outcome rather than hanging,
## erroring, or crashing.
class_name NullSecureStore extends RefCounted
uses SecureStore

const _STORAGE_DETAIL: String = "secure session storage is unavailable on this platform"

func is_available() -> bool:
	return false

async func store(_bytes: PackedByteArray) -> CompletionResult:
	return CompletionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

async func load() -> SecureLoadOutcome:
	return SecureLoadOutcome.Absent

async func erase() -> CompletionResult:
	return CompletionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))
