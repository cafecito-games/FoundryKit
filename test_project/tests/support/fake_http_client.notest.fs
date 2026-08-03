namespace games.cafecito.foundrykit.tests.support

import games.cafecito.foundrykit.core

## A scripted [HttpTransport] double: no network, no [SceneTree], no waiting.
##
## This is the seam the whole backend session layer is tested through. Queue the outcomes
## a test expects with [method enqueue], run the code under test, then assert on what the
## transport was handed via [member last_method] and its siblings.
##
## Resolves synchronously — [method send] never suspends — so a test can drive several
## requests in a row without pumping frames.
class_name FakeHttpClient extends RefCounted
uses HttpTransport

## How many times [method send] has been called since construction or [method reset].
var send_count: int = 0

## The verb of the most recent request, uppercase or not, exactly as it was passed.
var last_method: String = ""

## The URL of the most recent request.
var last_url: String = ""

## The headers of the most recent request, in [code]"Name: value"[/code] form.
var last_headers: PackedStringArray = PackedStringArray()

## The body of the most recent request.
var last_body: PackedByteArray = PackedByteArray()

## The timeout the most recent request asked for.
var last_timeout_seconds: float = 0.0

var _queued: Array[HttpOutcome] = []

## Queues one outcome. Calls to [method send] consume queued outcomes in order.
func enqueue(outcome: HttpOutcome) -> void:
	_queued.append(outcome)

## Clears the queue and every recorded value, so one instance can serve several phases of
## a test without carrying state across them.
func reset() -> void:
	_queued.clear()
	send_count = 0
	last_method = ""
	last_url = ""
	last_headers = PackedStringArray()
	last_body = PackedByteArray()
	last_timeout_seconds = 0.0

## Records the request and returns the next queued outcome.
##
## An empty queue resolves to [code]TransportFailed[/code] rather than a plausible success:
## a test that forgot to script an outcome should fail, not quietly pass on an invented
## 200.
async func send(
		method: String,
		url: String,
		headers: PackedStringArray,
		body: PackedByteArray,
		timeout_seconds: float) -> HttpOutcome:
	send_count += 1
	last_method = method
	last_url = url
	last_headers = headers
	last_body = body
	last_timeout_seconds = timeout_seconds
	if _queued.is_empty():
		return HttpOutcome.TransportFailed("no outcome was enqueued on FakeHttpClient")
	var outcome: HttpOutcome = _queued[0]
	_queued.remove_at(0)
	return outcome
