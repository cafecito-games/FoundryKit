namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name PlatformTests
extends RefCounted
uses Test

func test_apple_mobile_maps_to_ios() -> void:
	Expect.that(Platform.from_os_name("iOS")).to_equal(PlatformKind.IOS)

func test_apple_desktop_maps_to_macos() -> void:
	Expect.that(Platform.from_os_name("macOS")).to_equal(PlatformKind.MACOS)

func test_android_maps_to_android() -> void:
	Expect.that(Platform.from_os_name("Android")).to_equal(PlatformKind.ANDROID)

func test_every_desktop_os_name_maps_to_desktop() -> void:
	for name: String in ["Linux", "Windows", "X11", "FreeBSD", "NetBSD", "OpenBSD", "BSD"]:
		Expect.that(Platform.from_os_name(name)).to_equal(PlatformKind.DESKTOP)

func test_unknown_os_name_maps_to_unknown() -> void:
	Expect.that(Platform.from_os_name("Dreamcast")).to_equal(PlatformKind.UNKNOWN)

func test_empty_os_name_maps_to_unknown() -> void:
	Expect.that(Platform.from_os_name("")).to_equal(PlatformKind.UNKNOWN)

func test_current_matches_host_os_name() -> void:
	Expect.that(Platform.current()).to_equal(Platform.from_os_name(OS.get_name()))

func test_is_apple_is_true_only_for_ios_and_macos() -> void:
	Expect.that(Platform.is_apple(PlatformKind.IOS)).to_be_true()
	Expect.that(Platform.is_apple(PlatformKind.MACOS)).to_be_true()
	Expect.that(Platform.is_apple(PlatformKind.ANDROID)).to_be_false()
	Expect.that(Platform.is_apple(PlatformKind.DESKTOP)).to_be_false()
	Expect.that(Platform.is_apple(PlatformKind.UNKNOWN)).to_be_false()
