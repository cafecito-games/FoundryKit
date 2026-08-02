namespace games.cafecito.foundrykit.tests.support

import games.cafecito.foundrykit.core

## A factory that supplies platform backends for Apple and Android only.
class_name FakeBackendFactory extends RefCounted
uses BackendFactory[FakeBackend]

func for_platform(platform: PlatformKind) -> FakeBackend?:
	match platform:
		PlatformKind.IOS, PlatformKind.MACOS:
			return FakePlatformBackend.new("apple")
		PlatformKind.ANDROID:
			return FakePlatformBackend.new("android")
		PlatformKind.DESKTOP, PlatformKind.UNKNOWN:
			return null
	return null

func null_backend() -> FakeBackend:
	return FakeNullBackend.new()
