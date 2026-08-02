namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name NativeBridgeTests
extends RefCounted
uses Test

var _log: FoundryKitLog
var _bridge: NativeBridge

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_log.set_level(LogLevel.DEBUG)
	_log.set_capture_enabled(true)
	_bridge = NativeBridge.new(_log)

func test_unregistered_class_is_unavailable() -> void:
	Expect.that(_bridge.is_available("NoSuchNativeClass")).to_be_false()

func test_registered_instantiable_class_is_available() -> void:
	Expect.that(_bridge.is_available("RefCounted")).to_be_true()

func test_instantiate_returns_null_for_unregistered_class() -> void:
	Expect.that(_bridge.instantiate("NoSuchNativeClass") == null).to_be_true()

func test_instantiate_returns_instance_for_registered_class() -> void:
	var created: Object? = _bridge.instantiate("RefCounted")
	Expect.that(created != null).to_be_true()

func test_missing_class_is_logged_at_debug() -> void:
	_bridge.instantiate("NoSuchNativeClass")
	Expect.that(_log.captured().size()).to_equal(1)

func test_registered_but_abstract_class_is_unavailable() -> void:
	# InputEvent is registered but abstract, so it exists and cannot be instantiated.
	Expect.that(ClassDB.class_exists("InputEvent")).to_be_true()
	Expect.that(_bridge.is_available("InputEvent")).to_be_false()

func test_empty_class_name_is_unavailable() -> void:
	Expect.that(_bridge.is_available("")).to_be_false()
