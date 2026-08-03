namespace games.cafecito.foundrykit.core

## How one HTTP attempt ended.
##
## Deliberately free of authorization vocabulary: a 401 is [code]Answered(401, ...)[/code]
## and nothing more. Only the auth subsystem knows what a 401 means. This keeps `core/`
## usable by purchase and mobile, which have their own interpretations of the same status.
##
## Concrete rather than generic because tagged unions take no type parameters — the same
## constraint that produced [NativeOutcome]. Each subsystem maps this into its own result.
enum_name HttpOutcome:
	## The server answered. [param status_code] may be any status, including 4xx and 5xx.
	Answered(status_code: int, body: PackedByteArray)
	## The request never reached the server — DNS, TLS, connection refused.
	TransportFailed(detail: String)
	## The request exceeded its watchdog window.
	TimedOut(elapsed_seconds: float)
