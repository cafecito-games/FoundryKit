namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name FoundryKitLogTests
extends RefCounted
uses Test

var _log: FoundryKitLog

func before_each() -> void:
	_log = FoundryKitLog.new("foundrykit")
	_log.set_level(LogLevel.DEBUG)
	_log.set_capture_enabled(true)

func test_default_level_suppresses_debug() -> void:
	var log: FoundryKitLog = FoundryKitLog.new("quiet")
	log.set_capture_enabled(true)
	log.debug("hidden")
	Expect.that(log.captured().size()).to_equal(0)

func test_debug_level_emits_debug() -> void:
	_log.debug("visible")
	Expect.that(_log.captured().size()).to_equal(1)

func test_message_is_prefixed_with_logger_name() -> void:
	_log.debug("hello")
	Expect.that(_log.captured()[0]).to_equal("[foundrykit] hello")

func test_child_prefixes_with_dotted_path() -> void:
	var child: FoundryKitLog = _log.child("auth")
	child.set_capture_enabled(true)
	child.debug("hello")
	Expect.that(child.captured()[0]).to_equal("[foundrykit.auth] hello")

func test_child_inherits_parent_level_at_creation() -> void:
	var child: FoundryKitLog = _log.child("auth")
	Expect.that(child.level()).to_equal(LogLevel.DEBUG)

func test_child_level_is_independent_of_parent() -> void:
	var child: FoundryKitLog = _log.child("auth")
	child.set_level(LogLevel.ERROR)
	Expect.that(_log.level()).to_equal(LogLevel.DEBUG)
	Expect.that(child.level()).to_equal(LogLevel.ERROR)

func test_sibling_loggers_do_not_share_level() -> void:
	var first: FoundryKitLog = FoundryKitLog.new("a")
	var second: FoundryKitLog = FoundryKitLog.new("b")
	first.set_level(LogLevel.DEBUG)
	Expect.that(second.level()).to_equal(LogLevel.WARN)

func test_level_ordering_filters_lower_severities() -> void:
	_log.set_level(LogLevel.WARN)
	_log.debug("no")
	_log.info("no")
	_log.warn("yes")
	_log.error("yes")
	Expect.that(_log.captured().size()).to_equal(2)
