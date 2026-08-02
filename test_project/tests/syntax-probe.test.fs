namespace games.cafecito.foundrykit.tests

import foundry.testlib

## Proves the Foundry Script features FoundryKit's core design depends on are available.
##
## If any assertion here fails to compile, the affected core design must be revised
## before the core layer is built on top of it.
class_name SyntaxProbeTests
extends RefCounted
uses Test

enum Shape:
	Empty
	Circle(radius: float)
	Rect(width: float, height: float)

tuple Pair(first: int, second: int)

trait Holder[T]:
	abstract func held() -> T

class IntHolder uses Holder[int]:
	var _value: int = 7

	func held() -> int:
		return _value

## Mirrors BackendFactory: abstract requirements plus a concrete method that composes
## them, so implementors supply only the pieces that vary.
trait Selector[T]:
	abstract func candidate() -> T?
	abstract func fallback() -> T

	func select() -> T:
		var chosen: T? = candidate()
		if chosen == null:
			return fallback()
		return chosen

class PresentSelector uses Selector[String]:
	func candidate() -> String?:
		return "present"

	func fallback() -> String:
		return "fallback"

class AbsentSelector uses Selector[String]:
	func candidate() -> String?:
		return null

	func fallback() -> String:
		return "fallback"

var _backing: int = 0

var doubled: int:
	get:
		return _backing * 2
	set(value):
		_backing = value / 2

## The rest parameter is deliberately untyped: the engine rejects a typed array here
## ("Typed arrays are currently not supported for the rest parameter").
##
## This mirrors how NativeRequest fans a native signal's arguments into a payload
## dictionary — elements stay Variant and are never numerically converted.
func _collect_all(...values: Array) -> Dictionary[String, Variant]:
	var collected: Dictionary[String, Variant] = {}
	for index: int in range(values.size()):
		collected["field_%d" % index] = values[index]
	return collected

func test_tagged_union_matches_exhaustively_with_payload_binds() -> void:
	var shape: Shape = Shape.Rect(3.0, 4.0)
	var area: float = 0.0
	match shape:
		Shape.Empty:
			area = 0.0
		Shape.Circle(radius):
			area = 3.14159 * radius * radius
		Shape.Rect(width, height):
			area = width * height
	Expect.that(area).to_equal(12.0)

func test_tagged_union_payload_less_case_is_a_value() -> void:
	var shape: Shape = Shape.Empty
	var matched: bool = false
	match shape:
		Shape.Empty:
			matched = true
		Shape.Circle(_radius):
			matched = false
		Shape.Rect(_width, _height):
			matched = false
	Expect.that(matched).to_be_true()

func test_named_tuple_fields_are_readable() -> void:
	var pair: Pair = Pair(2, 5)
	Expect.that(pair.first).to_equal(2)
	Expect.that(pair.second).to_equal(5)

func test_generic_trait_is_satisfiable() -> void:
	var holder: IntHolder = IntHolder.new()
	Expect.that(holder.held()).to_equal(7)

func test_trait_concrete_method_composes_abstract_requirements() -> void:
	Expect.that(PresentSelector.new().select()).to_equal("present")
	Expect.that(AbsentSelector.new().select()).to_equal("fallback")

func test_property_accessors_run() -> void:
	doubled = 10
	Expect.that(_backing).to_equal(5)
	Expect.that(doubled).to_equal(10)

func test_rest_parameter_collects_arguments_into_payload() -> void:
	var collected: Dictionary[String, Variant] = _collect_all("token", "user@example.com")
	Expect.that(collected.size()).to_equal(2)
	Expect.that(str(collected["field_0"])).to_equal("token")
	Expect.that(str(collected["field_1"])).to_equal("user@example.com")

func test_rest_parameter_accepts_no_arguments() -> void:
	Expect.that(_collect_all().size()).to_equal(0)

func test_nullable_type_accepts_null() -> void:
	var maybe: RefCounted? = null
	Expect.that(maybe == null).to_be_true()
