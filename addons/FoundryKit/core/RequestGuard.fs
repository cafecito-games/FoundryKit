namespace games.cafecito.foundrykit.core

## Serialises native requests and detects abandoned native sheets.
##
## Native sign-in and purchase sheets take over the screen, so at most one request may be
## outstanding at a time. A user can also dismiss such a sheet without the native emitting
## anything; the only observable signal is the app regaining focus with a request still
## active. After a short grace period — long enough for a real native response to arrive
## first — this emits [signal recovery_due] so the subsystem can abandon the request.
class_name RequestGuard extends RefCounted

const _DEFAULT_GRACE_SECONDS: float = 1.0

## Emitted when an active request should be treated as abandoned.
signal recovery_due()

var _log: FoundryKitLog
var _is_active: bool = false
var _was_backgrounded: bool = false
var _grace_seconds: float = _DEFAULT_GRACE_SECONDS
var _request_generation: int = 0
var _focus_generation: int = 0

func _init(log: FoundryKitLog) -> void:
	_log = log

## Claims the gate. Returns false when a request is already outstanding.
func begin() -> bool:
	if _is_active:
		_log.warn("rejected a request while another is still in progress")
		return false
	_is_active = true
	_was_backgrounded = false
	_request_generation += 1
	return true

## Releases the gate.
func end() -> void:
	_is_active = false
	_was_backgrounded = false
	_request_generation += 1

func is_active() -> bool:
	return _is_active

func was_backgrounded() -> bool:
	return _was_backgrounded

## Sets the delay before a backgrounded active request is treated as abandoned. Must stay
## long enough for a genuine native response to arrive first.
func set_grace_seconds(seconds: float) -> void:
	_grace_seconds = seconds

## Records that the app lost focus. Only meaningful while a request is active. Also
## invalidates any grace timer scheduled by an earlier foreground return, so a second
## backgrounding before that timer fires cannot report the request abandoned.
func notify_focus_lost() -> void:
	_focus_generation += 1
	if not _is_active:
		return
	_was_backgrounded = true
	_log.debug("request backgrounded")

## Records that the app regained focus, scheduling recovery when a backgrounded request
## is still outstanding.
func notify_focus_gained() -> void:
	if not _is_active or not _was_backgrounded:
		return
	_log.debug("request returned to foreground; scheduling recovery")
	var scheduled_request_generation: int = _request_generation
	var scheduled_focus_generation: int = _focus_generation
	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree == null:
		recovery_due.emit()
		return
	var timer: SceneTreeTimer = tree.create_timer(_grace_seconds)
	timer.timeout.connect(_on_grace_elapsed.bind(scheduled_request_generation, scheduled_focus_generation))

## Fires [signal recovery_due] only when both the request and the foreground return that
## scheduled this callback are still current — guarding against a stale timer from a
## request that has since ended, or a foreground return that was followed by another
## focus loss before the grace period elapsed.
func _on_grace_elapsed(scheduled_request_generation: int, scheduled_focus_generation: int) -> void:
	if not _is_active or scheduled_request_generation != _request_generation:
		return
	if scheduled_focus_generation != _focus_generation:
		return
	recovery_due.emit()
