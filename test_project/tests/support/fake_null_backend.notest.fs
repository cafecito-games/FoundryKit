namespace games.cafecito.foundrykit.tests.support

## Stands in for the no-op backend used on unsupported platforms and partial installs.
class_name FakeNullBackend extends RefCounted
uses FakeBackend

func backend_name() -> String:
	return "null"

func is_available() -> bool:
	return false
