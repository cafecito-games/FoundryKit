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
## Google only for provider sign-in. Session persistence is delegated to [SecureStore],
## which uses the Keychain on Apple platforms and reports unavailable when its binary is
## absent.
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
const _STORAGE_UNAVAILABLE_DETAIL: String = "secure session storage is unavailable on this platform"
const _STORAGE_ABSENT_DETAIL: String = "no stored session is available"
const _STORAGE_ORIGIN_DETAIL: String = "the stored session belongs to a different backend origin"
const _STORAGE_MALFORMED_DETAIL: String = "the stored session record is malformed"
const _STORAGE_VERSION_DETAIL: String = "the stored session record uses an unsupported schema version"

## The failure codes the Google native emits, mirrored from `SignInOutcome.swift`.
##
## They are this native's protocol, not a shared vocabulary, so translating them is this
## backend's job: [method AuthError.from_native] knows only that a native failed, and
## would report an ordinary cancellation as a request failure.
const _NATIVE_ERROR_CANCELLED: int = 0
const _NATIVE_ERROR_NO_CREDENTIAL: int = 1
const _NATIVE_ERROR_UNAVAILABLE: int = 2

var _log: FoundryKitLog
var _native: Object? = null
var _secure_store: SecureStore
var _request_count: int = 0
var _has_stored_session: bool = false

## [param native_override] and [param secure_store_override] let tests inject the two
## platform seams. Production passes null for both, so each probes [ClassDB] through its
## own bridge and resolves to unavailable when the binary is absent.
func _init(
		log: FoundryKitLog,
		native_override: Object? = null,
		secure_store_override: SecureStore? = null) -> void:
	_log = log
	if secure_store_override != null:
		_secure_store = secure_store_override
	else:
		_secure_store = AppleSecureStore.new(log)
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

async func store_session(session: AuthSession, origin: String) -> CompletionResult:
	if not _secure_store.is_available():
		return CompletionResult.Failure(AuthError.Storage(_STORAGE_UNAVAILABLE_DETAIL))
	var record: StoredSession = StoredSession.from_session(session, origin)
	var result: CompletionResult = await _secure_store.store(record.to_bytes())
	match result:
		CompletionResult.Success:
			_has_stored_session = true
		CompletionResult.Failure(_error):
			pass
	return result

async func restore_session(origin: String) -> SessionResult:
	if not _secure_store.is_available():
		return SessionResult.Failure(AuthError.Storage(_STORAGE_UNAVAILABLE_DETAIL))
	var loaded: SecureLoadOutcome = await _secure_store.load()
	match loaded:
		SecureLoadOutcome.Loaded(bytes):
			return await _restore_loaded(bytes, origin)
		SecureLoadOutcome.Absent:
			_has_stored_session = false
			return SessionResult.Failure(AuthError.Storage(_STORAGE_ABSENT_DETAIL))
		SecureLoadOutcome.Failed(detail):
			return SessionResult.Failure(AuthError.Storage(detail))
	return SessionResult.Failure(AuthError.Storage(_STORAGE_ABSENT_DETAIL))

func has_stored_session() -> bool:
	if not _secure_store.is_available():
		return false
	return _has_stored_session

async func clear_stored_session() -> CompletionResult:
	if not _secure_store.is_available():
		# There cannot be a record to erase when this process has no secure-storage stack.
		# Clearing still reaches the state the caller asked for, matching NullAuthBackend.
		_has_stored_session = false
		return CompletionResult.Success
	var result: CompletionResult = await _secure_store.erase()
	match result:
		CompletionResult.Success:
			_has_stored_session = false
		CompletionResult.Failure(_error):
			pass
	return result

## Parses one loaded record, checks its persisted origin before materialising a session,
## and erases every record this build cannot safely restore.
async func _restore_loaded(bytes: PackedByteArray, origin: String) -> SessionResult:
	var parsed: StoredSessionOutcome = StoredSession.from_bytes(bytes)
	match parsed:
		StoredSessionOutcome.Parsed(record):
			if record.origin != origin:
				return await _erase_rejected_record(_STORAGE_ORIGIN_DETAIL)
			_has_stored_session = true
			return SessionResult.Success(record.to_session())
		StoredSessionOutcome.Malformed(_detail):
			return await _erase_rejected_record(_STORAGE_MALFORMED_DETAIL)
		StoredSessionOutcome.VersionUnsupported(_version):
			return await _erase_rejected_record(_STORAGE_VERSION_DETAIL)
	return SessionResult.Failure(AuthError.Storage(_STORAGE_MALFORMED_DETAIL))

## Attempts the erase exactly once and reports the original rejection as a storage error.
## The returned detail never includes record bytes or token values.
async func _erase_rejected_record(detail: String) -> SessionResult:
	var erased: CompletionResult = await _secure_store.erase()
	_has_stored_session = false
	match erased:
		CompletionResult.Success:
			return SessionResult.Failure(AuthError.Storage(detail))
		CompletionResult.Failure(_error):
			return SessionResult.Failure(AuthError.Storage(
					"%s; the rejected record could not be erased" % detail))
	return SessionResult.Failure(AuthError.Storage(detail))

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
		NativeOutcome.Failed(code, _message):
			return CredentialResult.Failure(_error_from_failure(code, outcome, provider))
		NativeOutcome.TimedOut(_elapsed_seconds):
			return CredentialResult.Failure(AuthError.from_native(outcome, provider))
		NativeOutcome.Abandoned:
			return CredentialResult.Failure(AuthError.from_native(outcome, provider))
		NativeOutcome.Unavailable(_missing_class):
			return CredentialResult.Failure(AuthError.from_native(outcome, provider))
	return _unavailable(provider)

## Translates a native failure code into the auth vocabulary.
##
## The native reserves codes for outcomes that are not faults — the player closing the
## sheet, and a silent sign-in finding no stored account — and for a provider it cannot
## serve at all. Every other code, including the native's own generic one, keeps its code
## and message through [method AuthError.from_native] rather than being reinterpreted.
func _error_from_failure(code: int, outcome: NativeOutcome, provider: Provider) -> AuthError:
	if code == _NATIVE_ERROR_CANCELLED:
		return AuthError.Cancelled
	if code == _NATIVE_ERROR_NO_CREDENTIAL:
		return AuthError.NoCredential
	if code == _NATIVE_ERROR_UNAVAILABLE:
		return AuthError.Unavailable(provider)
	return AuthError.from_native(outcome, provider)

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
