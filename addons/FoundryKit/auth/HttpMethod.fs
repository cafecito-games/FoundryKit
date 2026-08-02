namespace games.cafecito.foundrykit.auth

## HTTP methods an authorized backend request may use.
##
## FoundryKit's own enum rather than the engine's HTTP constants, so the public API does
## not leak an engine type and callers get exhaustiveness. The session layer maps these
## onto the engine's constants at the transport boundary.
enum_name HttpMethod:
	GET = 0
	POST = 1
	PUT = 2
	PATCH = 3
	DELETE = 4
