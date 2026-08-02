# Auth Epic A — Types and Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the `auth/` subsystem's complete public type surface and backend contract — providers, configuration, errors, credentials, sessions, result unions, the `AuthApi` and `AuthBackend` traits, a Null backend, and the lazy `FoundryKit.auth` accessor — script-only, with no native binaries.

**Architecture:** Everything sits on the merged `core/` layer. `AuthBackend` is a **trait**, not an abstract base class, because cross-file bare-name `extends` does not resolve for namespaced classes. The Null backend makes every operation return a well-formed failure, so the subsystem is fully exercisable before any native exists — and remains the correct behaviour on unsupported platforms and partial installs forever.

**Tech Stack:** Foundry Script (`.fs`), FoundryLib `foundry.testlib`, Task, prek. No Swift, no Android, no new CI.

**Spec:** `docs/superpowers/specs/2026-08-02-foundrykit-design.md`
**Predecessor:** plan 1 (foundation) — epic #1, merged.

**Scope note:** This is epic A of seven in plan 2. It is the base of a thin vertical slice: epic B adds the Apple native producing a `Credential`, proving the native pipeline before the remaining epics depend on it.

---

## Porting rules — read before writing any code

The source is `AuthenticationKit` on branch `foundry-migration`. It predates several language changes. These are **not** optional adaptations:

| Source form | Required form | Why |
|---|---|---|
| `enum_name Provider { GOOGLE, APPLE, }` | Indented body, explicit value per case: `enum_name Provider:` / `\tGOOGLE = 0` | Brace/comma enum syntax is gone; plain enums require an explicit integer expression on every case |
| `abstract class_name X extends RefCounted` + subclasses in other files | `trait_name X` + `uses X` | Shared contracts are traits — composition over inheritance |
| `extends "res://path/to/base.fs"` | `trait_name` + `uses` | **FoundryKit does not use path-form `extends`.** The legacy code uses it because its contract was a class; ours are traits, so there is nothing to inherit across files |
| Two global types in one file | One global type per `.fs` file | The headless global-class scan registers one head type per file |
| `int` error codes + parallel string constants + a `Failure` class | One tagged union | The reason this redesign exists |

Also load-bearing, all recorded in `CLAUDE.md`:

