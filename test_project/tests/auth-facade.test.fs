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

## Names every method [AuthApi] requires, so one added to the contract and forgotten on the
## subsystem the facade builds fails here rather than in a consumer's game.
const _API_METHODS: Array[String] = [
	"configure",
	"configure_backend",
	"is_available",
	"is_configured",
	"sign_in",
	"sign_in_silent",
	"sign_out",
	"has_session",
	"access_token",
	"valid_access_token",
	"refresh_session",
	"restore_session",
	"clear_session",
	"request",
]

## Names every signal [AuthApi] declares.
const _API_SIGNALS: Array[String] = [
	"session_expired",
	"tokens_refreshed",
]

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

func test_the_backend_is_unconfigured_until_a_consumer_supplies_one() -> void:
	# The subsystem the facade builds gets no backend configuration, so every path that needs
	# the backend reports Configuration rather than failing later with an opaque error.
	var auth: AuthSubsystem = _kit.auth
	Expect.that(_session_failure_name(await auth.refresh_session())).to_equal("configuration")

func test_configuring_the_backend_through_the_facade_resolves_the_missing_configuration() -> void:
	# Refresh is the cheapest proof: with no session it never reaches the transport, so the
	# error it reports names which absence the subsystem is still complaining about. Before
	# configuration that is the endpoint; after it, only the missing session remains.
	var auth: AuthSubsystem = _kit.auth
	auth.configure_backend(BackendConfig.new("https://api.example.com"))
	Expect.that(_session_failure_name(await auth.refresh_session())).to_equal("session_expired")

func test_configuring_the_backend_keeps_the_paths_the_configuration_names() -> void:
	var auth: AuthSubsystem = _kit.auth
	auth.configure_backend(BackendConfig.new(
			"https://api.example.com", "/exchange", "/refresh", "/sign-out"))
	var config: BackendConfig = auth.backend_config()
	Expect.that(config.base_url).to_equal("https://api.example.com")
	Expect.that(config.exchange_path).to_equal("/exchange")
	Expect.that(config.refresh_path).to_equal("/refresh")
	Expect.that(config.sign_out_path).to_equal("/sign-out")

func test_mutating_the_supplied_configuration_afterwards_does_not_reconfigure_the_subsystem() -> void:
	# The subsystem keeps the values, not the object. A consumer that holds on to the
	# configuration it passed and edits it later must not move the backend under a session
	# that is already running against the old one.
	var auth: AuthSubsystem = _kit.auth
	var supplied: BackendConfig = BackendConfig.new("https://api.example.com")
	auth.configure_backend(supplied)
	supplied.base_url = "https://elsewhere.example.com"
	Expect.that(auth.backend_config().base_url).to_equal("https://api.example.com")

func test_the_subsystem_answers_the_whole_auth_api() -> void:
	# Composition is checked by the analyser, but a trait declared in another file is not
	# flattened into the composing script the way a same-file one is. This sweep is what
	# proves the surface a consumer calls through actually resolves at runtime.
	var auth: AuthSubsystem = _kit.auth
	# Accumulated as text rather than counted, so a failure names what is missing.
	var missing: String = ""
	for method_name: String in _API_METHODS:
		if not auth.has_method(method_name):
			missing += method_name + " "
	for signal_name: String in _API_SIGNALS:
		if not auth.has_signal(signal_name):
			missing += signal_name + " "
	Expect.that(missing).to_equal("")

## Renders a failed session result as a stable name.
##
## Exhaustive over [AuthError]; the trailing return exists only because the analyser cannot
## see that the match is total.
func _session_failure_name(result: SessionResult) -> String:
	match result:
		SessionResult.Success(_session):
			return "unexpected_success"
		SessionResult.Failure(error):
			return _error_name(error)
	return "unreachable"

func _error_name(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(_provider):
			return "unavailable"
		AuthError.Configuration(_detail):
			return "configuration"
		AuthError.Storage(_detail):
			return "storage"
		AuthError.RequestFailed(_status, _body):
			return "request_failed"
		AuthError.InvalidResponse(_detail):
			return "invalid_response"
		AuthError.MissingField(_field):
			return "missing_field"
		AuthError.SessionExpired(_expired_at):
			return "session_expired"
		AuthError.TimedOut(_elapsed_seconds):
			return "timed_out"
	return "unreachable"
