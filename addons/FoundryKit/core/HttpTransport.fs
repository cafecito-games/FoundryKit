namespace games.cafecito.foundrykit.core

## The contract an HTTP transport satisfies.
##
## A trait, not an abstract base class: FoundryKit composes contracts with `uses` rather
## than inheriting them, and no type here `extends` anything but an engine class.
##
## This exists so every layer above it can be tested against a scripted double with no
## network and no [SceneTree]. Production composes [HttpClient]; tests compose a fake that
## returns queued [HttpOutcome] values and records what it was handed.
##
## Deliberately free of authorization vocabulary, like [HttpOutcome]. A transport moves
## bytes and reports how the attempt ended; interpreting a 401 belongs to the subsystem
## that knows what one means.
trait_name HttpTransport

## Performs one HTTP request and resolves with how it ended.
##
## [param method] is an uppercase HTTP verb such as [code]GET[/code] or [code]POST[/code];
## an unrecognised verb resolves to [code]TransportFailed[/code] rather than erroring.
## [param headers] holds entries in [code]"Name: value"[/code] form.
## [param timeout_seconds] bounds the whole attempt, after which the result is
## [code]TimedOut[/code].
##
## An implementation must settle exactly once and must never resolve to an error the
## caller cannot act on: a request that cannot be started at all is still an outcome.
abstract async func send(
		method: String,
		url: String,
		headers: PackedStringArray,
		body: PackedByteArray,
		timeout_seconds: float) -> HttpOutcome
