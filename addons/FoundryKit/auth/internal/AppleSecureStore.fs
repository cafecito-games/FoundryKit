namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

## Drives the [code]iOSKeychain[/code] native class through the [SecureStore] seam.
##
## Every native call is synchronous — `SecItemAdd`, `SecItemCopyMatching`,
## `SecItemUpdate` and `SecItemDelete` all return before they yield — so unlike
## [AppleAuthBackend]'s Google surface this store needs no [NativeRequest], no signal and
## no correlation token. The trait's methods stay `async` for contract uniformity, but
## none of them ever suspends.
##
## A missing binary is never an error: the headless test suite and any consumer that
## deleted `bin/auth/` from a partial install both resolve here to
## [method is_available] reporting `false`, [method load] reporting
## [enum SecureLoadOutcome.Absent], and [method store] / [method erase] failing with
## [code]AuthError.Storage[/code] — the same shape [NullSecureStore] reports, never a null
## dereference.
class_name AppleSecureStore extends RefCounted
uses SecureStore

const _NATIVE_CLASS: String = "iOSKeychain"

## One session lives under one fixed account within this build's Keychain service; there
## is only ever one signed-in session to persist.
const _ACCOUNT: String = "session"

const _UNAVAILABLE_DETAIL: String = "secure session storage is unavailable on this platform"

## Mirrors `keychainStatus*` in `KeychainStatus.swift`. Kept as named constants here too,
## rather than trusted from the native by convention, so a mismatch between the two sides
## shows up as an unrecognised status rather than a silently wrong mapping.
const _STATUS_SUCCESS: int = 0
const _STATUS_ABSENT: int = 1
const _STATUS_MISSING_ENTITLEMENT: int = 2
const _STATUS_INTERACTION_NOT_ALLOWED: int = 3
const _STATUS_FAILED: int = 4

var _log: FoundryKitLog
var _native: Object? = null

## [param native_override] lets tests inject a fake native. Production passes null, so
## this store probes [ClassDB] itself through [NativeBridge] and resolves to unavailable
## when the binary is absent — mirroring [method AppleAuthBackend._init].
func _init(log: FoundryKitLog, native_override: Object? = null) -> void:
	_log = log
	if native_override != null:
		_native = native_override
		return
	var bridge: NativeBridge = NativeBridge.new(log)
	_native = bridge.instantiate(_NATIVE_CLASS)

func is_available() -> bool:
	return _native != null

async func store(bytes: PackedByteArray) -> CompletionResult:
	var native: Object? = _native
	if native == null:
		return CompletionResult.Failure(AuthError.Storage(_UNAVAILABLE_DETAIL))
	var target: Object = native
	var status: int = _status_of(target.call("store", _ACCOUNT, bytes))
	if status == _STATUS_SUCCESS:
		return CompletionResult.Success
	return CompletionResult.Failure(AuthError.Storage(_failure_detail(target, status)))

async func load() -> SecureLoadOutcome:
	var native: Object? = _native
	if native == null:
		# No native stack to ask means nothing has ever been written from this process's
		# point of view — the same first-launch state a real, empty Keychain reports.
		return SecureLoadOutcome.Absent
	var target: Object = native
	var status: int = _status_of(target.call("load", _ACCOUNT))
	if status == _STATUS_SUCCESS:
		return _take_loaded_value(target)
	if status == _STATUS_ABSENT:
		return SecureLoadOutcome.Absent
	return SecureLoadOutcome.Failed(_failure_detail(target, status))

async func erase() -> CompletionResult:
	var native: Object? = _native
	if native == null:
		return CompletionResult.Failure(AuthError.Storage(_UNAVAILABLE_DETAIL))
	var target: Object = native
	var status: int = _status_of(target.call("erase", _ACCOUNT))
	if status == _STATUS_SUCCESS or status == _STATUS_ABSENT:
		# Erasing something that was never there still satisfies the caller's intent.
		return CompletionResult.Success
	return CompletionResult.Failure(AuthError.Storage(_failure_detail(target, status)))

## Collects the bytes a successful `load` left behind.
##
## `takeLoadedValue()` clears on collection, so this must be called exactly once per
## successful `load` status and never on any other path.
func _take_loaded_value(target: Object) -> SecureLoadOutcome:
	var collected: Variant = target.call("takeLoadedValue")
	if collected is PackedByteArray:
		return SecureLoadOutcome.Loaded(collected)
	return SecureLoadOutcome.Failed(
			"the Keychain reported success but returned no bytes to collect")

## Narrows a native call's `Variant` return to the `int` status it is documented to be.
##
## A value that is not an `int` at all means the native's shape does not match what this
## store expects, which is a failure this store can still report cleanly rather than
## crash on.
func _status_of(value: Variant) -> int:
	if value is int:
		return value
	return _STATUS_FAILED

## Names the problem behind a non-success, non-absent status.
##
## Prefers the native's own detail — it names entitlement and interaction problems by
## number, which a human debugging a signing issue needs — and falls back to the bare
## status code only if the native reports no detail at all.
func _failure_detail(target: Object, status: int) -> String:
	var reported: Variant = target.call("lastErrorDetail")
	var detail: String = ""
	if reported is String:
		detail = reported
	if not detail.is_empty():
		_log.warn(detail)
		return detail
	var fallback: String = _fallback_detail_for(status)
	_log.warn(fallback)
	return fallback

## The detail used when the native reports no [code]lastErrorDetail()[/code] at all.
##
## Every status this store's own constants name gets a specific message; anything else is
## a status neither side anticipated, and the number itself is what a human needs to
## recognise it.
func _fallback_detail_for(status: int) -> String:
	if status == _STATUS_MISSING_ENTITLEMENT:
		return "the Keychain refused the request for want of an entitlement"
	if status == _STATUS_INTERACTION_NOT_ALLOWED:
		return "the Keychain is not accessible in the current device state"
	if status == _STATUS_FAILED:
		return "the Keychain request failed"
	return "the Keychain reported an unrecognised status (%d)" % status
