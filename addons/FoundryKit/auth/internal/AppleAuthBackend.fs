namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

## Google Sign-In on Apple platforms.
##
## Drives the [code]iOSGoogleSignIn[/code] native class through [NativeRequest], with a
## fresh correlation token for every call: the native echoes that token as the first
## argument of both its signals, so a late reply from a request that timed out or was
## abandoned can never be mistaken for the current one.
##
## Overlapping sign-ins are the native's concern, not this class's. [code]GIDSignIn[/code]
## is a singleton holding one pending operation, so the native refuses a second sign-in
## while one is in flight and answers it with its own failure — under the refused request's
## token, which keeps every emission attributable to the request that caused it.
##
## Google only in this epic. Sign in with Apple and Keychain-backed session storage belong
## to later epics; both report unavailable here rather than pretending to work.
class_name AppleAuthBackend extends RefCounted
uses AuthBackend

const _GOOGLE_NATIVE_CLASS: String = "iOSGoogleSignIn"
const _SUCCESS_SIGNAL: String = "sign_in_success"
const _FAILURE_SIGNAL: String = "sign_in_failed"

## Names the success signal's arguments after the leading correlation token, which
## [NativeRequest] consumes rather than zipping into the payload.
##
## [code]authorization_code[/code] is always empty for Google and has no home on
## [code]Credential.Google[/code]; it is named so the payload mirrors the signal the
## native actually emits.
const _PAYLOAD_FIELDS: Array[String] = ["id_token", "email", "display_name",
		"authorization_code"]
const _STORAGE_DETAIL: String = "secure session storage is not implemented on this backend yet"

var _log: FoundryKitLog
var _native: Object? = null
var _request_count: int = 0

## [param native_override] lets tests inject a fake native. Production passes null, so the
## backend probes [ClassDB] itself through [NativeBridge] and resolves to an unavailable
## backend when the binary is absent.
func _init(log: FoundryKitLog, native_override: Object? = null) -> void:
	_log = log
	if native_override != null:
		_native = native_override
		return
	var bridge: NativeBridge = NativeBridge.new(log)
	_native = bridge.instantiate(_GOOGLE_NATIVE_CLASS)

func backend_name() -> String:
	return "apple"

func configure(config: ProviderConfig) -> void:
	match config:
		ProviderConfig.Google(web_client_id, _ios_client_id, _desktop_client_id):
			var native: Object? = _native
			if native == null:
				_log.debug("configure(Google) ignored: the native class is absent")
				return
			var target: Object = native
			# The iOS/macOS client ID comes from the host app's Info.plist; the native
			# only needs the web client ID, which becomes the token's audience.
			target.call("initialize", web_client_id)
		ProviderConfig.Apple(_service_id, _redirect_uri):
			_log.debug("configure(Apple) ignored: this backend does not serve Apple yet")
		ProviderConfig.EmailPassword:
			_log.debug("configure(EmailPassword) ignored: not a native provider")

func is_available(provider: Provider) -> bool:
	match provider:
		Provider.GOOGLE:
			return _native != null
		Provider.APPLE, Provider.EMAIL_PASSWORD:
			return false
	return false

func is_configured(provider: Provider) -> bool:
	match provider:
		Provider.GOOGLE:
			var native: Object? = _native
			if native == null:
				return false
			var target: Object = native
			var reported: Variant = target.call("isAvailable")
			return reported is bool and reported == true
		Provider.APPLE, Provider.EMAIL_PASSWORD:
			return false
	return false

async func sign_in(provider: Provider) -> CredentialResult:
	var target: Object? = _sign_in_target(provider)
	if target == null:
		return _unavailable(provider)
	return await _await_native_sign_in(target, provider, false)

async func sign_in_silent(provider: Provider) -> CredentialResult:
	var target: Object? = _sign_in_target(provider)
	if target == null:
		return _unavailable(provider)
	return await _await_native_sign_in(target, provider, true)

