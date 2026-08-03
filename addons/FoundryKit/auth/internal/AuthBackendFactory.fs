namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.core

## Selects the authentication backend for a platform.
##
## Supplies only the two requirements [BackendFactory] declares; the shared fallback rule
## in `resolve()` does the rest. Returning null from [method for_platform] covers both an
## unsupported platform and a removed binary — deliberately the same path.
class_name AuthBackendFactory extends RefCounted
uses BackendFactory[AuthBackend]

var _log: FoundryKitLog

func _init(log: FoundryKitLog) -> void:
	_log = log

## Returns the platform backend, or null when none applies.
##
## Every platform returns null today because no native backend exists yet: epic B adds the
## Apple backend, epic F the Android one, and epic D the desktop OAuth backend. Until then
## the Null backend is the correct answer everywhere, not a placeholder.
func for_platform(_platform: PlatformKind) -> AuthBackend?:
	return null

func null_backend() -> AuthBackend:
	return NullAuthBackend.new(_log)
