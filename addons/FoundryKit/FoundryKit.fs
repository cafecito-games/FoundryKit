@autoload
namespace games.cafecito.foundrykit

import games.cafecito.foundrykit.core

## The FoundryKit autoload: the single entry point for every subsystem.
##
## One autoload rather than one per subsystem, so there is a single owner for application
## lifecycle notifications and no autoload ordering to manage. Subsystem accessors are
## lazy, so a game that uses one subsystem never constructs the others.
class_name FoundryKit extends Node

const _ROOT_LOGGER_NAME: String = "foundrykit"

var _log: FoundryKitLog = FoundryKitLog.new(_ROOT_LOGGER_NAME)
var _guards: Array[RequestGuard] = []

## Returns the root logger. Subsystems receive children of it.
func log() -> FoundryKitLog:
	return _log

## Returns the platform family the game is running on.
func platform() -> PlatformKind:
	return Platform.current()

## Enables or disables verbose FoundryKit logging.
func set_debug_logging(enabled: bool) -> void:
	_log.set_level(LogLevel.DEBUG if enabled else LogLevel.WARN)

## Registers a guard to receive application lifecycle notifications. Registering the same
## guard twice has no additional effect.
func register_guard(guard: RequestGuard) -> void:
	if _guards.has(guard):
		return
	_guards.append(guard)

## Unregisters a guard from application lifecycle notifications.
func unregister_guard(guard: RequestGuard) -> void:
	_guards.erase(guard)

## Returns the number of currently registered guards.
func guard_count() -> int:
	return _guards.size()

## Notifies every registered guard that the app lost focus.
func notify_focus_lost() -> void:
	for guard: RequestGuard in _guards:
		guard.notify_focus_lost()

## Notifies every registered guard that the app regained focus.
func notify_focus_gained() -> void:
	for guard: RequestGuard in _guards:
		guard.notify_focus_gained()

func _notification(what: int) -> void:
	match what:
		NOTIFICATION_APPLICATION_FOCUS_OUT, NOTIFICATION_APPLICATION_PAUSED:
			notify_focus_lost()
		NOTIFICATION_APPLICATION_FOCUS_IN, NOTIFICATION_APPLICATION_RESUMED:
			notify_focus_gained()
