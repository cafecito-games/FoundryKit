namespace games.cafecito.foundrykit.core

## Selects a subsystem's backend for the running platform.
##
## Implementations supply only [method for_platform] and [method null_backend];
## [method resolve] applies the shared fallback rule. Returning null from
## [method for_platform] covers both an unsupported platform and a subsystem binary the
## consumer removed — the two are deliberately indistinguishable.
trait_name BackendFactory[TBackend]

## Returns the backend for a platform, or null when none applies.
abstract func for_platform(platform: PlatformKind) -> TBackend?

## Returns the no-op backend used whenever no platform backend applies.
abstract func null_backend() -> TBackend

## Returns the backend for a platform, falling back to the null backend.
func resolve(platform: PlatformKind) -> TBackend:
	var selected: TBackend? = for_platform(platform)
	if selected == null:
		return null_backend()
	return selected

## Returns the backend for the platform the game is running on.
func resolve_current() -> TBackend:
	return resolve(Platform.current())