- Indentation is **tabs**.
- `test_project/project.foundry` sets `untyped_declaration` and five `unsafe_*` warnings to **error**. `int(some_variant)` is rejected.
- **No `_` wildcard branch over a tagged union.** `scripts/test-foundry-script-strict` enforces this for `NativeOutcome`; the rule applies to every union you add here even though the checker does not yet recognise them (tracked in #32).
- Inline lambdas connected to signals do not reliably observe captured-variable mutation. Use a connected method in tests.
- Rest parameters reject typed arrays.

**Do not** copy `AuthenticationKitTypes.provider_to_string` / `code_to_string` / `error_code_from_string` / `normalize_native_code`. Those exist only to carry an `int` code plus loosely-associated context, which `AuthError` replaces. The JWT helpers from that file **do** port (Task 3).

---

## File Structure

| File | Responsibility |
|---|---|
| `addons/FoundryKit/auth/Provider.fs` | Which provider — int enum |
| `addons/FoundryKit/auth/ProviderConfig.fs` | How a provider is configured — tagged union |
| `addons/FoundryKit/auth/AuthError.fs` | Every way auth can fail — tagged union |
| `addons/FoundryKit/auth/Credential.fs` | Native SSO result before backend exchange — tagged union |
| `addons/FoundryKit/auth/AuthSession.fs` | Backend-owned session |
| `addons/FoundryKit/auth/AuthResponse.fs` | Authorized HTTP response |
| `addons/FoundryKit/auth/HttpMethod.fs` | Method for authorized requests |
| `addons/FoundryKit/auth/CredentialResult.fs` | `Success(credential)` / `Failure(error)` |
| `addons/FoundryKit/auth/SessionResult.fs` | `Success(session)` / `Failure(error)` |
| `addons/FoundryKit/auth/TokenResult.fs` | `Success(token)` / `Failure(error)` |
| `addons/FoundryKit/auth/ResponseResult.fs` | `Success(response)` / `Failure(error)` |
| `addons/FoundryKit/auth/CompletionResult.fs` | `Success` / `Failure(error)` |
| `addons/FoundryKit/auth/AuthApi.fs` | Public contract game code depends on |
| `addons/FoundryKit/auth/AuthSubsystem.fs` | Concrete `FoundryKit.auth` |
| `addons/FoundryKit/auth/internal/Jwt.fs` | JWT subject/expiry decoding |
| `addons/FoundryKit/auth/internal/AuthBackend.fs` | Platform backend contract — trait |
| `addons/FoundryKit/auth/internal/NullAuthBackend.fs` | Unsupported platform / absent binary |
| `addons/FoundryKit/auth/internal/AuthBackendFactory.fs` | Platform → backend, via `BackendFactory[AuthBackend]` |
| `addons/FoundryKit/FoundryKit.fs` | **Modify** — add lazy `auth` accessor |
| `scripts/test-foundry-script-strict` | **Modify** — assert the auth surface |
| `test_project/tests/auth-*.test.fs` | Suites, one per task |

Namespaces: `games.cafecito.foundrykit.auth` and `games.cafecito.foundrykit.auth.internal`.

---

### Task 1: Provider and ProviderConfig

**Goal:** Provider identity as an int enum, and provider configuration as a tagged union that makes an unconfigurable combination unrepresentable.

**Files:**
- Create: `addons/FoundryKit/auth/Provider.fs`, `addons/FoundryKit/auth/ProviderConfig.fs`
- Test: `test_project/tests/auth-provider.test.fs`

**Acceptance Criteria:**
- [ ] `Provider` has `GOOGLE`, `APPLE`, `EMAIL_PASSWORD`, each with an explicit integer value
- [ ] `ProviderConfig` is a tagged union whose `Google` case carries web, iOS **and** desktop client IDs
- [ ] `ProviderConfig.provider_of()` maps a config to its `Provider` without a wildcard branch
- [ ] Constructing `ProviderConfig.Google` without a client ID is impossible

**Verify:** `task test:foundrylib` → 5 tests in `AuthProviderTests` pass, all pre-existing suites green

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-provider.test.fs`:

```
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `Provider` and `ProviderConfig` are not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/Provider.fs`**

```
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
```

- [ ] **Step 4: Create `addons/FoundryKit/auth/ProviderConfig.fs`**

```
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
```

Note: the trailing `return` is unreachable once the match is exhaustive, but the analyser
requires every path to return. Keep it; do **not** replace the match with a wildcard.

- [ ] **Step 5: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 5 tests in `AuthProviderTests`.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/auth/Provider.fs addons/FoundryKit/auth/ProviderConfig.fs \
        test_project/tests/auth-provider.test.fs
git commit -m "feat(auth): add Provider and ProviderConfig"
```

---

### Task 2: AuthError

**Goal:** One tagged union covering every way authentication can fail, replacing an int enum, ten parallel string constants, a `Failure` class and three conversion functions.

**Files:**
- Create: `addons/FoundryKit/auth/AuthError.fs`
- Test: `test_project/tests/auth-error.test.fs`

**Acceptance Criteria:**
- [ ] All ten cases construct and match with payload binds
- [ ] A match over all ten compiles with no wildcard branch
- [ ] `AuthError.from_native(outcome, provider)` maps every `NativeOutcome` case
- [ ] `NativeOutcome.Abandoned` maps to `Cancelled` — a dismissed sheet is a cancellation
- [ ] `NativeOutcome.Succeeded` maps to `InvalidResponse`, since a success is not an error

**Verify:** `task test:foundrylib` → 8 tests in `AuthErrorTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-error.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

class_name AuthErrorTests
extends RefCounted
uses Test

## Renders an error without a wildcard branch, proving exhaustiveness holds.
func _describe(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(provider):
			return "unavailable:%d" % provider
		AuthError.Configuration(detail):
			return "configuration:%s" % detail
		AuthError.Storage(detail):
			return "storage:%s" % detail
		AuthError.RequestFailed(status, body):
			return "request_failed:%d:%s" % [status, body]
		AuthError.InvalidResponse(detail):
			return "invalid_response:%s" % detail
		AuthError.MissingField(field):
			return "missing_field:%s" % field
		AuthError.SessionExpired(expired_at):
			return "session_expired:%d" % expired_at
		AuthError.TimedOut(elapsed_seconds):
			return "timed_out:%s" % ("positive" if elapsed_seconds > 0.0 else "zero")
	return "unreachable"

func test_payload_less_cases_are_values() -> void:
	Expect.that(_describe(AuthError.Cancelled)).to_equal("cancelled")
	Expect.that(_describe(AuthError.NoCredential)).to_equal("no_credential")

func test_unavailable_carries_provider() -> void:
	Expect.that(_describe(AuthError.Unavailable(Provider.APPLE))).to_equal("unavailable:1")

func test_configuration_and_storage_carry_detail() -> void:
	Expect.that(_describe(AuthError.Configuration("missing web_client_id"))) \
		.to_equal("configuration:missing web_client_id")
	Expect.that(_describe(AuthError.Storage("keychain denied"))).to_equal("storage:keychain denied")

func test_request_failed_carries_status_and_body() -> void:
	Expect.that(_describe(AuthError.RequestFailed(503, "unavailable"))) \
		.to_equal("request_failed:503:unavailable")

func test_missing_field_and_invalid_response_carry_context() -> void:
	Expect.that(_describe(AuthError.MissingField("access_token"))).to_equal("missing_field:access_token")
	Expect.that(_describe(AuthError.InvalidResponse("not json"))).to_equal("invalid_response:not json")

func test_session_expired_carries_expiry() -> void:
	Expect.that(_describe(AuthError.SessionExpired(1750000000))).to_equal("session_expired:1750000000")

func test_from_native_maps_failure_timeout_and_unavailable() -> void:
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Failed(4, "boom"), Provider.GOOGLE))) \
		.to_equal("request_failed:4:boom")
	Expect.that(_describe(AuthError.from_native(NativeOutcome.TimedOut(2.5), Provider.GOOGLE))) \
		.to_equal("timed_out:positive")
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Unavailable("iOSGoogleSignIn"), Provider.APPLE))) \
		.to_equal("unavailable:1")

func test_from_native_maps_abandoned_to_cancelled_and_success_to_invalid() -> void:
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Abandoned, Provider.GOOGLE))) \
		.to_equal("cancelled")
	var payload: Dictionary[String, Variant] = {}
	Expect.that(_describe(AuthError.from_native(NativeOutcome.Succeeded(payload), Provider.GOOGLE))) \
		.to_equal("invalid_response:native reported success where a failure was expected")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `AuthError` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/AuthError.fs`**

```
namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.core

## Every way authentication can fail.
##
## Replaces the legacy `AuthenticationErrorCode` int enum, its ten parallel `ERROR_*`
## string constants, the `AuthenticationFailure` carrier class, and the three functions
## that converted between them. Each case carries exactly the context that case has.
##
## There is deliberately no `Abandoned` case: a native sheet dismissed without a reply is
## a cancellation from the player's point of view, so [method from_native] folds
## [code]NativeOutcome.Abandoned[/code] into [code]Cancelled[/code].
enum_name AuthError:
	## The player dismissed the flow, or a native sheet was abandoned.
	Cancelled
	## The provider completed without returning a credential.
	NoCredential
	## No backend supports this provider here — unsupported platform, or a removed binary.
	Unavailable(provider: Provider)
	## Required configuration is absent or malformed.
	Configuration(detail: String)
	## Secure storage refused a read or write.
	Storage(detail: String)
	## A backend HTTP request returned a non-success status.
	RequestFailed(status: int, body: String)
	## A response was received but could not be interpreted.
	InvalidResponse(detail: String)
	## A response was well-formed but omitted a required field.
	MissingField(field: String)
	## The active session is past its expiry and could not be refreshed.
	SessionExpired(expired_at: int)
	## The operation exceeded its watchdog window.
	TimedOut(elapsed_seconds: float)

	## Maps a core [NativeOutcome] onto an auth error.
	##
	## [param provider] supplies context the outcome itself does not carry.
	static func from_native(outcome: NativeOutcome, provider: Provider) -> AuthError:
		match outcome:
			NativeOutcome.Succeeded(_payload):
				return AuthError.InvalidResponse(
						"native reported success where a failure was expected")
			NativeOutcome.Failed(code, message):
				return AuthError.RequestFailed(code, message)
			NativeOutcome.TimedOut(elapsed_seconds):
				return AuthError.TimedOut(elapsed_seconds)
			NativeOutcome.Abandoned:
				return AuthError.Cancelled
			NativeOutcome.Unavailable(_missing_class):
				return AuthError.Unavailable(provider)
		return AuthError.Cancelled
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 8 tests in `AuthErrorTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/AuthError.fs test_project/tests/auth-error.test.fs
git commit -m "feat(auth): add AuthError union"
```

---

### Task 3: JWT helpers

**Goal:** Decode a JWT's subject and expiry, ported from `AuthenticationKitTypes`, as the only two pieces of that file worth keeping.

**Files:**
- Create: `addons/FoundryKit/auth/internal/Jwt.fs`
- Test: `test_project/tests/auth-jwt.test.fs`

**Acceptance Criteria:**
- [ ] `subject_from(token)` returns the `sub` claim of a valid token
- [ ] `expiry_from(token)` returns the `exp` claim as an int, accepting int or float encodings
- [ ] Both return an empty string / `0` for a malformed token rather than erroring
- [ ] Base64url padding of length 2 and 3 decodes; a remainder of 1 is rejected

**Verify:** `task test:foundrylib` → 8 tests in `JwtTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

The fixture is a real three-segment JWT with `{"sub":"user-123","exp":1750000000}` as its payload, base64url-encoded without padding.

Create `test_project/tests/auth-jwt.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth.internal

class_name JwtTests
extends RefCounted
uses Test

## header.payload.signature — payload decodes to {"sub":"user-123","exp":1750000000}
const _VALID: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func test_subject_is_decoded() -> void:
	Expect.that(Jwt.subject_from(_VALID)).to_equal("user-123")

func test_expiry_is_decoded() -> void:
	Expect.that(Jwt.expiry_from(_VALID)).to_equal(1750000000)

func test_empty_token_yields_empty_subject_and_zero_expiry() -> void:
	Expect.that(Jwt.subject_from("")).to_equal("")
	Expect.that(Jwt.expiry_from("")).to_equal(0)

func test_wrong_segment_count_is_rejected() -> void:
	Expect.that(Jwt.subject_from("only.two")).to_equal("")
	Expect.that(Jwt.expiry_from("a.b.c.d")).to_equal(0)

func test_non_json_payload_is_rejected() -> void:
	Expect.that(Jwt.subject_from("aaa.bm90LWpzb24.sig")).to_equal("")

func test_payload_without_claims_yields_defaults() -> void:
	# {} base64url-encoded is "e30"
	Expect.that(Jwt.subject_from("aaa.e30.sig")).to_equal("")
	Expect.that(Jwt.expiry_from("aaa.e30.sig")).to_equal(0)

func test_base64url_alphabet_is_translated() -> void:
	# Payload uses - and _ which must map to + and / before decoding.
	# {"sub":"a-b_c"} -> eyJzdWIiOiJhLWJfYyJ9
	Expect.that(Jwt.subject_from("aaa.eyJzdWIiOiJhLWJfYyJ9.sig")).to_equal("a-b_c")

func test_float_expiry_is_truncated_to_int() -> void:
	# {"exp":1750000000.9} -> eyJleHAiOjE3NTAwMDAwMDAuOX0
	Expect.that(Jwt.expiry_from("aaa.eyJleHAiOjE3NTAwMDAwMDAuOX0.sig")).to_equal(1750000000)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `Jwt` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/internal/Jwt.fs`**

```
namespace games.cafecito.foundrykit.auth.internal

## Reads claims out of a JWT without verifying its signature.
##
## Verification is the backend's job. FoundryKit decodes only to learn the subject and
## expiry it needs for session bookkeeping, and treats every malformed input as absent
## rather than as an error — a token this code cannot read is one the backend will reject
## anyway, and failing here would mask the clearer backend failure.
class_name Jwt extends RefCounted

## Returns the `sub` claim, or an empty string when it is absent or undecodable.
static func subject_from(token: String) -> String:
	var claims: Dictionary = _claims_of(token)
	if claims.is_empty():
		return ""
	return str(claims.get("sub", ""))

## Returns the `exp` claim as a Unix timestamp, or 0 when absent or undecodable.
##
## Accepts both int and float encodings; JSON parsers commonly widen large integers.
static func expiry_from(token: String) -> int:
	var claims: Dictionary = _claims_of(token)
	if claims.is_empty():
		return 0
	var expiry: Variant = claims.get("exp")
	if expiry is int:
		var as_int: int = expiry
		return as_int
	if expiry is float:
		var as_float: float = expiry
		return int(as_float)
	return 0

static func _claims_of(token: String) -> Dictionary:
	var segments: PackedStringArray = token.split(".")
	if segments.size() != 3:
		return {}
	var payload_json: String = _decode_segment(segments[1])
	if payload_json.is_empty():
		return {}
	var parser: JSON = JSON.new()
	if parser.parse(payload_json) != OK:
		return {}
	if not (parser.data is Dictionary):
		return {}
	var claims: Dictionary = parser.data
	return claims

static func _decode_segment(segment: String) -> String:
	var padded: String = segment.replace("-", "+").replace("_", "/")
	match padded.length() % 4:
		0:
			pass
		2:
			padded += "=="
		3:
			padded += "="
		_:
			# A remainder of 1 is not producible by valid base64.
			return ""
	return Marshalls.base64_to_utf8(padded)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 8 tests in `JwtTests`.

If a fixture's decoded value disagrees with the test, recompute the base64url of the
stated JSON rather than changing the expectation — the fixtures encode exactly the JSON
in their comments.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/internal/Jwt.fs test_project/tests/auth-jwt.test.fs
git commit -m "feat(auth): add JWT claim decoding"
```

---

### Task 4: Credential

**Goal:** The native-to-backend seam — what a platform backend produces before any backend exchange happens. This is the type epic B's Apple native will return.

**Files:**
- Create: `addons/FoundryKit/auth/Credential.fs`
- Test: `test_project/tests/auth-credential.test.fs`

**Acceptance Criteria:**
- [ ] Three cases, each carrying the fields that provider actually returns
- [ ] `Credential.provider_of()` maps every case with no wildcard
- [ ] `Credential.subject_of()` decodes the subject from whichever token the case carries
- [ ] `EmailPassword` yields an empty subject — it has no token to decode

**Verify:** `task test:foundrylib` → 6 tests in `CredentialTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-credential.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name CredentialTests
extends RefCounted
uses Test

const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func test_google_case_carries_token_email_name_and_audience() -> void:
	var credential: Credential = Credential.Google(_TOKEN, "a@b.c", "Ada", "web-id")
	var described: String = ""
	match credential:
		Credential.Google(id_token, email, display_name, audience):
			described = "%s|%s|%s|%d" % [email, display_name, audience, id_token.length()]
		Credential.Apple(_t, _c, _e, _n):
			described = "apple"
		Credential.EmailPassword(_e, _p):
			described = "email"
	Expect.that(described).to_equal("a@b.c|Ada|web-id|%d" % _TOKEN.length())

func test_apple_case_carries_identity_token_and_authorization_code() -> void:
	var credential: Credential = Credential.Apple(_TOKEN, "auth-code", "a@b.c", "Ada Lovelace")
	var described: String = ""
	match credential:
		Credential.Google(_t, _e, _n, _a):
			described = "google"
		Credential.Apple(_identity_token, authorization_code, email, full_name):
			described = "%s|%s|%s" % [authorization_code, email, full_name]
		Credential.EmailPassword(_e, _p):
			described = "email"
	Expect.that(described).to_equal("auth-code|a@b.c|Ada Lovelace")

func test_provider_of_maps_every_case() -> void:
	Expect.that(Credential.provider_of(Credential.Google(_TOKEN, "", "", ""))).to_equal(Provider.GOOGLE)
	Expect.that(Credential.provider_of(Credential.Apple(_TOKEN, "", "", ""))).to_equal(Provider.APPLE)
	Expect.that(Credential.provider_of(Credential.EmailPassword("a@b.c", "pw"))) \
		.to_equal(Provider.EMAIL_PASSWORD)

func test_subject_is_decoded_from_google_id_token() -> void:
	Expect.that(Credential.subject_of(Credential.Google(_TOKEN, "", "", ""))).to_equal("user-123")

func test_subject_is_decoded_from_apple_identity_token() -> void:
	Expect.that(Credential.subject_of(Credential.Apple(_TOKEN, "", "", ""))).to_equal("user-123")

func test_email_password_has_no_subject() -> void:
	Expect.that(Credential.subject_of(Credential.EmailPassword("a@b.c", "pw"))).to_equal("")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `Credential` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/Credential.fs`**

```
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 6 tests in `CredentialTests`.

Note this file imports `…auth.internal` for `Jwt`. That is the **only** direction allowed:
a public file may use an internal helper; an internal file may not import a public
subsystem it does not belong to. `scripts/test-import-boundaries` enforces the rule it
checks — that non-`internal` files do not import *other* subsystems' internals.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/Credential.fs test_project/tests/auth-credential.test.fs
git commit -m "feat(auth): add Credential union"
```

---

### Task 5: AuthSession

**Goal:** The backend-owned session, ported from `AuthenticationSession` with its serialisation intact — it is what secure storage persists in later epics.

**Files:**
- Create: `addons/FoundryKit/auth/AuthSession.fs`
- Test: `test_project/tests/auth-session.test.fs`

**Acceptance Criteria:**
- [ ] Carries access token, refresh token, provider, raw payload and extras
- [ ] `to_dictionary()` / `from_dictionary()` round-trip without loss
- [ ] `from_dictionary()` separates `extras` from the reserved keys
- [ ] `duplicate_session()` deep-copies — mutating the copy's `raw` does not affect the original
- [ ] `is_expired_at(now)` uses the access token's `exp` claim, and a token with no expiry never expires

**Verify:** `task test:foundrylib` → 7 tests in `AuthSessionTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-session.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name AuthSessionTests
extends RefCounted
uses Test

## exp = 1750000000
const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func _session() -> AuthSession:
	var raw: Dictionary[String, Variant] = {"scope": "openid"}
	var extras: Dictionary[String, Variant] = {"tenant": "acme"}
	return AuthSession.new("access", "refresh", Provider.APPLE, raw, extras)

func test_fields_are_stored() -> void:
	var session: AuthSession = _session()
	Expect.that(session.access_token).to_equal("access")
	Expect.that(session.refresh_token).to_equal("refresh")
	Expect.that(session.provider).to_equal(Provider.APPLE)

func test_to_dictionary_includes_reserved_keys_and_extras() -> void:
	var payload: Dictionary[String, Variant] = _session().to_dictionary()
	Expect.that(str(payload["access_token"])).to_equal("access")
	Expect.that(str(payload["refresh_token"])).to_equal("refresh")
	Expect.that(str(payload["tenant"])).to_equal("acme")

func test_round_trip_preserves_tokens_and_extras() -> void:
	var restored: AuthSession = AuthSession.from_dictionary(_session().to_dictionary(), Provider.APPLE)
	Expect.that(restored.access_token).to_equal("access")
	Expect.that(restored.refresh_token).to_equal("refresh")
	Expect.that(str(restored.extras["tenant"])).to_equal("acme")

func test_from_dictionary_excludes_reserved_keys_from_extras() -> void:
	var restored: AuthSession = AuthSession.from_dictionary(_session().to_dictionary(), Provider.APPLE)
	Expect.that(restored.extras.has("access_token")).to_be_false()
	Expect.that(restored.extras.has("refresh_token")).to_be_false()
	Expect.that(restored.extras.has("raw")).to_be_false()

func test_duplicate_is_a_deep_copy() -> void:
	var original: AuthSession = _session()
	var copy: AuthSession = original.duplicate_session()
	copy.raw["scope"] = "changed"
	Expect.that(str(original.raw["scope"])).to_equal("openid")

func test_expiry_is_read_from_the_access_token() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var session: AuthSession = AuthSession.new(_TOKEN, "refresh", Provider.GOOGLE, raw, extras)
	Expect.that(session.expires_at()).to_equal(1750000000)
	Expect.that(session.is_expired_at(1750000001)).to_be_true()
	Expect.that(session.is_expired_at(1749999999)).to_be_false()

func test_token_without_expiry_never_expires() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var session: AuthSession = AuthSession.new("opaque", "refresh", Provider.GOOGLE, raw, extras)
	Expect.that(session.expires_at()).to_equal(0)
	Expect.that(session.is_expired_at(9999999999)).to_be_false()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `AuthSession` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/AuthSession.fs`**

```
namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.auth.internal

## A backend-issued authentication session.
##
## The backend owns session semantics; FoundryKit holds the tokens and whatever else the
## backend returned. [member raw] is the backend's own payload, preserved verbatim so a
## consumer can read fields FoundryKit does not model. [member extras] is everything
## alongside the reserved keys when a session is restored from storage.
class_name AuthSession extends RefCounted

const _RESERVED_KEYS: Array[String] = ["access_token", "refresh_token", "provider", "raw"]

var access_token: String = ""
var refresh_token: String = ""
var provider: Provider = Provider.GOOGLE
var raw: Dictionary[String, Variant] = {}
var extras: Dictionary[String, Variant] = {}

func _init(
		p_access_token: String = "",
		p_refresh_token: String = "",
		p_provider: Provider = Provider.GOOGLE,
		p_raw: Dictionary[String, Variant] = {},
		p_extras: Dictionary[String, Variant] = {}) -> void:
	access_token = p_access_token
	refresh_token = p_refresh_token
	provider = p_provider
	raw = p_raw.duplicate(true)
	extras = p_extras.duplicate(true)

## Returns the access token's `exp` claim, or 0 when it carries none.
##
## An opaque (non-JWT) access token has no readable expiry. Treating it as
## never-expiring is deliberate: the backend is the authority, and a 401 will surface
## expiry through [code]AuthError.SessionExpired[/code] instead.
func expires_at() -> int:
	return Jwt.expiry_from(access_token)

## Returns whether the session is expired at [param now_unix_seconds].
func is_expired_at(now_unix_seconds: int) -> bool:
	var expiry: int = expires_at()
	if expiry == 0:
		return false
	return now_unix_seconds > expiry

## Returns an independent copy; mutating it cannot affect this session.
func duplicate_session() -> AuthSession:
	return AuthSession.new(access_token, refresh_token, provider, raw, extras)

## Flattens the session for secure storage.
func to_dictionary() -> Dictionary[String, Variant]:
	var payload: Dictionary[String, Variant] = extras.duplicate(true)
	payload["access_token"] = access_token
	payload["refresh_token"] = refresh_token
	payload["provider"] = provider
	payload["raw"] = raw.duplicate(true)
	return payload

## Rebuilds a session from storage. Reserved keys become fields; everything else is extras.
static func from_dictionary(
		payload: Dictionary[String, Variant],
		p_provider: Provider) -> AuthSession:
	var restored_extras: Dictionary[String, Variant] = payload.duplicate(true)
	for key: String in _RESERVED_KEYS:
		restored_extras.erase(key)
	var restored_raw: Dictionary[String, Variant] = {}
	var stored_raw: Variant = payload.get("raw")
	if stored_raw is Dictionary:
		var stored: Dictionary = stored_raw
		for key: Variant in stored.keys():
			restored_raw[str(key)] = stored[key]
	return AuthSession.new(
			str(payload.get("access_token", "")),
			str(payload.get("refresh_token", "")),
			p_provider,
			restored_raw,
			restored_extras)
```

Note the legacy `provider_name` field is intentionally dropped: it was a denormalised
copy of `provider` that existed only to feed string-keyed comparisons the union removes.

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 7 tests in `AuthSessionTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/AuthSession.fs test_project/tests/auth-session.test.fs
git commit -m "feat(auth): add AuthSession"
```

---

### Task 6: AuthResponse and HttpMethod

**Goal:** The result of an authorized backend request, plus the method enum its callers pass — ported from `AuthorizedResponse` without the engine-constant coupling.

**Files:**
- Create: `addons/FoundryKit/auth/AuthResponse.fs`, `addons/FoundryKit/auth/HttpMethod.fs`
- Test: `test_project/tests/auth-response.test.fs`

**Acceptance Criteria:**
- [ ] `HttpMethod` covers GET, POST, PUT, PATCH, DELETE with explicit values
- [ ] `is_ok()` is true only for a successful transport **and** a 2xx status
- [ ] `json()` returns a parsed dictionary for a JSON body, and `null` for empty or non-JSON
- [ ] `session_expired` is settable so the session layer can flag a 401

**Verify:** `task test:foundrylib` → 8 tests in `AuthResponseTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-response.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name AuthResponseTests
extends RefCounted
uses Test

func _response(status: int, text: String) -> AuthResponse:
	var response: AuthResponse = AuthResponse.new()
	response.transport_ok = true
	response.status_code = status
	response.body = text.to_utf8_buffer()
	return response

func test_http_method_values_are_explicit() -> void:
	Expect.that(HttpMethod.GET).to_equal(0)
	Expect.that(HttpMethod.POST).to_equal(1)
	Expect.that(HttpMethod.PUT).to_equal(2)
	Expect.that(HttpMethod.PATCH).to_equal(3)
	Expect.that(HttpMethod.DELETE).to_equal(4)

func test_two_hundred_is_ok() -> void:
	Expect.that(_response(200, "{}").is_ok()).to_be_true()

func test_two_ninety_nine_is_ok_and_three_hundred_is_not() -> void:
	Expect.that(_response(299, "").is_ok()).to_be_true()
	Expect.that(_response(300, "").is_ok()).to_be_false()

func test_transport_failure_is_never_ok() -> void:
	var response: AuthResponse = _response(200, "{}")
	response.transport_ok = false
	Expect.that(response.is_ok()).to_be_false()

func test_json_body_is_parsed() -> void:
	var parsed: Variant = _response(200, '{"token":"abc"}').json()
	Expect.that(parsed is Dictionary).to_be_true()
	var claims: Dictionary = parsed
	Expect.that(str(claims["token"])).to_equal("abc")

func test_empty_body_yields_null() -> void:
	Expect.that(_response(204, "").json() == null).to_be_true()

func test_non_json_body_yields_null() -> void:
	Expect.that(_response(200, "not json").json() == null).to_be_true()

func test_json_array_yields_null_because_a_dictionary_is_expected() -> void:
	Expect.that(_response(200, "[1,2]").json() == null).to_be_true()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `AuthResponse` and `HttpMethod` are not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/HttpMethod.fs`**

```
namespace games.cafecito.foundrykit.auth

## HTTP methods an authorized backend request may use.
##
## FoundryKit's own enum rather than the engine's HTTP constants, so the public API does
## not leak an engine type and callers get exhaustiveness. The session layer maps these
## onto the engine's constants at the transport boundary.
enum_name HttpMethod:
	GET = 0
	POST = 1
	PUT = 2
	PATCH = 3
	DELETE = 4
```

- [ ] **Step 4: Create `addons/FoundryKit/auth/AuthResponse.fs`**

```
namespace games.cafecito.foundrykit.auth

## The result of an authorized backend HTTP request.
##
## [member transport_ok] distinguishes "the request never reached the server" from "the
## server answered with an error status" — a distinction callers need, because only the
## latter carries a meaningful [member status_code].
class_name AuthResponse extends RefCounted

## Whether the request completed at the transport level, regardless of status.
var transport_ok: bool = false
var status_code: int = 0
var body: PackedByteArray = PackedByteArray()
## Set by the session layer when the backend reported the session is no longer valid.
var session_expired: bool = false

## Returns whether the request both completed and returned a 2xx status.
func is_ok() -> bool:
	if not transport_ok:
		return false
	return status_code >= 200 and status_code < 300

## Returns the body parsed as a JSON object, or null when it is empty or not an object.
func json() -> Variant:
	var text: String = body.get_string_from_utf8()
	if text.is_empty():
		return null
	var parser: JSON = JSON.new()
	if parser.parse(text) != OK:
		return null
	if not (parser.data is Dictionary):
		return null
	return parser.data
```

The legacy `transport_result: int` compared against `HTTPRequest.RESULT_SUCCESS` becomes a
bool: callers only ever tested equality with success, and the raw enum value leaked an
engine type through the public API.

- [ ] **Step 5: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 8 tests in `AuthResponseTests`.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/auth/AuthResponse.fs addons/FoundryKit/auth/HttpMethod.fs \
        test_project/tests/auth-response.test.fs
git commit -m "feat(auth): add AuthResponse and HttpMethod"
```

---

### Task 7: Result unions

**Goal:** The five two-case unions every async auth operation returns, named by payload shape so they are reused across operations rather than one per method.

**Files:**
- Create: `addons/FoundryKit/auth/CredentialResult.fs`, `SessionResult.fs`, `TokenResult.fs`, `ResponseResult.fs`, `CompletionResult.fs` (all under `addons/FoundryKit/auth/`)
- Test: `test_project/tests/auth-results.test.fs`

**Acceptance Criteria:**
- [ ] Each union has exactly `Success` and `Failure(error: AuthError)`
- [ ] `CompletionResult.Success` is payload-less; the other four carry their payload
- [ ] Each matches exhaustively with no wildcard
- [ ] Each is in its own file — five files, five global types

**Verify:** `task test:foundrylib` → 6 tests in `AuthResultsTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-results.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth

class_name AuthResultsTests
extends RefCounted
uses Test

const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

func test_credential_result_matches_both_cases() -> void:
	var ok: CredentialResult = CredentialResult.Success(Credential.Google(_TOKEN, "", "", ""))
	var described: String = ""
	match ok:
		CredentialResult.Success(credential):
			described = "ok:%d" % Credential.provider_of(credential)
		CredentialResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok:0")

func test_session_result_carries_session() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var result: SessionResult = SessionResult.Success(
			AuthSession.new("a", "r", Provider.APPLE, raw, extras))
	var described: String = ""
	match result:
		SessionResult.Success(session):
			described = session.access_token
		SessionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("a")

func test_token_result_carries_token() -> void:
	var result: TokenResult = TokenResult.Success("bearer-abc")
	var described: String = ""
	match result:
		TokenResult.Success(token):
			described = token
		TokenResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("bearer-abc")

func test_response_result_carries_response() -> void:
	var response: AuthResponse = AuthResponse.new()
	response.transport_ok = true
	response.status_code = 204
	var result: ResponseResult = ResponseResult.Success(response)
	var described: int = 0
	match result:
		ResponseResult.Success(value):
			described = value.status_code
		ResponseResult.Failure(_error):
			described = -1
	Expect.that(described).to_equal(204)

func test_completion_result_success_is_payload_less() -> void:
	var result: CompletionResult = CompletionResult.Success
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok")

func test_every_failure_carries_the_error() -> void:
	var result: CompletionResult = CompletionResult.Failure(AuthError.Cancelled)
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(error):
			match error:
				AuthError.Cancelled:
					described = "cancelled"
				AuthError.NoCredential, AuthError.Unavailable(_p), AuthError.Configuration(_d), \
				AuthError.Storage(_s), AuthError.RequestFailed(_st, _b), \
				AuthError.InvalidResponse(_i), AuthError.MissingField(_f), \
				AuthError.SessionExpired(_e), AuthError.TimedOut(_t):
					described = "other"
	Expect.that(described).to_equal("cancelled")
```

If the multi-pattern branch in the last test does not parse — a bind cannot be combined
with multiple patterns, and each of those payload binds counts — replace that inner match
with a call to a helper that returns `"cancelled"` for `AuthError.Cancelled` and
`"other"` for every other case listed individually. Do not use a wildcard.

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — the result unions are not defined.

- [ ] **Step 3: Create the five union files**

`addons/FoundryKit/auth/CredentialResult.fs`:

```
namespace games.cafecito.foundrykit.auth

## The outcome of obtaining a native credential.
##
## Named by payload shape, not by operation, so both interactive and silent sign-in
## return the same type. Tagged unions cannot be generic, so there is one concrete result
## union per payload rather than a shared `Result[T, E]`.
enum_name CredentialResult:
	Success(credential: Credential)
	Failure(error: AuthError)
```

`addons/FoundryKit/auth/SessionResult.fs`:

```
namespace games.cafecito.foundrykit.auth

## The outcome of any operation that yields a backend session.
enum_name SessionResult:
	Success(session: AuthSession)
	Failure(error: AuthError)
```

`addons/FoundryKit/auth/TokenResult.fs`:

```
namespace games.cafecito.foundrykit.auth

## The outcome of any operation that yields a single access token.
enum_name TokenResult:
	Success(token: String)
	Failure(error: AuthError)
```

`addons/FoundryKit/auth/ResponseResult.fs`:

```
namespace games.cafecito.foundrykit.auth

## The outcome of an authorized backend request.
##
## A non-2xx status is still [code]Success[/code] — the request completed and the caller
## can inspect [member AuthResponse.status_code]. [code]Failure[/code] means the request
## could not be made or answered at all.
enum_name ResponseResult:
	Success(response: AuthResponse)
	Failure(error: AuthError)
```

`addons/FoundryKit/auth/CompletionResult.fs`:

```
namespace games.cafecito.foundrykit.auth

## The outcome of an operation with nothing to return but success or failure.
enum_name CompletionResult:
	Success
	Failure(error: AuthError)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 6 tests in `AuthResultsTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/CredentialResult.fs addons/FoundryKit/auth/SessionResult.fs \
        addons/FoundryKit/auth/TokenResult.fs addons/FoundryKit/auth/ResponseResult.fs \
        addons/FoundryKit/auth/CompletionResult.fs test_project/tests/auth-results.test.fs
git commit -m "feat(auth): add result unions"
```

---

### Task 8: AuthBackend trait

**Goal:** The contract every platform backend implements — as a **trait**, because the legacy abstract base class cannot be extended across files.

**Files:**
- Create: `addons/FoundryKit/auth/internal/AuthBackend.fs`
- Test: covered by Task 9's Null backend suite; a trait with only requirements has no behaviour to test alone

**Acceptance Criteria:**
- [ ] Declared as `trait_name AuthBackend`, never `abstract class_name`
- [ ] Every requirement is `abstract func` or `abstract async func`
- [ ] Async operations return result unions; no success/failure signal pairs
- [ ] Covers configuration, availability, sign-in, sign-out and secure session storage

**Verify:** `task test:foundrylib` → suite stays green (this task adds no tests of its own)

**Steps:**

- [ ] **Step 1: Create `addons/FoundryKit/auth/internal/AuthBackend.fs`**

```
namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth

## The contract every platform authentication backend implements.
##
## A trait, not an abstract base class. Each backend lives in its own file, and FoundryKit
## composes contracts with `uses` rather than inheriting them — no backend `extends`
## anything but `RefCounted`.
##
## Every asynchronous operation returns a result union. The legacy contract emitted paired
## success/failure signals, which forced every caller to connect two handlers and made it
## impossible to tell which request a signal belonged to.
##
## A backend produces a [Credential]; exchanging one for an [AuthSession] is the session
## layer's job, not a backend's.
trait_name AuthBackend

## Applies configuration for one provider. Called before any sign-in attempt.
abstract func configure(config: ProviderConfig) -> void

## Returns whether this backend can serve a provider on this platform at all.
abstract func is_available(provider: Provider) -> bool

## Returns whether a provider has received the configuration it requires.
abstract func is_configured(provider: Provider) -> bool

## Starts an interactive sign-in and resolves with a credential.
abstract async func sign_in(provider: Provider) -> CredentialResult

## Attempts sign-in without UI, for a returning player.
abstract async func sign_in_silent(provider: Provider) -> CredentialResult

## Signs out of the native provider. Does not touch the backend session.
abstract async func sign_out(provider: Provider) -> CompletionResult

## Writes a session to platform secure storage.
abstract async func store_session(session: AuthSession) -> CompletionResult

## Reads the stored session back.
abstract async func restore_session() -> SessionResult

## Returns whether secure storage currently holds a session.
abstract func has_stored_session() -> bool

## Removes the stored session.
abstract async func clear_stored_session() -> CompletionResult

## Identifies the backend in logs and diagnostics.
abstract func backend_name() -> String
```

- [ ] **Step 2: Verify the suite still compiles and passes**

Run: `task test:foundrylib`
Expected: PASS — unchanged test count. A trait with no implementors compiles on its own.

- [ ] **Step 3: Commit**

```bash
git add addons/FoundryKit/auth/internal/AuthBackend.fs
git commit -m "feat(auth): add AuthBackend trait"
```

---

### Task 9: NullAuthBackend

**Goal:** The backend used on unsupported platforms and partial installs — every operation fails cleanly and identically, so absent natives never crash or hang.

**Files:**
- Create: `addons/FoundryKit/auth/internal/NullAuthBackend.fs`
- Test: `test_project/tests/auth-null-backend.test.fs`

**Acceptance Criteria:**
- [ ] Satisfies `AuthBackend` via `uses`
- [ ] `is_available` and `is_configured` are false for every provider
- [ ] Every async operation resolves — none hang — with `Unavailable` or `Storage`
- [ ] `sign_out` succeeds: signing out of nothing is not a failure
- [ ] `backend_name()` returns `"null"`

**Verify:** `task test:foundrylib` → 8 tests in `NullAuthBackendTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-null-backend.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core

class_name NullAuthBackendTests
extends RefCounted
uses Test

var _backend: NullAuthBackend

func before_each() -> void:
	var log: FoundryKitLog = FoundryKitLog.new("test")
	_backend = NullAuthBackend.new(log)

func _credential_failure_name(result: CredentialResult) -> String:
	match result:
		CredentialResult.Success(_credential):
			return "unexpected_success"
		CredentialResult.Failure(error):
			match error:
				AuthError.Unavailable(_provider):
					return "unavailable"
				AuthError.Cancelled:
					return "cancelled"
				AuthError.NoCredential:
					return "no_credential"
				AuthError.Configuration(_d):
					return "configuration"
				AuthError.Storage(_d):
					return "storage"
				AuthError.RequestFailed(_s, _b):
					return "request_failed"
				AuthError.InvalidResponse(_d):
					return "invalid_response"
				AuthError.MissingField(_f):
					return "missing_field"
				AuthError.SessionExpired(_e):
					return "session_expired"
				AuthError.TimedOut(_t):
					return "timed_out"
	return "unreachable"

func test_backend_name() -> void:
	Expect.that(_backend.backend_name()).to_equal("null")

func test_never_available() -> void:
	Expect.that(_backend.is_available(Provider.GOOGLE)).to_be_false()
	Expect.that(_backend.is_available(Provider.APPLE)).to_be_false()
	Expect.that(_backend.is_available(Provider.EMAIL_PASSWORD)).to_be_false()

func test_never_configured_even_after_configure() -> void:
	_backend.configure(ProviderConfig.Google("w", "i", "d"))
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_false()

func test_sign_in_resolves_unavailable() -> void:
	var result: CredentialResult = await _backend.sign_in(Provider.GOOGLE)
	Expect.that(_credential_failure_name(result)).to_equal("unavailable")

func test_sign_in_silent_resolves_unavailable() -> void:
	var result: CredentialResult = await _backend.sign_in_silent(Provider.APPLE)
	Expect.that(_credential_failure_name(result)).to_equal("unavailable")

func test_sign_out_succeeds() -> void:
	var result: CompletionResult = await _backend.sign_out(Provider.GOOGLE)
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok")

func test_storage_operations_fail_with_storage_error() -> void:
	var raw: Dictionary[String, Variant] = {}
	var extras: Dictionary[String, Variant] = {}
	var stored: CompletionResult = await _backend.store_session(
			AuthSession.new("a", "r", Provider.GOOGLE, raw, extras))
	var described: String = ""
	match stored:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(error):
			match error:
				AuthError.Storage(_detail):
					described = "storage"
				AuthError.Cancelled, AuthError.NoCredential:
					described = "other"
				AuthError.Unavailable(_p):
					described = "other"
				AuthError.Configuration(_d):
					described = "other"
				AuthError.RequestFailed(_s, _b):
					described = "other"
				AuthError.InvalidResponse(_i):
					described = "other"
				AuthError.MissingField(_f):
					described = "other"
				AuthError.SessionExpired(_e):
					described = "other"
				AuthError.TimedOut(_t):
					described = "other"
	Expect.that(described).to_equal("storage")
	Expect.that(_backend.has_stored_session()).to_be_false()

func test_clear_stored_session_succeeds() -> void:
	var result: CompletionResult = await _backend.clear_stored_session()
	var described: String = ""
	match result:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(_error):
			described = "fail"
	Expect.that(described).to_equal("ok")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `NullAuthBackend` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/internal/NullAuthBackend.fs`**

```
namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

## The backend used when no platform backend applies.
##
## Two situations resolve here and are deliberately indistinguishable: the platform has no
## native sign-in stack, and the consumer removed the auth binary from a partial install.
## Both mean "authentication is not available", and neither is an error condition — so
## every operation resolves promptly with a well-formed failure rather than hanging,
## erroring, or crashing.
##
## Signing out and clearing storage succeed: there is nothing to sign out of and nothing
## stored, which is the state the caller asked for.
class_name NullAuthBackend extends RefCounted
uses AuthBackend

const _STORAGE_DETAIL: String = "secure session storage is unavailable on this platform"

var _log: FoundryKitLog

func _init(log: FoundryKitLog) -> void:
	_log = log

func configure(_config: ProviderConfig) -> void:
	_log.debug("configure ignored: no authentication backend on this platform")

func is_available(_provider: Provider) -> bool:
	return false

func is_configured(_provider: Provider) -> bool:
	return false

async func sign_in(provider: Provider) -> CredentialResult:
	return _unavailable(provider)

async func sign_in_silent(provider: Provider) -> CredentialResult:
	return _unavailable(provider)

async func sign_out(_provider: Provider) -> CompletionResult:
	return CompletionResult.Success

async func store_session(_session: AuthSession) -> CompletionResult:
	return CompletionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

async func restore_session() -> SessionResult:
	return SessionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

func has_stored_session() -> bool:
	return false

async func clear_stored_session() -> CompletionResult:
	return CompletionResult.Success

func backend_name() -> String:
	return "null"

func _unavailable(provider: Provider) -> CredentialResult:
	_log.debug("sign-in unavailable for provider %d" % provider)
	return CredentialResult.Failure(AuthError.Unavailable(provider))
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 8 tests in `NullAuthBackendTests`.

If an `async func` with no `await` in its body is rejected, add `await` on a resolved
value or drop `async` from that function and adjust the trait — but report the change in
the PR, because it alters the contract's shape.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/internal/NullAuthBackend.fs \
        test_project/tests/auth-null-backend.test.fs
git commit -m "feat(auth): add null authentication backend"
```

---

### Task 10: AuthBackendFactory

**Goal:** Platform-to-backend selection built on the core `BackendFactory[TBackend]` trait, returning the Null backend everywhere until epic B adds the Apple backend.

**Files:**
- Create: `addons/FoundryKit/auth/internal/AuthBackendFactory.fs`
- Test: `test_project/tests/auth-backend-factory.test.fs`

**Acceptance Criteria:**
- [ ] Satisfies `BackendFactory[AuthBackend]` via `uses`
- [ ] `for_platform` returns `null` for every platform for now, with a comment naming the epic that changes it
- [ ] `resolve()` therefore yields the Null backend for every platform including `UNKNOWN`
- [ ] `resolve_current()` works on the host without special-casing

**Verify:** `task test:foundrylib` → 4 tests in `AuthBackendFactoryTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-backend-factory.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core

class_name AuthBackendFactoryTests
extends RefCounted
uses Test

var _factory: AuthBackendFactory

func before_each() -> void:
	_factory = AuthBackendFactory.new(FoundryKitLog.new("test"))

func test_every_platform_resolves_to_null_backend_for_now() -> void:
	for platform: PlatformKind in [PlatformKind.IOS, PlatformKind.MACOS, PlatformKind.ANDROID,
			PlatformKind.DESKTOP, PlatformKind.UNKNOWN]:
		var backend: AuthBackend = _factory.resolve(platform)
		Expect.that(backend.backend_name()).to_equal("null")

func test_resolved_backend_reports_unavailable() -> void:
	var backend: AuthBackend = _factory.resolve(PlatformKind.IOS)
	Expect.that(backend.is_available(Provider.GOOGLE)).to_be_false()

func test_resolve_current_returns_a_backend() -> void:
	var backend: AuthBackend = _factory.resolve_current()
	Expect.that(backend.backend_name()).to_equal("null")

func test_each_resolve_returns_a_usable_instance() -> void:
	var first: AuthBackend = _factory.resolve(PlatformKind.ANDROID)
	var second: AuthBackend = _factory.resolve(PlatformKind.ANDROID)
	Expect.that(first.backend_name()).to_equal("null")
	Expect.that(second.backend_name()).to_equal("null")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `AuthBackendFactory` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/internal/AuthBackendFactory.fs`**

```
namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.core

## Selects the authentication backend for a platform.
##
## Supplies only the two requirements [BackendFactory] declares; the shared fallback rule
## in `resolve()` does the rest. Returning null from [method for_platform] covers both an
## unsupported platform and a removed binary — deliberately the same path.
class_name AuthBackendFactory extends RefCounted
uses BackendFactory[AuthBackend]

var _log: FoundryKitLog

func _init(log: FoundryKitLog) -> void:
	_log = log

## Returns the platform backend, or null when none applies.
##
## Every platform returns null today because no native backend exists yet: epic B adds the
## Apple backend, epic F the Android one, and epic D the desktop OAuth backend. Until then
## the Null backend is the correct answer everywhere, not a placeholder.
func for_platform(_platform: PlatformKind) -> AuthBackend?:
	return null

func null_backend() -> AuthBackend:
	return NullAuthBackend.new(_log)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 4 tests in `AuthBackendFactoryTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/internal/AuthBackendFactory.fs \
        test_project/tests/auth-backend-factory.test.fs
git commit -m "feat(auth): add auth backend factory"
```

---

### Task 11: AuthApi trait

**Goal:** The public contract game code depends on, and that a test double can satisfy in place of the real subsystem.

**Files:**
- Create: `addons/FoundryKit/auth/AuthApi.fs`
- Test: covered by Task 12

**Acceptance Criteria:**
- [ ] Declared as `trait_name AuthApi`
- [ ] Exactly two signals — `session_expired` and `tokens_refreshed` — for genuinely unsolicited events
- [ ] Every request/response operation is an `abstract async func` returning a result union
- [ ] No operation returns `void` and reports through paired signals

**Verify:** `task test:foundrylib` → suite stays green

**Steps:**

- [ ] **Step 1: Create `addons/FoundryKit/auth/AuthApi.fs`**

```
namespace games.cafecito.foundrykit.auth

## The public authentication contract.
##
## Game code depends on this trait so a test double can stand in for the real subsystem.
##
## Ten of the legacy API's twelve signals became return values. The two that remain are
## genuinely unsolicited — they are not answers to any call the game made:
## [signal session_expired] fires when a session lapses in the background, and
## [signal tokens_refreshed] when a refresh rotates tokens the game may be holding.
trait_name AuthApi

## Emitted when the active session expires outside any call the game made.
signal session_expired(error: AuthError)

## Emitted when a background refresh rotates the active session's tokens.
signal tokens_refreshed(session: AuthSession)

## Applies configuration for one provider.
abstract func configure(config: ProviderConfig) -> void

## Returns whether a provider can be used on this platform.
abstract func is_available(provider: Provider) -> bool

## Returns whether a provider has the configuration it requires.
abstract func is_configured(provider: Provider) -> bool

## Runs an interactive sign-in and resolves with a backend session.
abstract async func sign_in(provider: Provider) -> SessionResult

## Attempts sign-in without UI for a returning player.
abstract async func sign_in_silent(provider: Provider) -> SessionResult

## Signs out of the native provider and clears the active session.
abstract async func sign_out(provider: Provider) -> CompletionResult

## Returns whether a session is currently active.
abstract func has_session() -> bool

## Returns the current access token, or an empty string when there is no session.
##
## Prefer [method valid_access_token] when the token is about to be used: this accessor
## does not refresh and may return an expired token.
abstract func access_token() -> String

## Returns an access token that is valid now, refreshing first if required.
abstract async func valid_access_token() -> TokenResult

## Forces a refresh of the active session.
abstract async func refresh_session() -> SessionResult

## Restores a session from platform secure storage.
abstract async func restore_session() -> SessionResult

## Clears the active session and removes it from secure storage.
abstract async func clear_session() -> CompletionResult

## Sends an authorized request to the configured backend.
abstract async func request(method: HttpMethod, path: String, body: Variant) -> ResponseResult
```

- [ ] **Step 2: Verify the suite still compiles**

Run: `task test:foundrylib`
Expected: PASS — unchanged test count.

- [ ] **Step 3: Commit**

```bash
git add addons/FoundryKit/auth/AuthApi.fs
git commit -m "feat(auth): add AuthApi public contract"
```

---

### Task 12: AuthSubsystem

**Goal:** The concrete implementation behind `FoundryKit.auth` — resolves a backend, delegates credential work to it, and returns correct failures for the session-layer operations epic C builds.

**Files:**
- Create: `addons/FoundryKit/auth/AuthSubsystem.fs`
- Test: `test_project/tests/auth-subsystem.test.fs`

**Acceptance Criteria:**
- [ ] Satisfies `AuthApi` via `uses`
- [ ] Resolves its backend once at construction through `AuthBackendFactory`
- [ ] `sign_in` maps a backend `CredentialResult.Failure` straight through to `SessionResult.Failure`
- [ ] `sign_in` on a **successful** credential returns `Failure(Configuration(...))` naming the missing backend — honest, not a stub, because no exchange endpoint is configured yet
- [ ] Session-layer operations return `Failure(Configuration(...))` with the same reason
- [ ] `has_session()` is false and `access_token()` is empty with no session
- [ ] A `RequestGuard` is registered with the facade so lifecycle notifications reach it

**Verify:** `task test:foundrylib` → 8 tests in `AuthSubsystemTests` pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-subsystem.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

class_name AuthSubsystemTests
extends RefCounted
uses Test

var _auth: AuthSubsystem

func before_each() -> void:
	_auth = AuthSubsystem.new(FoundryKitLog.new("test").child("auth"))

func _session_failure_name(result: SessionResult) -> String:
	match result:
		SessionResult.Success(_session):
			return "unexpected_success"
		SessionResult.Failure(error):
			return _error_name(error)
	return "unreachable"

func _error_name(error: AuthError) -> String:
	match error:
		AuthError.Cancelled:
			return "cancelled"
		AuthError.NoCredential:
			return "no_credential"
		AuthError.Unavailable(_p):
			return "unavailable"
		AuthError.Configuration(_d):
			return "configuration"
		AuthError.Storage(_d):
			return "storage"
		AuthError.RequestFailed(_s, _b):
			return "request_failed"
		AuthError.InvalidResponse(_d):
			return "invalid_response"
		AuthError.MissingField(_f):
			return "missing_field"
		AuthError.SessionExpired(_e):
			return "session_expired"
		AuthError.TimedOut(_t):
			return "timed_out"
	return "unreachable"

func test_no_session_initially() -> void:
	Expect.that(_auth.has_session()).to_be_false()
	Expect.that(_auth.access_token()).to_equal("")

func test_availability_follows_the_backend() -> void:
	Expect.that(_auth.is_available(Provider.GOOGLE)).to_be_false()
	Expect.that(_auth.is_configured(Provider.GOOGLE)).to_be_false()

func test_configure_is_accepted_without_error() -> void:
	_auth.configure(ProviderConfig.Google("w", "i", "d"))
	Expect.that(_auth.is_configured(Provider.GOOGLE)).to_be_false()

func test_sign_in_surfaces_the_backend_unavailability() -> void:
	Expect.that(_session_failure_name(await _auth.sign_in(Provider.GOOGLE))).to_equal("unavailable")

func test_sign_in_silent_surfaces_the_backend_unavailability() -> void:
	Expect.that(_session_failure_name(await _auth.sign_in_silent(Provider.APPLE))) \
		.to_equal("unavailable")

func test_refresh_reports_missing_backend_and_restore_reports_storage() -> void:
	# refresh_session has no endpoint to call; restore_session delegates to the backend,
	# which has no secure storage. Two different absences, two different errors.
	Expect.that(_session_failure_name(await _auth.refresh_session())).to_equal("configuration")
	Expect.that(_session_failure_name(await _auth.restore_session())).to_equal("storage")

func test_valid_access_token_reports_missing_backend_configuration() -> void:
	var result: TokenResult = await _auth.valid_access_token()
	var described: String = ""
	match result:
		TokenResult.Success(_token):
			described = "unexpected_success"
		TokenResult.Failure(error):
			described = _error_name(error)
	Expect.that(described).to_equal("configuration")

func test_sign_out_and_clear_session_succeed_with_no_session() -> void:
	var signed_out: CompletionResult = await _auth.sign_out(Provider.GOOGLE)
	var cleared: CompletionResult = await _auth.clear_session()
	var described: String = ""
	match signed_out:
		CompletionResult.Success:
			match cleared:
				CompletionResult.Success:
					described = "both_ok"
				CompletionResult.Failure(_e):
					described = "clear_failed"
		CompletionResult.Failure(_error):
			described = "sign_out_failed"
	Expect.that(described).to_equal("both_ok")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `AuthSubsystem` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/auth/AuthSubsystem.fs`**

```
@warning_ignore("unused_private_class_variable")
namespace games.cafecito.foundrykit.auth

import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core

## The concrete authentication subsystem behind [code]FoundryKit.auth[/code].
##
## Resolves its platform backend once at construction. A backend produces a [Credential];
## exchanging that credential for an [AuthSession] needs a configured backend endpoint,
## which epic C builds — until then the exchange step reports
## [code]AuthError.Configuration[/code] naming what is missing.
##
## That is not a stub: with no backend URL configured there is genuinely nothing to
## exchange against, and reporting it is the correct behaviour.
class_name AuthSubsystem extends RefCounted
uses AuthApi

const _NO_BACKEND: String = "no authentication backend endpoint is configured"

var _log: FoundryKitLog
var _backend: AuthBackend
var _guard: RequestGuard
var _session: AuthSession? = null

func _init(log: FoundryKitLog) -> void:
	_log = log
	_guard = RequestGuard.new(log)
	var factory: AuthBackendFactory = AuthBackendFactory.new(log)
	_backend = factory.resolve_current()
	_log.debug("resolved auth backend '%s'" % _backend.backend_name())

## Returns the guard so the facade can route lifecycle notifications to it.
func request_guard() -> RequestGuard:
	return _guard

func configure(config: ProviderConfig) -> void:
	_backend.configure(config)

func is_available(provider: Provider) -> bool:
	return _backend.is_available(provider)

func is_configured(provider: Provider) -> bool:
	return _backend.is_configured(provider)

async func sign_in(provider: Provider) -> SessionResult:
	return await _exchange(await _backend.sign_in(provider))

async func sign_in_silent(provider: Provider) -> SessionResult:
	return await _exchange(await _backend.sign_in_silent(provider))

async func sign_out(provider: Provider) -> CompletionResult:
	_session = null
	return await _backend.sign_out(provider)

func has_session() -> bool:
	return _session != null

func access_token() -> String:
	var session: AuthSession? = _session
	if session == null:
		return ""
	var active: AuthSession = session
	return active.access_token

async func valid_access_token() -> TokenResult:
	return TokenResult.Failure(AuthError.Configuration(_NO_BACKEND))

async func refresh_session() -> SessionResult:
	return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))

async func restore_session() -> SessionResult:
	return await _backend.restore_session()

async func clear_session() -> CompletionResult:
	_session = null
	return await _backend.clear_stored_session()

async func request(_method: HttpMethod, _path: String, _body: Variant) -> ResponseResult:
	return ResponseResult.Failure(AuthError.Configuration(_NO_BACKEND))

## Exchanges a credential for a backend session.
##
## Epic C replaces the failure branch with a real exchange; the credential-failure branch
## already behaves correctly and does not change.
async func _exchange(credential_result: CredentialResult) -> SessionResult:
	match credential_result:
		CredentialResult.Failure(error):
			return SessionResult.Failure(error)
		CredentialResult.Success(credential):
			_log.debug("credential obtained for provider %d, awaiting backend exchange"
					% Credential.provider_of(credential))
			return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))
	return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))
```

If the leading `@warning_ignore` on the file is rejected or unnecessary, remove it — it is
there only in case `_session` reads as unused before epic C writes to it.

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 8 tests in `AuthSubsystemTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/AuthSubsystem.fs test_project/tests/auth-subsystem.test.fs
git commit -m "feat(auth): add AuthSubsystem"
```

---

### Task 13: Facade wiring and strict checks

**Goal:** Expose the subsystem as `FoundryKit.auth`, constructed lazily, with its guard registered for lifecycle notifications — and extend the structural checks to cover the auth surface.

**Files:**
- Modify: `addons/FoundryKit/FoundryKit.fs`
- Modify: `scripts/test-foundry-script-strict`
- Test: `test_project/tests/auth-facade.test.fs`

**Acceptance Criteria:**
- [ ] `FoundryKit.auth` returns an `AuthSubsystem`
- [ ] The same instance is returned on repeated access — construction happens once
- [ ] The subsystem is **not** constructed until first access
- [ ] Its `RequestGuard` is registered with the facade on construction
- [ ] `scripts/test-foundry-script-strict` asserts every auth file exists in the right namespace, and that `AuthBackend`/`AuthApi` are traits
- [ ] `task test:scripts` exits 0

**Verify:** `task test:foundrylib` → 4 tests in `AuthFacadeTests` pass; `task test:scripts` → exit 0

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/auth-facade.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit
import games.cafecito.foundrykit.auth

class_name AuthFacadeTests
extends RefCounted
uses Test

## FoundryKit is an autoload, so its bare name resolves to the singleton instance type.
## Construct through the preloaded script instead — see CLAUDE.md.
const FoundryKitScript = preload("res://addons/FoundryKit/FoundryKit.fs")

var _kit: FoundryKit

func before_each() -> void:
	_kit = FoundryKitScript.new()

func after_each() -> void:
	_kit.free()

func test_auth_is_not_constructed_until_first_access() -> void:
	Expect.that(_kit.has_auth()).to_be_false()

func test_auth_returns_a_subsystem() -> void:
	var auth: AuthSubsystem = _kit.auth
	Expect.that(auth.has_session()).to_be_false()
	Expect.that(_kit.has_auth()).to_be_true()

func test_auth_is_constructed_once() -> void:
	var first: AuthSubsystem = _kit.auth
	var second: AuthSubsystem = _kit.auth
	Expect.that(first == second).to_be_true()

func test_auth_guard_is_registered_with_the_facade() -> void:
	Expect.that(_kit.guard_count()).to_equal(0)
	var auth: AuthSubsystem = _kit.auth
	Expect.that(auth.has_session()).to_be_false()
	Expect.that(_kit.guard_count()).to_equal(1)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `FoundryKit` has no `auth` property and no `has_auth()`.

- [ ] **Step 3: Modify `addons/FoundryKit/FoundryKit.fs`**

Add the import at the top, alongside the existing `core` import:

```
import games.cafecito.foundrykit.auth
```

Add the backing field beside the other `var` declarations:

```
var _auth: AuthSubsystem? = null
```

Add the lazy accessor and its probe after `platform()`:

```
## The authentication subsystem.
##
## Constructed on first access and never before: a game that only reads keyboard height
## must not instantiate the auth backend or touch secure storage. Its request guard is
## registered here so application lifecycle notifications reach it.
var auth: AuthSubsystem:
	get:
		var existing: AuthSubsystem? = _auth
		if existing == null:
			var created: AuthSubsystem = AuthSubsystem.new(_log.child("auth"))
			register_guard(created.request_guard())
			_auth = created
			return created
		return existing

## Returns whether the auth subsystem has been constructed yet. Used by tests to prove
## laziness; game code should not need it.
func has_auth() -> bool:
	return _auth != null
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 4 tests in `AuthFacadeTests`, all other suites green.

- [ ] **Step 5: Extend `scripts/test-foundry-script-strict`**

Add these assertions after the existing core-file loop. Keep the file's existing style and
its `assert_contains` helper.

```bash
# --- auth subsystem ---------------------------------------------------------
auth="$addon/auth"

for auth_file in Provider ProviderConfig AuthError Credential AuthSession AuthResponse \
                 HttpMethod CredentialResult SessionResult TokenResult ResponseResult \
                 CompletionResult AuthApi AuthSubsystem
do
    path="$auth/${auth_file}.fs"
    [[ -f "$path" ]] || fail "auth/${auth_file}.fs is missing"
    assert_contains "$path" '^namespace games\.cafecito\.foundrykit\.auth$' \
        "auth/${auth_file}.fs must use the auth namespace"
done

for internal_file in Jwt AuthBackend NullAuthBackend AuthBackendFactory
do
    path="$auth/internal/${internal_file}.fs"
    [[ -f "$path" ]] || fail "auth/internal/${internal_file}.fs is missing"
    assert_contains "$path" '^namespace games\.cafecito\.foundrykit\.auth\.internal$' \
        "auth/internal/${internal_file}.fs must use the auth internal namespace"
done

# Contracts must be traits: a namespaced class_name cannot be extended across files.
assert_contains "$auth/internal/AuthBackend.fs" '^trait_name AuthBackend$' \
    "AuthBackend must be a trait, not an abstract class"
assert_contains "$auth/AuthApi.fs" '^trait_name AuthApi$' \
    "AuthApi must be a trait"
assert_contains "$auth/AuthSubsystem.fs" '^uses AuthApi$' \
    "AuthSubsystem must satisfy AuthApi"
assert_contains "$auth/internal/NullAuthBackend.fs" '^uses AuthBackend$' \
    "NullAuthBackend must satisfy AuthBackend"
```

- [ ] **Step 6: Run the script checks**

Run: `task test:scripts`
Expected: both scripts print `PASS`, exit 0.

Then prove the new assertions are load-bearing:

```bash
mv addons/FoundryKit/auth/AuthError.fs /tmp/AuthError.fs
scripts/test-foundry-script-strict; echo "exit=$?"
mv /tmp/AuthError.fs addons/FoundryKit/auth/AuthError.fs
scripts/test-foundry-script-strict; echo "exit=$?"
```

Expected: `FAIL: auth/AuthError.fs is missing` and `exit=1`, then `PASS` and `exit=0`.
Record that transcript in the PR body.

- [ ] **Step 7: Commit**

```bash
git add addons/FoundryKit/FoundryKit.fs scripts/test-foundry-script-strict \
        test_project/tests/auth-facade.test.fs
git commit -m "feat(auth): expose FoundryKit.auth and assert the auth surface"
```

---

## Definition of Done

- [ ] `task test:foundrylib` passes with all new suites: `AuthProvider`, `AuthError`, `Jwt`, `Credential`, `AuthSession`, `AuthResponse`, `AuthResults`, `NullAuthBackend`, `AuthBackendFactory`, `AuthSubsystem`, `AuthFacade`
- [ ] `task test:scripts` passes, including the seeded-violation proof
- [ ] `task lint` passes
- [ ] `addons/FoundryKit/core/` still contains no reference to `auth`
- [ ] No `_` wildcard branch over any auth union anywhere
- [ ] No Swift, Android, or CI changes — those are epic B

## What this epic deliberately does not do

- **No backend HTTP exchange.** `sign_in` obtains a credential and then reports missing backend configuration. Epic C builds the exchange.
- **No native backends.** `AuthBackendFactory.for_platform` returns null for every platform. Epic B adds Apple.
- **No secure storage.** The Null backend reports storage unavailable. Epics B and F add Keychain and Android storage.
- **No token refresh or session authority.** Epic C.

## Follow-on epics

| Epic | Depends on | Scope |
|---|---|---|
| B — Apple native (Google only) | A | Swift multi-target package, correlation token, xcframework, macOS CI, `.foundryextension`, Apple backend returning a `Credential` |
| C — Backend session layer | A | Credential exchange, token refresh, session authority, authorized requests |
| D — Desktop backend | A, C | OAuth PKCE loopback, browser launch, macOS composite routing |
| E — Apple: Sign in with Apple + Keychain | B | Second provider, secure session storage |
| F — Android native | A | Credentials API, Apple web flow, AAR, `android-dependencies` |
| G — Integration and release | B–F | Partial-install test, provenance, migration docs, release workflow |
