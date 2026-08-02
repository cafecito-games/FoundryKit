namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

class_name BackendFactoryTests
extends RefCounted
uses Test

var _factory: FakeBackendFactory

func before_each() -> void:
	_factory = FakeBackendFactory.new()

func test_ios_resolves_to_apple_backend() -> void:
	var backend: FakeBackend = _factory.resolve(PlatformKind.IOS)
	Expect.that(backend.backend_name()).to_equal("apple")

func test_macos_resolves_to_apple_backend() -> void:
	var backend: FakeBackend = _factory.resolve(PlatformKind.MACOS)
	Expect.that(backend.backend_name()).to_equal("apple")

func test_android_resolves_to_android_backend() -> void:
	var backend: FakeBackend = _factory.resolve(PlatformKind.ANDROID)
	Expect.that(backend.backend_name()).to_equal("android")

func test_desktop_resolves_to_null_backend() -> void:
	var backend: FakeBackend = _factory.resolve(PlatformKind.DESKTOP)
	Expect.that(backend.backend_name()).to_equal("null")

func test_unknown_resolves_to_null_backend() -> void:
	var backend: FakeBackend = _factory.resolve(PlatformKind.UNKNOWN)
	Expect.that(backend.backend_name()).to_equal("null")

func test_null_backend_reports_unavailable() -> void:
	var backend: FakeBackend = _factory.resolve(PlatformKind.DESKTOP)
	Expect.that(backend.is_available()).to_be_false()

func test_platform_backend_reports_available() -> void:
	var backend: FakeBackend = _factory.resolve(PlatformKind.IOS)
	Expect.that(backend.is_available()).to_be_true()
