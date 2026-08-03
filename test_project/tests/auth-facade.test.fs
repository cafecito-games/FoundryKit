namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit
import games.cafecito.foundrykit.auth

class_name AuthFacadeTests
extends RefCounted
uses Test

## FoundryKit is an autoload, so its bare name resolves to the singleton instance type.
## Construct through the preloaded script instead — see CLAUDE.md.
const FoundryKitScript = preload("res://addons/FoundryKit/FoundryKit.fs")

var _kit: FoundryKit

func before_each() -> void:
	_kit = FoundryKitScript.new()

func after_each() -> void:
	_kit.free()

func test_auth_is_not_constructed_until_first_access() -> void:
	Expect.that(_kit.has_auth()).to_be_false()

func test_auth_returns_a_subsystem() -> void:
	var auth: AuthSubsystem = _kit.auth
	Expect.that(auth.has_session()).to_be_false()
	Expect.that(_kit.has_auth()).to_be_true()

func test_auth_is_constructed_once() -> void:
	var first: AuthSubsystem = _kit.auth
	var second: AuthSubsystem = _kit.auth
	Expect.that(first == second).to_be_true()

func test_auth_guard_is_registered_with_the_facade() -> void:
	Expect.that(_kit.guard_count()).to_equal(0)
	var auth: AuthSubsystem = _kit.auth
	Expect.that(auth.has_session()).to_be_false()
	Expect.that(_kit.guard_count()).to_equal(1)
