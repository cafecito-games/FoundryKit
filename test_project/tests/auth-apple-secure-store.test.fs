namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

## Drives [AppleSecureStore] against [FakeKeychainNative] — never a real Keychain.
##
## No assertion here depends on `ClassDB` probing the real `iOSKeychain` class: the
## headless suite runs on macOS locally and ubuntu in CI, and that class is registered on
## neither, so every case either injects the fake native or, for the "binary absent" cases,
## constructs the store with no override and asserts through the store's own reported
## behaviour rather than through a platform-specific expectation (#75, #121).
class_name AppleSecureStoreTests
extends RefCounted
uses Test

var _native: FakeKeychainNative
var _store: AppleSecureStore

func before_each() -> void:
	_native = FakeKeychainNative.new()
	_store = AppleSecureStore.new(FoundryKitLog.new("test"), _native)

func after_each() -> void:
	_native.free()

func _error_name(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(_provider):
			return "unavailable"
		AuthError.Configuration(_detail):
			return "configuration"
		AuthError.Storage(_detail):
			return "storage"
		AuthError.RequestFailed(_status, _body):
			return "request_failed"
		AuthError.InvalidResponse(_detail):
			return "invalid_response"
		AuthError.MissingField(_field):
			return "missing_field"
		AuthError.SessionExpired(_expired_at):
			return "session_expired"
		AuthError.TimedOut(_elapsed_seconds):
			return "timed_out"
	return "unreachable"

func _describe_completion(result: CompletionResult) -> String:
	match result:
		CompletionResult.Success:
			return "ok"
		CompletionResult.Failure(error):
			return "fail:%s" % _error_name(error)
	return "unreachable"

func _describe_load(outcome: SecureLoadOutcome) -> String:
	match outcome:
		SecureLoadOutcome.Loaded(_bytes):
			return "loaded"
		SecureLoadOutcome.Absent:
			return "absent"
		SecureLoadOutcome.Failed(_detail):
			return "failed"
	return "unreachable"

func test_is_available_when_the_native_is_present() -> void:
	Expect.that(_store.is_available()).to_be_true()

func test_store_writes_the_bytes_under_the_fixed_account() -> void:
	var bytes: PackedByteArray = PackedByteArray([1, 2, 3])
	var result: CompletionResult = await _store.store(bytes)
	Expect.that(_describe_completion(result)).to_equal("ok")
	Expect.that(_native.store_call_count).to_equal(1)
	Expect.that(_native.last_stored_bytes).to_equal(bytes)
	Expect.that(_native.last_store_account.is_empty()).to_be_false()

func test_store_failure_maps_to_storage_error() -> void:
	_native.next_store_status = FakeKeychainNative.STATUS_FAILED
	_native.next_detail = "the disk is full"
	var result: CompletionResult = await _store.store(PackedByteArray([1]))
	Expect.that(_describe_completion(result)).to_equal("fail:storage")

func test_store_missing_entitlement_maps_to_storage_error() -> void:
	_native.next_store_status = FakeKeychainNative.STATUS_MISSING_ENTITLEMENT
	_native.next_detail = "errSecMissingEntitlement"
	var result: CompletionResult = await _store.store(PackedByteArray([1]))
	Expect.that(_describe_completion(result)).to_equal("fail:storage")

func test_store_interaction_not_allowed_maps_to_storage_error() -> void:
	_native.next_store_status = FakeKeychainNative.STATUS_INTERACTION_NOT_ALLOWED
	var result: CompletionResult = await _store.store(PackedByteArray([1]))
	Expect.that(_describe_completion(result)).to_equal("fail:storage")

func test_load_returns_the_stored_bytes() -> void:
	var bytes: PackedByteArray = PackedByteArray([4, 5, 6])
	_native.next_load_status = FakeKeychainNative.STATUS_SUCCESS
	_native.bytes_to_load = bytes
	var outcome: SecureLoadOutcome = await _store.load()
	Expect.that(_describe_load(outcome)).to_equal("loaded")
	Expect.that(_loaded_bytes_of(outcome)).to_equal(bytes)

func _loaded_bytes_of(outcome: SecureLoadOutcome) -> PackedByteArray:
	match outcome:
		SecureLoadOutcome.Loaded(loaded_bytes):
			return loaded_bytes
		SecureLoadOutcome.Absent:
			return PackedByteArray()
		SecureLoadOutcome.Failed(_detail):
			return PackedByteArray()
	return PackedByteArray()

## A first launch is the ordinary state, not a failure: this is the whole reason
## [enum SecureLoadOutcome] keeps `Absent` distinct from `Failed`.
func test_load_reports_absent_not_failed() -> void:
	_native.next_load_status = FakeKeychainNative.STATUS_ABSENT
	var outcome: SecureLoadOutcome = await _store.load()
	Expect.that(_describe_load(outcome)).to_equal("absent")

func test_load_failure_maps_to_failed_with_the_native_detail() -> void:
	_native.next_load_status = FakeKeychainNative.STATUS_INTERACTION_NOT_ALLOWED
	_native.next_detail = "the device has not been unlocked"
	var outcome: SecureLoadOutcome = await _store.load()
	Expect.that(_describe_load(outcome)).to_equal("failed")

## `takeLoadedValue()` clears on collection on the real native; a successful load must
## collect it exactly once so a second, unrelated read never replays a stale value.
func test_load_collects_the_native_value_exactly_once() -> void:
	_native.next_load_status = FakeKeychainNative.STATUS_SUCCESS
	_native.bytes_to_load = PackedByteArray([7, 8])
	await _store.load()
	Expect.that(_native.take_loaded_value_call_count).to_equal(1)

func test_erase_succeeds() -> void:
	var result: CompletionResult = await _store.erase()
	Expect.that(_describe_completion(result)).to_equal("ok")
	Expect.that(_native.erase_call_count).to_equal(1)

## Erasing a record that was never there still satisfies the caller's intent — the same
## contract [NullSecureStore] and the trait doc both describe.
func test_erase_of_an_absent_record_still_succeeds() -> void:
	_native.next_erase_status = FakeKeychainNative.STATUS_ABSENT
	var result: CompletionResult = await _store.erase()
	Expect.that(_describe_completion(result)).to_equal("ok")

func test_erase_failure_maps_to_storage_error() -> void:
	_native.next_erase_status = FakeKeychainNative.STATUS_FAILED
	_native.next_detail = "the item could not be deleted"
	var result: CompletionResult = await _store.erase()
	Expect.that(_describe_completion(result)).to_equal("fail:storage")

## The headless suite runs with no native binaries at all — this is the production-shaped
## path for the platforms this epic does not cover, and for a consumer that removed
## `bin/auth/` from a partial install. Invariant 6: never a crash, never a null
## dereference, always the same unavailable shape [NullSecureStore] reports.
func test_without_the_native_class_every_operation_resolves_unavailable_without_hanging() -> void:
	var bare: AppleSecureStore = AppleSecureStore.new(FoundryKitLog.new("test"))
	Expect.that(bare.is_available()).to_be_false()
	Expect.that(_describe_load(await bare.load())).to_equal("absent")
	Expect.that(_describe_completion(await bare.store(PackedByteArray([1])))) \
			.to_equal("fail:storage")
	Expect.that(_describe_completion(await bare.erase())).to_equal("fail:storage")
