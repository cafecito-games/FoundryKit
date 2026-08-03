namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.core

## Every way authentication can fail.
##
## Replaces the legacy `AuthenticationErrorCode` int enum, its ten parallel `ERROR_*`
## string constants, the `AuthenticationFailure` carrier class, and the three functions
## that converted between them. Each case carries exactly the context that case has.
##
## There is deliberately no `Abandoned` case: a native sheet dismissed without a reply is
## a cancellation from the player's point of view, so [method from_native] folds
## [code]NativeOutcome.Abandoned[/code] into [code]Cancelled[/code].
enum_name AuthError:
	## The player dismissed the flow, or a native sheet was abandoned.
	Cancelled
	## The provider completed without returning a credential.
	NoCredential
	## No backend supports this provider here — unsupported platform, or a removed binary.
	Unavailable(provider: Provider)
	## Required configuration is absent or malformed.
	Configuration(detail: String)
	## Secure storage refused a read or write.
	Storage(detail: String)
	## A backend HTTP request returned a non-success status.
	RequestFailed(status: int, body: String)
	## A response was received but could not be interpreted.
	InvalidResponse(detail: String)
	## A response was well-formed but omitted a required field.
	MissingField(field: String)
	## The active session is past its expiry and could not be refreshed.
	SessionExpired(expired_at: int)
	## The operation exceeded its watchdog window.
	TimedOut(elapsed_seconds: float)

	## Maps a core [NativeOutcome] onto an auth error.
	##
	## [param provider] supplies context the outcome itself does not carry.
	static func from_native(outcome: NativeOutcome, provider: Provider) -> AuthError:
		match outcome:
			NativeOutcome.Succeeded(_payload):
				return AuthError.InvalidResponse(
						"native reported success where a failure was expected")
			NativeOutcome.Failed(code, message):
				return AuthError.RequestFailed(code, message)
			NativeOutcome.TimedOut(elapsed_seconds):
				return AuthError.TimedOut(elapsed_seconds)
			NativeOutcome.Abandoned:
				return AuthError.Cancelled
			NativeOutcome.Unavailable(_missing_class):
				return AuthError.Unavailable(provider)
		return AuthError.Cancelled
