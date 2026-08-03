namespace games.cafecito.foundrykit.auth

## The outcome of an authorized backend request.
##
## A non-2xx status is still [code]Success[/code] — the request completed and the caller
## can inspect [member AuthResponse.status_code]. [code]Failure[/code] means the request
## could not be made or answered at all.
enum_name ResponseResult:
	Success(response: AuthResponse)
	Failure(error: AuthError)
