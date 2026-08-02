namespace games.cafecito.foundrykit.core

## The platform families FoundryKit selects backends for.
##
## `DESKTOP` covers every non-Apple desktop OS; macOS is separate because it can run
## either the Apple native backend or the desktop backend depending on configuration.
enum_name PlatformKind:
	UNKNOWN = 0
	IOS = 1
	MACOS = 2
	ANDROID = 3
	DESKTOP = 4
