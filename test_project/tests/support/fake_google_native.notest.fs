namespace games.cafecito.foundrykit.tests.support

## Stands in for the `iOSGoogleSignIn` native class.
##
## Mirrors the real signal and method shape exactly — snake_case signals carrying the
## correlation token as their first argument, camelCase methods matching the names the
## Swift `@Callable` macro registers — so the backend is exercised against the protocol it
## meets in production.
class_name FakeGoogleNative extends Object

signal sign_in_success(request_token: String, id_token: String, email: String,
		display_name: String, authorization_code: String)
signal sign_in_failed(request_token: String, code: int, message: String)
signal sign_out_complete(request_token: String)

var last_request_token: String = ""
var last_nonce: String = ""
var configured_web_client_id: String = ""
var silent_call_count: int = 0
var interactive_call_count: int = 0

func initialize(web_client_id: String) -> void:
	configured_web_client_id = web_client_id

func isAvailable() -> bool:
	return not configured_web_client_id.is_empty()

func setDebugLogging(_enabled: bool) -> void:
	pass

func signIn(nonce: String, request_token: String) -> void:
	last_nonce = nonce
	last_request_token = request_token
	interactive_call_count += 1

func signInSilent(request_token: String) -> void:
	last_request_token = request_token
	silent_call_count += 1

func signOut(request_token: String) -> void:
	last_request_token = request_token
	sign_out_complete.emit(request_token)

func emit_success(request_token: String, id_token: String, email: String,
		display_name: String) -> void:
	sign_in_success.emit(request_token, id_token, email, display_name, "")

func emit_failure(request_token: String, code: int, message: String) -> void:
	sign_in_failed.emit(request_token, code, message)
