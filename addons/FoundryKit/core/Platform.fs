namespace games.cafecito.foundrykit.core

## Maps engine OS names onto [PlatformKind].
class_name Platform extends RefCounted

## Returns the platform family the game is currently running on.
static func current() -> PlatformKind:
	return from_os_name(OS.get_name())

## Maps an engine OS name onto a platform family.
##
## Unrecognised names return [constant PlatformKind.UNKNOWN] so callers fall back to a
## null backend rather than failing.
static func from_os_name(os_name: String) -> PlatformKind:
	match os_name:
		"iOS":
			return PlatformKind.IOS
		"macOS":
			return PlatformKind.MACOS
		"Android":
			return PlatformKind.ANDROID
		"Linux", "Windows", "X11", "FreeBSD", "NetBSD", "OpenBSD", "BSD":
			return PlatformKind.DESKTOP
		_:
			return PlatformKind.UNKNOWN

## Returns whether the platform is an Apple platform.
static func is_apple(platform: PlatformKind) -> bool:
	match platform:
		PlatformKind.IOS, PlatformKind.MACOS:
			return true
		_:
			return false
