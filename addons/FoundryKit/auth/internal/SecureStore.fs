namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth

## The platform secure-storage seam.
##
## A trait, not an abstract base class: FoundryKit composes contracts with `uses` rather
## than inheriting them, and no type here `extends` anything but an engine class. Production
## composes a platform implementation such as an Apple Keychain wrapper; tests compose a
## fake that records what it was handed and can be primed with any [SecureLoadOutcome].
##
## Mutating and loading operations are declared `async` for contract uniformity with the
## rest of the auth surface, but no implementation may ever suspend: awaiting an
## already-completed coroutine hangs this engine's test runner rather than failing it,
## and every native surface this seam reaches (the Keychain) is synchronous. Presence is
## synchronous so [method AuthBackend.has_stored_session] can report durable state.
##
## Deliberately free of session vocabulary. This trait moves opaque bytes and reports how
## the attempt ended; interpreting those bytes as a [code]StoredSession[/code] belongs to
## the caller.
trait_name SecureStore

## Reports whether this platform has secure storage to offer at all.
##
## `false` covers both "this platform has no such native stack" and "the consumer removed
## the storage binary from a partial install" — both mean "not available", and neither is
## an error.
abstract func is_available() -> bool

## Reports whether secure storage currently holds an opaque value.
##
## This query is synchronous because [method AuthBackend.has_stored_session] is
## synchronous and must reflect durable storage even in a newly constructed backend.
## Implementations that probe by loading must immediately discard the loaded bytes.
abstract func has_value() -> bool

## Writes [param bytes] to secure storage, replacing whatever was previously stored.
abstract async func store(bytes: PackedByteArray) -> CompletionResult

## Reads whatever is currently in secure storage.
abstract async func load() -> SecureLoadOutcome

## Removes whatever is currently in secure storage.
##
## Erasing when nothing is stored still succeeds: that is the state the caller asked for.
abstract async func erase() -> CompletionResult
