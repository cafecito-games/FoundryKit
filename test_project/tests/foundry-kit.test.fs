namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit
import games.cafecito.foundrykit.core

class_name FoundryKitTests
extends RefCounted
uses Test

const _FoundryKitScript = preload("res://addons/FoundryKit/FoundryKit.fs")

var _kit: FoundryKit

func before_each() -> void:
	_kit = _FoundryKitScript.new()

func after_each() -> void:
	_kit.free()

func test_default_log_level_is_warn() -> void:
	Expect.that(_kit.log().level()).to_equal(LogLevel.WARN)

func test_enabling_debug_logging_sets_debug_level() -> void:
	_kit.set_debug_logging(true)
	Expect.that(_kit.log().level()).to_equal(LogLevel.DEBUG)

func test_disabling_debug_logging_restores_warn_level() -> void:
	_kit.set_debug_logging(true)
	_kit.set_debug_logging(false)
	Expect.that(_kit.log().level()).to_equal(LogLevel.WARN)

func test_platform_matches_detected_platform() -> void:
	Expect.that(_kit.platform()).to_equal(Platform.current())

func test_registered_guard_receives_focus_lost() -> void:
	var guard: RequestGuard = RequestGuard.new(_kit.log())
	_kit.register_guard(guard)
	guard.begin()
	_kit.notify_focus_lost()
	Expect.that(guard.was_backgrounded()).to_be_true()

func test_unregistered_guard_receives_nothing() -> void:
	var guard: RequestGuard = RequestGuard.new(_kit.log())
	_kit.register_guard(guard)
	_kit.unregister_guard(guard)
	guard.begin()
	_kit.notify_focus_lost()
	Expect.that(guard.was_backgrounded()).to_be_false()

func test_registering_the_same_guard_twice_registers_once() -> void:
	var guard: RequestGuard = RequestGuard.new(_kit.log())
	_kit.register_guard(guard)
	_kit.register_guard(guard)
	Expect.that(_kit.guard_count()).to_equal(1)
