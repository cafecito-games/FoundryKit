namespace games.cafecito.foundrykit.auth

## The outcome of an operation with nothing to return but success or failure.
enum_name CompletionResult:
	Success
	Failure(error: AuthError)
