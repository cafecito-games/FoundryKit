namespace games.cafecito.foundrykit.core

## The single outcome shape shared by every native request.
##
## Core stops here deliberately. Tagged unions cannot be generic, so there is no shared
## [code]Result[T, E][/code]; each subsystem maps this union into its own typed result
## union, and that mapping is the only place an untyped payload dictionary appears.
enum_name NativeOutcome:
	## The native reported success. Payload keys are defined by the calling subsystem.
	Succeeded(payload: Dictionary[String, Variant])
	## The native reported a failure with its own numeric code.
	Failed(code: int, message: String)
	## The native never answered within the watchdog window.
	TimedOut(elapsed_seconds: float)
	## The app regained focus with the request still outstanding, which means the user
	## dismissed the native sheet without the native emitting anything. Distinct from
	## [code]TimedOut[/code]: detectable in about a second rather than after the full
	## watchdog window. Subsystems map this to their own cancellation case.
	Abandoned
	## A required native class is not registered — an unsupported platform, or a
	## subsystem binary the consumer removed. The two are deliberately indistinguishable.
	Unavailable(missing_class: String)
