namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core

class_name AuthBackendFactoryTests
extends RefCounted
uses Test

var _factory: AuthBackendFactory

func before_each() -> void:
	_factory = AuthBackendFactory.new(FoundryKitLog.new("test"))

func test_apple_platforms_resolve_to_the_apple_backend() -> void:
	for platform: PlatformKind in [PlatformKind.IOS, PlatformKind.MACOS]:
		var backend: AuthBackend = _factory.resolve(platform)
		Expect.that(backend.backend_name()).to_equal("apple")

func test_other_platforms_still_resolve_to_null() -> void:
	for platform: PlatformKind in [PlatformKind.ANDROID, PlatformKind.DESKTOP,
			PlatformKind.UNKNOWN]:
		var backend: AuthBackend = _factory.resolve(platform)
		Expect.that(backend.backend_name()).to_equal("null")

func test_apple_backend_reports_unavailable_without_the_native_binary() -> void:
	# The headless suite runs with no binaries, so the native class is absent.
	var backend: AuthBackend = _factory.resolve(PlatformKind.IOS)
	Expect.that(backend.is_available(Provider.GOOGLE)).to_be_false()

## `resolve_current` returns whatever the host platform maps to, which is now the Apple
## backend on a developer's Mac and the Null backend on the Linux CI runner. Asserting a
## specific name here would make the suite pass in one place and fail in the other.
func test_resolve_current_returns_a_usable_backend() -> void:
	var backend: AuthBackend = _factory.resolve_current()
	Expect.that(backend.backend_name().is_empty()).to_be_false()

func test_each_resolve_returns_a_usable_instance() -> void:
	var first: AuthBackend = _factory.resolve(PlatformKind.ANDROID)
	var second: AuthBackend = _factory.resolve(PlatformKind.ANDROID)
	Expect.that(first.backend_name()).to_equal("null")
	Expect.that(second.backend_name()).to_equal("null")
