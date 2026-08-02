namespace games.cafecito.foundrykit.core

## A named, leveled logger.
##
## Instances are deliberately independent: [code]FoundryKitCore[/code] is shared by three
## dynamically loaded frameworks, so a static level flag would be contended across
## subsystems. Each subsystem receives its own logger via [method child].
class_name FoundryKitLog extends RefCounted

const _DEFAULT_LEVEL: LogLevel = LogLevel.WARN

var _name: String = ""
var _level: LogLevel = _DEFAULT_LEVEL
var _capture_enabled: bool = false
var _captured: Array[String] = []

func _init(logger_name: String) -> void:
	_name = logger_name

## Returns a child logger named [code]<parent>.<child_name>[/code], inheriting the
## parent's current level.
func child(child_name: String) -> FoundryKitLog:
	var created: FoundryKitLog = FoundryKitLog.new("%s.%s" % [_name, child_name])
	created.set_level(_level)
	return created

func level() -> LogLevel:
	return _level

func set_level(new_level: LogLevel) -> void:
	_level = new_level

## Enables in-memory capture instead of engine output. Used by tests.
func set_capture_enabled(enabled: bool) -> void:
	_capture_enabled = enabled
	if not enabled:
		_captured.clear()

## Returns messages captured since capture was enabled.
func captured() -> Array[String]:
	return _captured.duplicate()

func debug(message: String) -> void:
	_emit(LogLevel.DEBUG, message)

func info(message: String) -> void:
	_emit(LogLevel.INFO, message)

func warn(message: String) -> void:
	_emit(LogLevel.WARN, message)

func error(message: String) -> void:
	_emit(LogLevel.ERROR, message)

func _emit(severity: LogLevel, message: String) -> void:
	if severity < _level:
		return
	var formatted: String = "[%s] %s" % [_name, message]
	if _capture_enabled:
		_captured.append(formatted)
		return
	match severity:
		LogLevel.DEBUG, LogLevel.INFO:
			print(formatted)
		LogLevel.WARN:
			push_warning(formatted)
		LogLevel.ERROR:
			push_error(formatted)
