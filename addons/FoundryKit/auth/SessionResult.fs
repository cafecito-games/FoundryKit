namespace games.cafecito.foundrykit.auth

## The outcome of any operation that yields a backend session.
enum_name SessionResult:
	Success(session: AuthSession)
	Failure(error: AuthError)
