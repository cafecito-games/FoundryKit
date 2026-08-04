namespace games.cafecito.foundrykit.tests.support

import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal

## A scripted [SecureStore] double: no Keychain, no [SceneTree], no waiting.
##
## Prime the outcome [method load] should return with [member next_load_outcome], run the
## code under test, then assert on what was written via [member last_stored_bytes] and the
## call counters.
##
## Resolves synchronously — no method ever suspends — so a test can drive several calls in
## a row without pumping frames.
class_name FakeSecureStore extends RefCounted
uses SecureStore

## Whether [method is_available] reports storage as usable. Defaults to available.
var available: bool = true

## Whether durable storage currently holds a value.
var stored_value_present: bool = false

## How many times [method store] has been called since construction or [method reset].
var store_count: int = 0

## How many times [method erase] has been called since construction or [method reset].
var erase_count: int = 0

## The bytes most recently passed to [method store].
var last_stored_bytes: PackedByteArray = PackedByteArray()

## The outcome the next call to [method load] resolves with.
var next_load_outcome: SecureLoadOutcome = SecureLoadOutcome.Absent

## The result the next call to [method store] resolves with.
var next_store_result: CompletionResult = CompletionResult.Success

## The result the next call to [method erase] resolves with.
var next_erase_result: CompletionResult = CompletionResult.Success

## Resets every recorded value and counter, so one instance can serve several phases of a
## test without carrying state across them.
func reset() -> void:
	store_count = 0
	erase_count = 0
	stored_value_present = false
	last_stored_bytes = PackedByteArray()
	next_load_outcome = SecureLoadOutcome.Absent
	next_store_result = CompletionResult.Success
	next_erase_result = CompletionResult.Success

func is_available() -> bool:
	return available

func has_value() -> bool:
	return available and stored_value_present

async func store(bytes: PackedByteArray) -> CompletionResult:
	store_count += 1
	last_stored_bytes = bytes
	match next_store_result:
		CompletionResult.Success:
			stored_value_present = true
		CompletionResult.Failure(_error):
			pass
	return next_store_result

async func load() -> SecureLoadOutcome:
	return next_load_outcome

async func erase() -> CompletionResult:
	erase_count += 1
	match next_erase_result:
		CompletionResult.Success:
			stored_value_present = false
		CompletionResult.Failure(_error):
			pass
	return next_erase_result
