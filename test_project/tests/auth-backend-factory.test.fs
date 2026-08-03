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

func test_every_platform_resolves_to_null_backend_for_now() -> void:
	for platform: PlatformKind in [PlatformKind.IOS, PlatformKind.MACOS, PlatformKind.ANDROID,
			PlatformKind.DESKTOP, PlatformKind.UNKNOWN]:
		var backend: AuthBackend = _factory.resolve(platform)
		Expect.that(backend.backend_name()).to_equal("null")

func test_resolved_backend_reports_unavailable() -> void:
	var backend: AuthBackend = _factory.resolve(PlatformKind.IOS)
	Expect.that(backend.is_available(Provider.GOOGLE)).to_be_false()

func test_resolve_current_returns_a_backend() -> void:
	var backend: AuthBackend = _factory.resolve_current()
	Expect.that(backend.backend_name()).to_equal("null")

func test_each_resolve_returns_a_usable_instance() -> void:
	var first: AuthBackend = _factory.resolve(PlatformKind.ANDROID)
	var second: AuthBackend = _factory.resolve(PlatformKind.ANDROID)
	Expect.that(first.backend_name()).to_equal("null")
	Expect.that(second.backend_name()).to_equal("null")
