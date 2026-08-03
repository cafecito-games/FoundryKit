namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.auth.internal

## What a platform backend produces before any backend exchange.
##
## This is the seam between the native layer and the session layer: a native backend's
## whole job is to return one of these, and the backend session layer's whole job is to
## exchange one for an [AuthSession].
##
## A union rather than one class with nullable fields, because the providers genuinely
## differ: Google returns an ID token, Apple returns an identity token *and* a
## single-use authorization code, and email/password has no token at all. The legacy
## `AuthenticationCredential` carried every field for every provider and left callers to
## know which were meaningful.
enum_name Credential:
	## [param audience] is the client ID the token was issued to; the backend validates it.
	Google(id_token: String, email: String, display_name: String, audience: String)
	## [param authorization_code] is single-use and only present on first authorization.
	Apple(identity_token: String, authorization_code: String, email: String, full_name: String)
	## Carries the raw pair for backend exchange; never stored.
	EmailPassword(email: String, password: String)

	## Returns which provider issued a credential.
	static func provider_of(credential: Credential) -> Provider:
		match credential:
			Credential.Google(_id_token, _email, _display_name, _audience):
				return Provider.GOOGLE
			Credential.Apple(_identity_token, _authorization_code, _email, _full_name):
				return Provider.APPLE
			Credential.EmailPassword(_email, _password):
				return Provider.EMAIL_PASSWORD
		return Provider.GOOGLE

	## Returns the JWT subject of whichever token the credential carries.
	##
	## Email/password credentials have no token, so they have no subject until the backend
	## issues a session.
	static func subject_of(credential: Credential) -> String:
		match credential:
			Credential.Google(id_token, _email, _display_name, _audience):
				return Jwt.subject_from(id_token)
			Credential.Apple(identity_token, _authorization_code, _email, _full_name):
				return Jwt.subject_from(identity_token)
			Credential.EmailPassword(_email, _password):
				return ""
		return ""
