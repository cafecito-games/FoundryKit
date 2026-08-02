namespace games.cafecito.foundrykit.core

## Guarded access to natively registered classes.
##
## A subsystem's binary may be absent because the platform does not support it, or
## because the consumer removed it from a partial install. Both cases resolve to the same
## unavailable result rather than an error.
class_name NativeBridge extends RefCounted

var _log: FoundryKitLog

func _init(log: FoundryKitLog) -> void:
	_log = log

## Returns whether a native class is registered and can be instantiated.
func is_available(native_class_name: String) -> bool:
	if native_class_name.is_empty():
		return false
	if not ClassDB.class_exists(native_class_name):
		return false
	return ClassDB.can_instantiate(native_class_name)

## Instantiates a native class, or returns null when it is unavailable.
func instantiate(native_class_name: String) -> Object?:
	if not is_available(native_class_name):
		_log.debug("native class '%s' is absent" % native_class_name)
		return null
	return ClassDB.instantiate(native_class_name)
