namespace games.cafecito.foundrykit.auth

## Authentication providers FoundryKit can sign in with.
##
## This is the identity of a provider. How a provider is configured is [ProviderConfig].
enum_name Provider:
	## Native Google Sign-In, or the desktop OAuth loopback flow.
	GOOGLE = 0
	## Native Sign in with Apple where available, web flow elsewhere.
	APPLE = 1
	## Backend email and password authentication.
	EMAIL_PASSWORD = 2
