namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

## Covers the transport seam the whole backend session layer is built on.
##
## What this suite can honestly verify: the scripted double's behaviour, that [HttpClient]
## satisfies [HttpTransport], that a malformed request fails fast, and that a client whose
## caller kept no reference still settles. What it cannot verify is real network
## behaviour — no TLS handshake, no redirect, no server status is exercised here, and the
## `Answered` path of [HttpClient] is therefore unproven by this suite. That gap is
## deliberate and is stated rather than papered over with a test that only appears to
## cover it.
class_name HttpClientTests
extends RefCounted
uses Test

var _fake: FakeHttpClient

func before_each() -> void:
	_fake = FakeHttpClient.new()

## Renders an outcome as a comparable string, so assertions never need a failure helper.
func _describe(outcome: HttpOutcome) -> String:
	match outcome:
		HttpOutcome.Answered(status_code, body):
			return "answered:%d:%d" % [status_code, body.size()]
		HttpOutcome.TransportFailed(detail):
			return "transport:%s" % detail
		HttpOutcome.TimedOut(_elapsed_seconds):
			return "timeout"
	return "unreachable"

func _is_answered(outcome: HttpOutcome) -> bool:
	match outcome:
		HttpOutcome.Answered(_status_code, _body):
			return true
		HttpOutcome.TransportFailed(_detail):
			return false
		HttpOutcome.TimedOut(_elapsed_seconds):
			return false
	return false

## [HttpClient]'s watchdog and its underlying [HTTPRequest] node both require a running
## [SceneTree]. Gate the tests that need one behind this check instead of deleting them,
## so the coverage gap stays visible rather than silent.
func _has_scene_tree() -> bool:
	return Engine.get_main_loop() as SceneTree != null

func test_fake_returns_the_scripted_outcome() -> void:
	_fake.enqueue(HttpOutcome.Answered(204, PackedByteArray()))
	var outcome: HttpOutcome = await _fake.send(
			"GET", "https://example.test/x", PackedStringArray(), PackedByteArray(), 5.0)
	Expect.that(_describe(outcome)).to_equal("answered:204:0")

func test_fake_returns_scripted_outcomes_in_order() -> void:
	_fake.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	_fake.enqueue(HttpOutcome.TimedOut(3.0))
	var first: HttpOutcome = await _fake.send(
			"GET", "https://example.test/a", PackedStringArray(), PackedByteArray(), 5.0)
	var second: HttpOutcome = await _fake.send(
			"GET", "https://example.test/b", PackedStringArray(), PackedByteArray(), 5.0)
	Expect.that(_describe(first)).to_equal("answered:200:0")
	Expect.that(_describe(second)).to_equal("timeout")

func test_fake_records_what_it_was_sent() -> void:
	_fake.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	await _fake.send("POST", "https://example.test/session",
			PackedStringArray(["X-Test: abc"]), "{}".to_utf8_buffer(), 5.0)
	Expect.that(_fake.last_method).to_equal("POST")
	Expect.that(_fake.last_url).to_equal("https://example.test/session")
	Expect.that(_fake.last_headers.has("X-Test: abc")).to_be_true()
	Expect.that(_fake.last_body.get_string_from_utf8()).to_equal("{}")
	Expect.that(_fake.last_timeout_seconds).to_equal(5.0)

func test_fake_counts_sends() -> void:
	Expect.that(_fake.send_count).to_equal(0)
	_fake.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	_fake.enqueue(HttpOutcome.Answered(201, PackedByteArray()))
	await _fake.send("GET", "https://example.test/a",
			PackedStringArray(), PackedByteArray(), 5.0)
	await _fake.send("GET", "https://example.test/b",
			PackedStringArray(), PackedByteArray(), 5.0)
	Expect.that(_fake.send_count).to_equal(2)

func test_fake_with_an_empty_queue_reports_a_transport_failure() -> void:
	# Failing loudly beats returning a plausible 200 nobody scripted.
	var outcome: HttpOutcome = await _fake.send(
			"GET", "https://example.test/x", PackedStringArray(), PackedByteArray(), 5.0)
	Expect.that(_is_answered(outcome)).to_be_false()

func test_fake_can_be_reset() -> void:
	_fake.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	await _fake.send("GET", "https://example.test/a",
			PackedStringArray(), PackedByteArray(), 5.0)
	_fake.reset()
	Expect.that(_fake.send_count).to_equal(0)
	Expect.that(_fake.last_url).to_equal("")

func test_fake_survives_its_caller_dropping_the_reference() -> void:
	# The seam every later issue leans on: hold only the coroutine, not the transport.
	var transport: FakeHttpClient = FakeHttpClient.new()
	transport.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	var pending: Coroutine[HttpOutcome] = transport.send(
			"GET", "https://example.test/x", PackedStringArray(), PackedByteArray(), 5.0)
	transport = _fake
	var outcome: HttpOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("answered:200:0")

func test_real_client_satisfies_the_transport_contract() -> void:
	var client: HttpTransport = HttpClient.new(FoundryKitLog.new("test"))
	Expect.that(client != null).to_be_true()

func test_real_client_rejects_an_unsupported_method_without_a_request() -> void:
	var client: HttpTransport = HttpClient.new(FoundryKitLog.new("test"))
	var outcome: HttpOutcome = await client.send(
			"BREW", "https://example.test/x", PackedStringArray(), PackedByteArray(), 5.0)
	Expect.that(_describe(outcome)).to_equal("transport:unsupported HTTP method \"BREW\"")

func test_real_client_settles_when_the_connection_is_refused() -> void:
	if not _has_scene_tree():
		return
	# Loopback port 1 is closed, so this exercises the full node-and-signal settle path
	# without leaving the machine. The client's only reference is the coroutine below —
	# if its in-flight registry were missing, the request would be freed while suspended.
	var pending: Coroutine[HttpOutcome] = HttpClient.new(FoundryKitLog.new("test")).send(
			"GET", "http://127.0.0.1:1/", PackedStringArray(), PackedByteArray(), 5.0)
	var outcome: HttpOutcome = await pending
	Expect.that(_is_answered(outcome)).to_be_false()

func test_real_client_progresses_while_the_tree_is_paused() -> void:
	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree == null:
		return
	# The request node polls its connection from _process. Under an inherited process mode
	# a paused tree stalls it while the watchdog keeps counting, so a reachable host would
	# be reported as a timeout. Settling promptly here is what proves that is not so.
	tree.paused = true
	var outcome: HttpOutcome = await HttpClient.new(FoundryKitLog.new("test")).send(
			"GET", "http://127.0.0.1:1/", PackedStringArray(), PackedByteArray(), 5.0)
	tree.paused = false
	Expect.that(_describe(outcome)).to_equal("transport:could not connect to the host")

func test_real_client_settles_twice_in_a_row_on_one_instance() -> void:
	if not _has_scene_tree():
		return
	# A transport is long-lived and reused; per-request state must not leak between sends.
	var client: HttpTransport = HttpClient.new(FoundryKitLog.new("test"))
	var first: HttpOutcome = await client.send(
			"GET", "http://127.0.0.1:1/", PackedStringArray(), PackedByteArray(), 5.0)
	var second: HttpOutcome = await client.send(
			"GET", "http://127.0.0.1:1/", PackedStringArray(), PackedByteArray(), 5.0)
	Expect.that(_is_answered(first)).to_be_false()
	Expect.that(_is_answered(second)).to_be_false()
