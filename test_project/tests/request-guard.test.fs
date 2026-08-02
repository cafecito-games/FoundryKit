namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name RequestGuardTests
extends RefCounted
uses Test

var _log: FoundryKitLog
var _guard: RequestGuard

var _recovery_due_count: int = 0

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_guard = RequestGuard.new(_log)
	_guard.set_grace_seconds(0.02)
	_recovery_due_count = 0

func _on_recovery_due() -> void:
	_recovery_due_count += 1

func test_first_begin_is_accepted() -> void:
	Expect.that(_guard.begin()).to_be_true()

func test_second_begin_while_active_is_rejected() -> void:
	_guard.begin()
	Expect.that(_guard.begin()).to_be_false()

func test_end_releases_the_gate() -> void:
	_guard.begin()
	_guard.end()
	Expect.that(_guard.begin()).to_be_true()

func test_is_active_reflects_gate_state() -> void:
	Expect.that(_guard.is_active()).to_be_false()
	_guard.begin()
	Expect.that(_guard.is_active()).to_be_true()
	_guard.end()
	Expect.that(_guard.is_active()).to_be_false()

func test_focus_loss_during_active_request_marks_backgrounded() -> void:
	_guard.begin()
	_guard.notify_focus_lost()
	Expect.that(_guard.was_backgrounded()).to_be_true()

func test_focus_loss_without_active_request_does_not_mark_backgrounded() -> void:
	_guard.notify_focus_lost()
	Expect.that(_guard.was_backgrounded()).to_be_false()

func test_focus_regain_on_backgrounded_request_emits_recovery_due() -> void:
	_guard.recovery_due.connect(_on_recovery_due)
	_guard.begin()
	_guard.notify_focus_lost()
	_guard.notify_focus_gained()
	await _guard.recovery_due
	Expect.that(_recovery_due_count).to_equal(1)

func test_focus_regain_without_active_request_emits_nothing() -> void:
	_guard.recovery_due.connect(_on_recovery_due)
	_guard.notify_focus_gained()
	Expect.that(_recovery_due_count).to_equal(0)

func test_end_clears_backgrounded_state() -> void:
	_guard.begin()
	_guard.notify_focus_lost()
	_guard.end()
	Expect.that(_guard.was_backgrounded()).to_be_false()
