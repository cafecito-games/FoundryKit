namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name BackendConfigTests
extends RefCounted
uses Test

func test_defaults_are_sensible_paths() -> void:
	var config: BackendConfig = BackendConfig.new("https://api.test")
	Expect.that(config.exchange_path).to_equal("/v1/auth/exchange")
	Expect.that(config.refresh_path).to_equal("/v1/auth/refresh")
	Expect.that(config.sign_out_path).to_equal("/v1/auth/sign-out")

func test_base_without_trailing_slash_joins_path_with_leading_slash() -> void:
	var config: BackendConfig = BackendConfig.new("https://api.test")
	Expect.that(config.url_for("/v1/session")).to_equal("https://api.test/v1/session")

func test_base_with_trailing_slash_joins_path_without_leading_slash() -> void:
	var config: BackendConfig = BackendConfig.new("https://api.test/")
	Expect.that(config.url_for("v1/session")).to_equal("https://api.test/v1/session")

func test_both_with_slashes_does_not_double_the_slash() -> void:
	var config: BackendConfig = BackendConfig.new("https://api.test/")
	Expect.that(config.url_for("/v1/session")).to_equal("https://api.test/v1/session")

func test_neither_with_slashes_does_not_concatenate_into_one_word() -> void:
	var config: BackendConfig = BackendConfig.new("https://api.test")
	Expect.that(config.url_for("v1/session")).to_equal("https://api.test/v1/session")

func test_empty_base_url_is_not_configured() -> void:
	var config: BackendConfig = BackendConfig.new("")
	Expect.that(config.is_configured()).to_be_false()

func test_non_empty_base_url_is_configured() -> void:
	var config: BackendConfig = BackendConfig.new("https://api.test")
	Expect.that(config.is_configured()).to_be_true()

func test_custom_paths_are_honored() -> void:
	var config: BackendConfig = BackendConfig.new(
			"https://api.test", "/custom/exchange", "/custom/refresh", "/custom/sign-out")
	Expect.that(config.url_for(config.exchange_path)).to_equal("https://api.test/custom/exchange")
	Expect.that(config.url_for(config.refresh_path)).to_equal("https://api.test/custom/refresh")
	Expect.that(config.url_for(config.sign_out_path)).to_equal("https://api.test/custom/sign-out")
