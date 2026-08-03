namespace games.cafecito.foundrykit.auth.internal

## How one wait for an OAuth redirect ended.
##
## Deliberately free of authorization vocabulary: an [code]error=access_denied[/code]
## callback is a perfectly ordinary [code]Received[/code], because the listener's job ends
## at "a request arrived and here is its query". Only [DesktopAuthBackend] knows that
## [code]error[/code] means the player pressed Cancel and that a missing [code]code[/code]
## is a malformed callback.
##
## Its own file because only one head type may live in a `.fs` file.
enum_name LoopbackOutcome:
	## A request arrived and its query string was parsed. The dictionary is empty when the
	## request carried no query at all, which is a callback the caller must reject rather
	## than a listener failure.
	Received(query: Dictionary[String, String])
	## No request arrived within the watchdog window.
	TimedOut(elapsed_seconds: float)
	## The listener could not run, or the wait was ended by something other than a
	## callback — it was never started, it was stopped, or the peer misbehaved.
	Failed(detail: String)
