namespace games.cafecito.foundrykit.tests.support

## Stands in for a native extension object in tests.
##
## Mirrors the signal shape FoundryKit's natives use: a success signal with
## subsystem-specific arguments, and a failure signal carrying (code, message).
class_name FakeNative extends Object

signal operation_success(first: String, second: String)
signal operation_failed(code: int, message: String)

func emit_success(first: String, second: String) -> void:
	operation_success.emit(first, second)

func emit_failure(code: int, message: String) -> void:
	operation_failed.emit(code, message)

func success_connection_count() -> int:
	return operation_success.get_connections().size()

func failure_connection_count() -> int:
	return operation_failed.get_connections().size()
