namespace games.cafecito.foundrykit.auth

## Configuration for one provider.
##
## A tagged union rather than an options dictionary so a provider cannot be configured
## with the wrong keys, or with none at all: the compiler requires every field a case
## declares. Replaces the legacy `configure(provider, Dictionary)` surface, which failed
## at runtime with a pushed error when a required key was absent.
enum_name ProviderConfig:
	## Google needs a distinct client ID per platform family. The web client ID is the
	## audience the backend validates; the iOS ID is used by the native SDK; the desktop
	## ID is used by the OAuth loopback flow.
	Google(web_client_id: String, ios_client_id: String, desktop_client_id: String)
	## Apple needs a Services ID and redirect URI for the non-native web flow.
	Apple(service_id: String, redirect_uri: String)
	## Email and password authentication is configured entirely by the backend.
	EmailPassword

	## Returns which provider a configuration configures.
	static func provider_of(config: ProviderConfig) -> Provider:
		match config:
			ProviderConfig.Google(_web_client_id, _ios_client_id, _desktop_client_id):
				return Provider.GOOGLE
			ProviderConfig.Apple(_service_id, _redirect_uri):
				return Provider.APPLE
			ProviderConfig.EmailPassword:
				return Provider.EMAIL_PASSWORD
		return Provider.GOOGLE
