namespace games.cafecito.foundrykit.tests.support

## Stands in for the `iOSKeychain` native class.
##
## Mirrors the real, synchronous `@Callable` surface exactly — `store`/`load`/`erase`
## return a status code, `takeLoadedValue()` collects the bytes from a successful `load`
## and clears on collection — so [AppleSecureStore] is exercised against the protocol it
## meets in production, never against a real Keychain.
##
## Prime a status with [member next_store_status], [member next_load_status] or
## [member next_erase_status] before the call under test; every field defaults to the
## success status so a test only needs to script the case it cares about.
class_name FakeKeychainNative extends Object

## Mirrors `keychainStatus*` in `KeychainStatus.swift`.
const STATUS_SUCCESS: int = 0
const STATUS_ABSENT: int = 1
const STATUS_MISSING_ENTITLEMENT: int = 2
const STATUS_INTERACTION_NOT_ALLOWED: int = 3
const STATUS_FAILED: int = 4

## The status [method load] returns on the next call.
var next_load_status: int = STATUS_SUCCESS

## The status [method store] returns on the next call.
var next_store_status: int = STATUS_SUCCESS

## The status [method erase] returns on the next call.
var next_erase_status: int = STATUS_SUCCESS

## The detail [method lastErrorDetail] reports after a non-success status.
var next_detail: String = ""

## The bytes a successful [method load] hands to the next [method takeLoadedValue] call.
var bytes_to_load: PackedByteArray = PackedByteArray()

var store_call_count: int = 0
var load_call_count: int = 0
var erase_call_count: int = 0
var take_loaded_value_call_count: int = 0

var last_store_account: String = ""
var last_stored_bytes: PackedByteArray = PackedByteArray()
var last_load_account: String = ""
var last_erase_account: String = ""

var _has_loaded_value: bool = false
var _loaded_value: PackedByteArray = PackedByteArray()

func isAvailable() -> bool:
	return true

func serviceName() -> String:
	return "com.example.test.foundrykit.auth"

func lastErrorDetail() -> String:
	return next_detail

func setDebugLogging(_enabled: bool) -> void:
	pass

func store(account: String, value: PackedByteArray) -> int:
	store_call_count += 1
	last_store_account = account
	last_stored_bytes = value
	return next_store_status

func load(account: String) -> int:
	load_call_count += 1
	last_load_account = account
	if next_load_status == STATUS_SUCCESS:
		_has_loaded_value = true
		_loaded_value = bytes_to_load
	else:
		_has_loaded_value = false
		_loaded_value = PackedByteArray()
	return next_load_status

func takeLoadedValue() -> PackedByteArray:
	take_loaded_value_call_count += 1
	if not _has_loaded_value:
		return PackedByteArray()
	var value: PackedByteArray = _loaded_value
	_has_loaded_value = false
	_loaded_value = PackedByteArray()
	return value

func erase(account: String) -> int:
	erase_call_count += 1
	last_erase_account = account
	return next_erase_status
