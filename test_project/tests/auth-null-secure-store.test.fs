namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal

class_name NullSecureStoreTests
extends RefCounted
uses Test

var _store: NullSecureStore

func before_each() -> void:
	_store = NullSecureStore.new()

func _storage_error_name(result: CompletionResult) -> String:
	match result:
		CompletionResult.Success:
			return "unexpected_success"
		CompletionResult.Failure(error):
			match error:
				AuthError.Storage(_detail):
					return "storage"
				AuthError.Cancelled:
					return "other"
				AuthError.NoCredential:
					return "other"
				AuthError.Unavailable(_provider):
					return "other"
				AuthError.Configuration(_detail):
					return "other"
				AuthError.RequestFailed(_status, _body):
					return "other"
				AuthError.InvalidResponse(_detail):
					return "other"
				AuthError.MissingField(_field):
					return "other"
				AuthError.SessionExpired(_expired_at):
					return "other"
				AuthError.TimedOut(_elapsed_seconds):
					return "other"
	return "unreachable"

func _load_outcome_name(outcome: SecureLoadOutcome) -> String:
	match outcome:
		SecureLoadOutcome.Loaded(_bytes):
			return "loaded"
		SecureLoadOutcome.Absent:
			return "absent"
		SecureLoadOutcome.Failed(_detail):
			return "failed"
	return "unreachable"

func test_is_never_available() -> void:
	Expect.that(_store.is_available()).to_be_false()
	Expect.that(_store.has_value()).to_be_false()

func test_store_fails_with_storage_error() -> void:
	var result: CompletionResult = await _store.store(PackedByteArray([1, 2, 3]))
	Expect.that(_storage_error_name(result)).to_equal("storage")

func test_erase_fails_with_storage_error() -> void:
	var result: CompletionResult = await _store.erase()
	Expect.that(_storage_error_name(result)).to_equal("storage")

## A first launch has nothing stored yet. That must read as [code]Absent[/code], never as
## [code]Failed[/code] — the two mean different things to a caller deciding whether to
## retry, and collapsing them here would defeat the whole point of the case.
func test_load_reports_absent_not_failed() -> void:
	var outcome: SecureLoadOutcome = await _store.load()
	Expect.that(_load_outcome_name(outcome)).to_equal("absent")
