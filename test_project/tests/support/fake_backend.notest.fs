namespace games.cafecito.foundrykit.tests.support

## Minimal backend contract used to exercise [BackendFactory] without a real subsystem.
trait_name FakeBackend

abstract func backend_name() -> String

abstract func is_available() -> bool
