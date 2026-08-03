namespace games.cafecito.foundrykit.auth

## The outcome of any operation that yields a single access token.
enum_name TokenResult:
	Success(token: String)
	Failure(error: AuthError)
