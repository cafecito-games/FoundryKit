namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name AuthProviderTests
extends RefCounted
uses Test

func test_provider_values_are_explicit_and_distinct() -> void:
	Expect.that(Provider.GOOGLE).to_equal(0)
	Expect.that(Provider.APPLE).to_equal(1)
	Expect.that(Provider.EMAIL_PASSWORD).to_equal(2)

func test_google_config_carries_all_three_client_ids() -> void:
	var config: ProviderConfig = ProviderConfig.Google("web-id", "ios-id", "desktop-id")
	var described: String = ""
	match config:
		ProviderConfig.Google(web_client_id, ios_client_id, desktop_client_id):
			described = "%s|%s|%s" % [web_client_id, ios_client_id, desktop_client_id]
		ProviderConfig.Apple(_service_id, _redirect_uri):
			described = "apple"
		ProviderConfig.EmailPassword:
			described = "email"
	Expect.that(described).to_equal("web-id|ios-id|desktop-id")

func test_apple_config_carries_service_id_and_redirect() -> void:
	var config: ProviderConfig = ProviderConfig.Apple("com.example.service", "https://example.com/cb")
	var described: String = ""
	match config:
		ProviderConfig.Google(_w, _i, _d):
			described = "google"
		ProviderConfig.Apple(service_id, redirect_uri):
			described = "%s|%s" % [service_id, redirect_uri]
		ProviderConfig.EmailPassword:
			described = "email"
	Expect.that(described).to_equal("com.example.service|https://example.com/cb")

func test_provider_of_maps_each_config_case() -> void:
	Expect.that(ProviderConfig.provider_of(ProviderConfig.Google("w", "i", "d"))).to_equal(Provider.GOOGLE)
	Expect.that(ProviderConfig.provider_of(ProviderConfig.Apple("s", "r"))).to_equal(Provider.APPLE)
	Expect.that(ProviderConfig.provider_of(ProviderConfig.EmailPassword)).to_equal(Provider.EMAIL_PASSWORD)

func test_email_password_config_is_a_payload_less_value() -> void:
	var config: ProviderConfig = ProviderConfig.EmailPassword
	Expect.that(ProviderConfig.provider_of(config)).to_equal(Provider.EMAIL_PASSWORD)
