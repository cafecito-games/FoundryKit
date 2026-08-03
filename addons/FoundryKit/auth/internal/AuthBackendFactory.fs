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
## Android is epic F and the desktop OAuth loopback is epic D; both still fall back to the
## Null backend, which is the correct answer until those exist. The Apple backend is
## returned whether or not its binary is installed — it probes [ClassDB] itself and reports
## unavailable when the class is absent, so routing never depends on the binary.
func for_platform(platform: PlatformKind) -> AuthBackend?:
	match platform:
		PlatformKind.IOS, PlatformKind.MACOS:
			return AppleAuthBackend.new(_log)
		PlatformKind.ANDROID, PlatformKind.DESKTOP, PlatformKind.UNKNOWN:
			return null
	return null

func null_backend() -> AuthBackend:
	return NullAuthBackend.new(_log)
