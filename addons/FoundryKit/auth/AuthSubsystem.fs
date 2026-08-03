namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core

## The concrete authentication subsystem behind [code]FoundryKit.auth[/code].
##
## Resolves its platform backend once at construction. A backend produces a [Credential];
## exchanging that credential for an [AuthSession] needs a configured backend endpoint,
## which epic C builds — until then the exchange step reports
## [code]AuthError.Configuration[/code] naming what is missing.
##
## That is not a stub: with no backend URL configured there is genuinely nothing to
## exchange against, and reporting it is the correct behaviour.
class_name AuthSubsystem extends RefCounted
uses AuthApi

const _NO_BACKEND: String = "no authentication backend endpoint is configured"

var _log: FoundryKitLog
var _backend: AuthBackend
var _guard: RequestGuard
var _session: AuthSession? = null

func _init(log: FoundryKitLog) -> void:
	_log = log
	_guard = RequestGuard.new(log)
	var factory: AuthBackendFactory = AuthBackendFactory.new(log)
	_backend = factory.resolve_current()
	_log.debug("resolved auth backend '%s'" % _backend.backend_name())

## Returns the guard so the facade can route lifecycle notifications to it.
func request_guard() -> RequestGuard:
	return _guard

func configure(config: ProviderConfig) -> void:
	_backend.configure(config)

func is_available(provider: Provider) -> bool:
	return _backend.is_available(provider)

func is_configured(provider: Provider) -> bool:
	return _backend.is_configured(provider)

async func sign_in(provider: Provider) -> SessionResult:
	return await _exchange(await _backend.sign_in(provider))

async func sign_in_silent(provider: Provider) -> SessionResult:
	return await _exchange(await _backend.sign_in_silent(provider))

async func sign_out(provider: Provider) -> CompletionResult:
	_session = null
	return await _backend.sign_out(provider)

func has_session() -> bool:
	return _session != null

func access_token() -> String:
	var session: AuthSession? = _session
	if session == null:
		return ""
	var active: AuthSession = session
	return active.access_token

async func valid_access_token() -> TokenResult:
	return TokenResult.Failure(AuthError.Configuration(_NO_BACKEND))

async func refresh_session() -> SessionResult:
	return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))

async func restore_session() -> SessionResult:
	return await _backend.restore_session()

async func clear_session() -> CompletionResult:
	_session = null
	return await _backend.clear_stored_session()

async func request(_method: HttpMethod, _path: String, _body: Variant) -> ResponseResult:
	return ResponseResult.Failure(AuthError.Configuration(_NO_BACKEND))

## Exchanges a credential for a backend session.
##
## Epic C replaces the failure branch with a real exchange; the credential-failure branch
## already behaves correctly and does not change.
async func _exchange(credential_result: CredentialResult) -> SessionResult:
	match credential_result:
		CredentialResult.Failure(error):
			return SessionResult.Failure(error)
		CredentialResult.Success(credential):
			_log.debug("credential obtained for provider %d, awaiting backend exchange"
					% Credential.provider_of(credential))
			return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))
	return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))
