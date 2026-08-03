namespace games.cafecito.foundrykit.tests.support

## A stand-in for the system browser: records the URL it was asked to open, opens nothing.
##
## [DesktopAuthBackend] takes its browser opener as a [Callable] returning whether the URL
## was handed off, so a test injects [method open_url] directly and never launches a real
## browser — which on a headless runner would either fail or, worse, succeed.
##
## Set [member opens] to false to exercise the path where the host has no browser to open:
## a flow that cannot show a consent screen must fail promptly and release its port rather
## than wait out the watchdog for a callback nobody will ever send.
class_name FakeBrowser extends RefCounted

## Whether [method open_url] reports success. Set false to simulate a host with no browser.
var opens: bool = true

## How many times [method open_url] has been called since construction.
var open_count: int = 0

## The URL of the most recent call, exactly as it was passed.
var last_url: String = ""

## Records the request and reports what [member opens] says.
func open_url(url: String) -> bool:
	open_count += 1
	last_url = url
	return opens
