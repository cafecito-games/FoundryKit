namespace games.cafecito.foundrykit.tests.support

## Stands in for a working platform backend.
class_name FakePlatformBackend extends RefCounted
uses FakeBackend

var _name: String = ""

func _init(name: String) -> void:
	_name = name

func backend_name() -> String:
	return _name

func is_available() -> bool:
	return true