## Signs out of the native provider without awaiting its acknowledgement.
##
## [code]signOut[/code] on the native is a local credential purge that cannot fail, and it
## has no failure signal to await against. Awaiting the acknowledgement would add a
## watchdog and a timeout failure mode for an operation with no failure to report.
async func sign_out(provider: Provider) -> CompletionResult:
	var target: Object? = _sign_in_target(provider)
	if target == null:
		# Nothing to sign out of is the state the caller asked for.
		return CompletionResult.Success
	target.call("signOut", _new_correlation_token())
	return CompletionResult.Success

async func store_session(_session: AuthSession) -> CompletionResult:
	return CompletionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

async func restore_session() -> SessionResult:
	return SessionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

func has_stored_session() -> bool:
	return false

async func clear_stored_session() -> CompletionResult:
	return CompletionResult.Success

## Returns the native object able to serve [param provider], or null when none can.
##
## A provider this backend does not serve and an absent binary resolve identically — the
## epic's invariant is that a removed binary is never an error condition.
func _sign_in_target(provider: Provider) -> Object?:
	if provider != Provider.GOOGLE:
		return null
	return _native

## Generates a correlation token unique within this process.
##
## The object ID is unique among live objects, and the counter is unique within this
## backend, so no two requests can share a token even within the same microsecond. This
## distinguishes overlapping requests; it is not a security boundary.
func _new_correlation_token() -> String:
	_request_count += 1
	return "%d-%d-%d" % [get_instance_id(), _request_count, Time.get_ticks_usec()]

## Starts a native sign-in and maps its outcome onto a credential result.
##
## The request is connected before the native call so a native that answers synchronously
## — the unavailable and busy guards do — is still heard, and the returned coroutine is
## awaited immediately, within the window [method NativeRequest.await_outcome] documents
## as safe.
async func _await_native_sign_in(
		target: Object, provider: Provider, silent: bool) -> CredentialResult:
	var correlation_token: String = _new_correlation_token()
	var request: NativeRequest = NativeRequest.new(_log)
	var pending: Coroutine[NativeOutcome] = request.await_outcome(
			target,
			_SUCCESS_SIGNAL,
			_PAYLOAD_FIELDS,
			_FAILURE_SIGNAL,
			NativeRequest.DEFAULT_TIMEOUT_SECONDS,
			correlation_token)

	if silent:
		target.call("signInSilent", correlation_token)
	else:
		# The nonce is empty until the backend exchange issues one in epic C.
		target.call("signIn", "", correlation_token)

	var outcome: NativeOutcome = await pending
	# Every non-success case delegates to the one native-to-auth mapping rather than
	# restating it, while the arms keep the union exhaustively checked.
	match outcome:
		NativeOutcome.Succeeded(payload):
			return _credential_from(payload)
		NativeOutcome.Failed(_code, _message):
			return CredentialResult.Failure(AuthError.from_native(outcome, provider))
		NativeOutcome.TimedOut(_elapsed_seconds):
			return CredentialResult.Failure(AuthError.from_native(outcome, provider))
		NativeOutcome.Abandoned:
			return CredentialResult.Failure(AuthError.from_native(outcome, provider))
		NativeOutcome.Unavailable(_missing_class):
			return CredentialResult.Failure(AuthError.from_native(outcome, provider))
	return _unavailable(provider)

## Builds the credential a successful emission describes.
##
## [code]audience[/code] is deliberately empty: the native does not return it, and epic C
## fills it from the configured web client ID. Inventing one here would be worse than
## leaving it blank, because the backend validates it.
func _credential_from(payload: Dictionary[String, Variant]) -> CredentialResult:
	var id_token: String = str(payload.get("id_token", ""))
	if id_token.is_empty():
		return CredentialResult.Failure(AuthError.MissingField("id_token"))
	return CredentialResult.Success(Credential.Google(
			id_token,
			str(payload.get("email", "")),
			str(payload.get("display_name", "")),
			""))

func _unavailable(provider: Provider) -> CredentialResult:
	_log.debug("sign-in unavailable for provider %d" % provider)
	return CredentialResult.Failure(AuthError.Unavailable(provider))
