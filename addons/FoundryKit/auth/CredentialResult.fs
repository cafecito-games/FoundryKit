namespace games.cafecito.foundrykit.auth

## The outcome of obtaining a native credential.
##
## Named by payload shape, not by operation, so both interactive and silent sign-in
## return the same type. Tagged unions cannot be generic, so there is one concrete result
## union per payload rather than a shared `Result[T, E]`.
enum_name CredentialResult:
	Success(credential: Credential)
	Failure(error: AuthError)
