# Auth Epic C — Backend Session Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn a `Credential` into a real `AuthSession` and keep it valid — credential exchange, token refresh, session authority, and authorized backend requests. This is the half of authentication that epic A declared and epic B fed.

**Architecture:** A generic HTTP client in `core/` that knows nothing about auth, and an auth-flavoured client in `auth/internal/` that adds bearer injection and maps transport outcomes onto `AuthError`. `AuthSubsystem` becomes the session authority: it owns the active session, refreshes single-flight, and emits the two unsolicited signals `AuthApi` already declares.

**Tech Stack:** Foundry Script · `HTTPRequest` · FoundryLib `foundry.testlib`.

**Spec:** `docs/superpowers/specs/2026-08-02-foundrykit-design.md`
**Predecessors:** epic A (#34) and epic B (#68) — both merged.

---

## What this epic replaces

`AuthSubsystem` today is honest but inert. Four methods return `AuthError.Configuration("no backend configured")`, and `_exchange` throws away a perfectly good credential:

```
async func refresh_session() -> SessionResult:
	return SessionResult.Failure(AuthError.Configuration(_NO_BACKEND))
```

That was correct with no endpoint to exchange against. This epic supplies the endpoint.

**Script-only.** No Swift, no Android, no CI changes, no native binaries. Epic B's `Apple native` job now gates every PR, so it will run — but nothing here should change its result.

---

## The four decisions worth making before writing code

### 1. The HTTP client belongs in `core/`, the auth semantics do not

Invariant 1 says `core/` never references a subsystem. An HTTP client is generic — purchase will want one for receipt validation, mobile may not. So:

- **`core/HttpClient.fs`** — method, URL, headers, body → a `HttpOutcome`. Knows nothing about tokens, 401, or sessions.
- **`auth/internal/BackendClient.fs`** — injects `Authorization: Bearer`, maps `HttpOutcome` onto `ResponseResult`/`AuthError`, and recognises the backend's session-expired signal.

Putting bearer injection in `core/` would be the easy mistake. It couples core to a concept only auth has.

### 2. Tagged unions cannot be generic, so core speaks its own outcome

Same constraint that produced `NativeOutcome`. `core/HttpOutcome.fs` is concrete; `BackendClient` maps it. Do not attempt `Result[T, E]`.

### 3. Refresh must be single-flight

Ten concurrent `valid_access_token()` calls on an expired session must produce **one** refresh, not ten. Every caller awaits the same in-flight result. This is the same hazard `RequestGuard` solves for native calls, and it is the single most likely source of a real defect in this epic.

### 4. A 401 retries exactly once

`request()` on a 401 refreshes and retries once. If the retry also 401s, that is `SessionExpired` — not an infinite loop. The retry must not itself be retried.

---

## Prerequisite: #57 must land first

`Jwt.expiry_from` returns `0` both for "no `exp` claim" and for `exp: 0`, and `AuthSession.is_expired_at` reads `0` as never-expiring:

```
func is_expired_at(now_unix_seconds: int) -> bool:
	var expiry: int = expires_at()
	if expiry == 0:
		return false          # "no expiry" — but exp: 0 lands here too
	return now_unix_seconds >= expiry
```

A token stamped `exp: 0` is expired at the epoch and would be treated as eternal. Harmless while nothing refreshes; **this epic makes expiry detection load-bearing**, so it is Task 1 rather than a follow-up.

---

## File Structure

| File | Responsibility |
|---|---|
| `addons/FoundryKit/auth/internal/Jwt.fs` | **Modify** — distinguish absent `exp` from `exp: 0` |
| `addons/FoundryKit/auth/AuthSession.fs` | **Modify** — consume the new expiry signal |
| `addons/FoundryKit/core/HttpOutcome.fs` | Concrete transport outcome union |
| `addons/FoundryKit/core/HttpClient.fs` | Generic async HTTP; no auth concepts |
| `addons/FoundryKit/auth/BackendConfig.fs` | Base URL and endpoint paths |
| `addons/FoundryKit/auth/internal/BackendClient.fs` | Bearer injection, `HttpOutcome` → `AuthError` |
| `addons/FoundryKit/auth/internal/SessionStore.fs` | In-memory session authority + single-flight refresh |
| `addons/FoundryKit/auth/AuthSubsystem.fs` | **Modify** — wire exchange, refresh, request, signals |
| `addons/FoundryKit/auth/AuthApi.fs` | **Modify only if** `configure_backend` must join the trait |
| `test_project/tests/support/fake_http_client.notest.fs` | Scripted transport double |
| `test_project/tests/*.test.fs` | One suite per new type |

---

## Conventions that apply to every task

From `CLAUDE.md`, plus what epics A and B paid for:

- **Every addon script needs a committed `.uid`.** Run `task uids`; `scripts/test-uid-coverage` gates it inside the required `Scripts` check.
- **`scripts/test-package-product-name` is now a fourth check** in `task test:scripts` (added by #90).
- One global type per `.fs` file. `trait_name` + `uses`; never `extends` across files.
- **No `_` wildcard over a tagged union.** A trailing `return` after an exhaustive `match` is required and is not a wildcard.
- **Rest parameters must be untyped** (`...values: Array`).
- **No `int(some_variant)`** under `unsafe_call_argument=2` — narrow with `raw is int` or bind a typed local first.
- Class annotations follow `namespace`/`import`. **Tabs.**
- `test_project/project.foundry` sets `untyped_declaration` and five `unsafe_*` warnings to **error**.
- **`core/` must not reference `auth`** — `scripts/test-import-boundaries` proves it.
- **`Apple native` is now a required check** (~5 min). Every PR waits on it, including script-only ones.
- Inline lambdas connected to signals do not reliably observe captured-variable mutation — use a connected method.

---

### Task 1: Distinguish an absent `exp` from `exp: 0`

**Goal:** Make expiry detection unambiguous before anything depends on it. Closes #57.

**Files:**
- Modify: `addons/FoundryKit/auth/internal/Jwt.fs`
- Modify: `addons/FoundryKit/auth/AuthSession.fs`
- Modify: `test_project/tests/auth-jwt.test.fs`, `test_project/tests/auth-session.test.fs`

**Acceptance Criteria:**
- [ ] A token with no `exp` claim is distinguishable from one with `exp: 0`
- [ ] `AuthSession.is_expired_at` treats a **present** `exp: 0` as expired at any time
- [ ] A token with no `exp` is still treated as never-expiring — the backend remains the authority, and a 401 surfaces expiry
- [ ] An opaque (non-JWT) token behaves as "no expiry", unchanged
- [ ] Every pre-existing `JwtTests` and `AuthSessionTests` case passes unchanged

**Verify:** `task test:foundrylib`

**Steps:**

- [ ] **Step 1: Write the failing tests**

Append to `test_project/tests/auth-jwt.test.fs`:

```
## `exp: 0` is a real claim meaning "expired at the epoch"; its absence means "unknown".
## Returning 0 for both made them indistinguishable — see #57.
func test_absent_exp_reports_no_expiry() -> void:
	Expect.that(Jwt.has_expiry(_token_without_exp())).to_be_false()

func test_explicit_zero_exp_reports_an_expiry() -> void:
	Expect.that(Jwt.has_expiry(_token_with_exp(0))).to_be_true()
	Expect.that(Jwt.expiry_from(_token_with_exp(0))).to_equal(0)

func test_opaque_token_reports_no_expiry() -> void:
	Expect.that(Jwt.has_expiry("not-a-jwt")).to_be_false()
```

Append to `test_project/tests/auth-session.test.fs`:

```
func test_session_with_explicit_zero_expiry_is_expired() -> void:
	var session: AuthSession = AuthSession.new(_token_with_exp(0), "r", Provider.GOOGLE, {}, {})
	Expect.that(session.is_expired_at(1)).to_be_true()
	Expect.that(session.is_expired_at(0)).to_be_true()

func test_session_without_expiry_is_never_expired() -> void:
	var session: AuthSession = AuthSession.new("opaque-token", "r", Provider.GOOGLE, {}, {})
	Expect.that(session.is_expired_at(9999999999)).to_be_false()
```

Add whatever `_token_with_exp` / `_token_without_exp` helpers the suites lack, following the existing fixture style in `auth-jwt.test.fs`.

- [ ] **Step 2: Run to verify they fail**

Run: `task test:foundrylib`
Expected: FAIL — `Jwt.has_expiry` is not defined.

- [ ] **Step 3: Add `has_expiry` to `Jwt.fs`**

```
## Returns whether the token carries a readable `exp` claim at all.
##
## [method expiry_from] cannot express this: it returns 0 both for an absent claim and
## for a literal `exp: 0`, which are different facts. A caller deciding whether a token
## can expire must ask this first.
static func has_expiry(token: String) -> bool:
	var claims: Dictionary = _claims_of(token)
	if claims.is_empty():
		return false
	var expiry: Variant = claims.get("exp")
	return expiry is int or expiry is float
```

- [ ] **Step 4: Consume it in `AuthSession.fs`**

```
## Returns whether the access token carries a readable expiry at all.
func has_expiry() -> bool:
	return Jwt.has_expiry(access_token)

## Returns whether the session is expired at [param now_unix_seconds].
##
## A token with no readable `exp` never expires locally — the backend is the authority
## and a 401 surfaces expiry through [code]AuthError.SessionExpired[/code]. A token that
## does carry `exp` is compared against it, including a literal 0, which per RFC 7519
## means expired at the epoch.
func is_expired_at(now_unix_seconds: int) -> bool:
	if not has_expiry():
		return false
	return now_unix_seconds >= expires_at()
```

- [ ] **Step 5: Run to verify they pass**

Run: `task uids && task test:foundrylib && task test:scripts`
Expected: PASS. Confirm the pre-existing cases are untouched.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/auth test_project/tests
git commit -m "fix(auth): distinguish an absent exp claim from exp: 0"
```

---

### Task 2: `HttpOutcome` — the core transport union

**Goal:** One concrete union describing how an HTTP attempt ended, with no auth vocabulary in it.

**Files:**
- Create: `addons/FoundryKit/core/HttpOutcome.fs`
- Create: `test_project/tests/http-outcome.test.fs`

**Acceptance Criteria:**
- [ ] Cases cover: answered with a status, transport failure, and timeout
- [ ] **No auth concepts** — no `Unauthorized`, no `SessionExpired`, no token vocabulary. A 401 is just a status
- [ ] Exhaustive `match` compiles with no `_` wildcard
- [ ] `scripts/test-import-boundaries` still passes — `core/` references no subsystem

**Verify:** `task test:foundrylib && task test:scripts`

**Steps:**

- [ ] **Step 1: Write the failing test**

`test_project/tests/http-outcome.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name HttpOutcomeTests
extends RefCounted
uses Test

func _describe(outcome: HttpOutcome) -> String:
	match outcome:
		HttpOutcome.Answered(status_code, body):
			return "answered:%d:%d" % [status_code, body.size()]
		HttpOutcome.TransportFailed(detail):
			return "transport:%s" % detail
		HttpOutcome.TimedOut(elapsed_seconds):
			return "timeout:%.0f" % elapsed_seconds
	return "unreachable"

func test_answered_carries_status_and_body() -> void:
	Expect.that(_describe(HttpOutcome.Answered(200, "hi".to_utf8_buffer()))) \
		.to_equal("answered:200:2")

func test_a_401_is_just_a_status() -> void:
	# core/ has no notion of authorization; only auth/ interprets 401.
	Expect.that(_describe(HttpOutcome.Answered(401, PackedByteArray()))) \
		.to_equal("answered:401:0")

func test_transport_failure_carries_detail() -> void:
	Expect.that(_describe(HttpOutcome.TransportFailed("dns"))).to_equal("transport:dns")

func test_timeout_carries_elapsed() -> void:
	Expect.that(_describe(HttpOutcome.TimedOut(30.0))).to_equal("timeout:30")
```

- [ ] **Step 2: Run to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `HttpOutcome` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/core/HttpOutcome.fs`**

```
namespace games.cafecito.foundrykit.core

## How one HTTP attempt ended.
##
## Deliberately free of authorization vocabulary: a 401 is [code]Answered(401, ...)[/code]
## and nothing more. Only the auth subsystem knows what a 401 means. This keeps `core/`
## usable by purchase and mobile, which have their own interpretations of the same status.
##
## Concrete rather than generic because tagged unions take no type parameters — the same
## constraint that produced [NativeOutcome]. Each subsystem maps this into its own result.
enum_name HttpOutcome:
	## The server answered. [param status_code] may be any status, including 4xx and 5xx.
	Answered(status_code: int, body: PackedByteArray)
	## The request never reached the server — DNS, TLS, connection refused.
	TransportFailed(detail: String)
	## The request exceeded its watchdog window.
	TimedOut(elapsed_seconds: float)
```

- [ ] **Step 4: Run to verify it passes**

Run: `task uids && task test:foundrylib && task test:scripts`
Expected: PASS — 4 tests.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/core test_project/tests/http-outcome.test.fs
git commit -m "feat(core): add the HTTP transport outcome union"
```

---

### Task 3: `HttpClient` — generic async transport

**Goal:** An awaitable HTTP call returning `HttpOutcome`, injectable so no test needs a network.

**Files:**
- Create: `addons/FoundryKit/core/HttpClient.fs`
- Create: `test_project/tests/support/fake_http_client.notest.fs`
- Create: `test_project/tests/http-client.test.fs`

**Acceptance Criteria:**
- [ ] `async func send(method, url, headers, body, timeout_seconds) -> HttpOutcome`
- [ ] Backed by a `trait_name HttpTransport` so `BackendClient` depends on the contract, not the engine — that is the seam every later test uses
- [ ] A request with no `SceneTree` does not hang forever
- [ ] The instance stays alive across its own await, the way `NativeRequest` had to (see its `_in_flight` registry and the codex findings behind it)
- [ ] `core/` still references no subsystem

**Verify:** `task test:foundrylib && task test:scripts`

**Steps:**

- [ ] **Step 1: Create the trait**

`HttpTransport` is a separate head type, so it needs its own file. Put it in `core/HttpTransport.fs`:

```
namespace games.cafecito.foundrykit.core

## The contract an HTTP transport satisfies.
##
## Exists so the auth subsystem can be tested against a scripted double with no network
## and no [SceneTree]. Production composes [HttpClient]; tests compose a fake.
trait_name HttpTransport
	abstract async func send(
			method: String,
			url: String,
			headers: PackedStringArray,
			body: PackedByteArray,
			timeout_seconds: float) -> HttpOutcome
```

- [ ] **Step 2: Write the failing test**

`test_project/tests/http-client.test.fs` — exercise the **fake** against the trait, and assert `HttpClient` satisfies the same contract. Full network behaviour is not unit-testable here; that is honest and should be stated in a doc comment rather than faked.

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

class_name HttpClientTests
extends RefCounted
uses Test

var _fake: FakeHttpClient

func before_each() -> void:
	_fake = FakeHttpClient.new()

func test_fake_returns_the_scripted_outcome() -> void:
	_fake.enqueue(HttpOutcome.Answered(204, PackedByteArray()))
	var outcome: HttpOutcome = await _fake.send(
			"GET", "https://example.test/x", PackedStringArray(), PackedByteArray(), 5.0)
	var status: int = -1
	match outcome:
		HttpOutcome.Answered(status_code, _body):
			status = status_code
		HttpOutcome.TransportFailed(_detail):
			status = -2
		HttpOutcome.TimedOut(_elapsed):
			status = -3
	Expect.that(status).to_equal(204)

func test_fake_records_what_it_was_sent() -> void:
	_fake.enqueue(HttpOutcome.Answered(200, PackedByteArray()))
	await _fake.send("POST", "https://example.test/session",
			PackedStringArray(["Authorization: Bearer abc"]), "{}".to_utf8_buffer(), 5.0)
	Expect.that(_fake.last_method).to_equal("POST")
	Expect.that(_fake.last_url).to_equal("https://example.test/session")
	Expect.that(_fake.last_headers.has("Authorization: Bearer abc")).to_be_true()

func test_real_client_satisfies_the_transport_contract() -> void:
	var client: HttpTransport = HttpClient.new(FoundryKitLog.new("test"))
	Expect.that(client != null).to_be_true()
```

- [ ] **Step 3: Run to verify it fails, then implement**

Create `test_project/tests/support/fake_http_client.notest.fs` (a queue of scripted outcomes plus `last_*` recorders, `uses HttpTransport`), then `addons/FoundryKit/core/HttpClient.fs` wrapping `HTTPRequest`.

`HttpClient` must:
- keep itself alive across its own `await` (mirror `NativeRequest`'s static `_in_flight` registry — a caller that drops its local reference must not free a suspended request)
- settle exactly once
- defer the settling emission by one message-queue turn, for the same reason `NativeRequest._resolve` does
- return `TransportFailed` rather than pushing an error when there is no `SceneTree`

**Read `core/NativeRequest.fs` before writing this.** It solved the identical lifetime and single-settle problems, and codex took six rounds to get them right. Reuse the shape; do not rediscover it.

- [ ] **Step 4: Verify and commit**

```bash
task uids && task test:foundrylib && task test:scripts && task lint
git add addons/FoundryKit/core test_project/tests
git commit -m "feat(core): add an awaitable HTTP client behind a transport trait"
```

---

### Task 4: `BackendConfig`

**Goal:** Describe where the backend is and which paths it exposes, so `AuthSubsystem` stops answering `Configuration`.

**Files:**
- Create: `addons/FoundryKit/auth/BackendConfig.fs`
- Create: `test_project/tests/auth-backend-config.test.fs`

**Acceptance Criteria:**
- [ ] Carries a base URL plus the exchange, refresh and sign-out paths, each defaulted
- [ ] Joins base and path without producing a double slash or dropping one
- [ ] An empty base URL is detectable, so `AuthSubsystem` can still report `Configuration` honestly when unconfigured
- [ ] A `##` doc comment on every public member

**Verify:** `task test:foundrylib`

**Steps:**

- [ ] **Step 1: Write the failing test** covering URL joining (`https://api.test` + `/v1/session`, `https://api.test/` + `v1/session`, and an empty base), then implement, then verify. Follow the TDD order used throughout this plan.

- [ ] **Step 2: Implement `BackendConfig.fs`** as a `class_name ... extends RefCounted` with `##`-documented fields and a `url_for(path: String) -> String` helper plus `is_configured() -> bool`.

- [ ] **Step 3: Commit**

```bash
task uids && task test:foundrylib
git add addons/FoundryKit/auth/BackendConfig.fs* test_project/tests/auth-backend-config.test.fs
git commit -m "feat(auth): add backend endpoint configuration"
```

---

### Task 5: `BackendClient` — auth semantics over the transport

**Goal:** The one place that knows a bearer token goes in a header and a 401 means the session is gone.

**Files:**
- Create: `addons/FoundryKit/auth/internal/BackendClient.fs`
- Create: `test_project/tests/auth-backend-client.test.fs`

**Acceptance Criteria:**
- [ ] Injects `Authorization: Bearer <token>` **only** when a non-empty token is supplied
- [ ] Maps `HttpOutcome.Answered(2xx)` → `ResponseResult.Success`
- [ ] Maps `Answered(401)` → an `AuthResponse` with `session_expired = true`, **not** an error — the caller decides whether to refresh
- [ ] Maps other non-2xx → `AuthError.RequestFailed(status, body)`
- [ ] Maps `TransportFailed` → `AuthError.RequestFailed(0, detail)`; `TimedOut` → `AuthError.TimedOut`
- [ ] **Never logs the token**, not even truncated
- [ ] Exhaustive `match` over `HttpOutcome`, no `_` wildcard

**Verify:** `task test:foundrylib && task test:scripts`

**Steps:**

- [ ] **Step 1: Write the failing tests** — one per mapping above, driven by `FakeHttpClient`. Include an explicit test that no header is added when the token is empty, and one asserting a 401 produces `Success` with `session_expired == true` rather than a `Failure`.

- [ ] **Step 2: Implement**, taking `HttpTransport` and `BackendConfig` by injection.

- [ ] **Step 3: Verify the boundary check still passes** — `BackendClient` lives in `auth/internal/` and may import `auth.internal`, but `core/` must not have gained any auth reference: `task test:scripts`.

- [ ] **Step 4: Commit**

```bash
task uids && task test:foundrylib && task test:scripts
git add addons/FoundryKit/auth/internal/BackendClient.fs* test_project/tests/auth-backend-client.test.fs
git commit -m "feat(auth): map transport outcomes onto auth results"
```

---

### Task 6: `SessionStore` — session authority and single-flight refresh

**Goal:** Hold the active session and guarantee that concurrent refreshes collapse into one.

**Files:**
- Create: `addons/FoundryKit/auth/internal/SessionStore.fs`
- Create: `test_project/tests/auth-session-store.test.fs`

**Acceptance Criteria:**
- [ ] Holds at most one active session; `has_session()`, `access_token()`, `set_session()`, `clear()`
- [ ] **`refresh()` is single-flight**: N concurrent callers on an expired session cause exactly **one** backend call, and all N receive the same result — assert the fake's call count is 1
- [ ] A refresh that fails clears the in-flight marker, so a later refresh is attempted rather than permanently short-circuited
- [ ] A refresh that succeeds replaces the session and reports that tokens rotated, so the subsystem can emit `tokens_refreshed`
- [ ] Refreshing with no refresh token fails fast with `AuthError.SessionExpired`, without a backend call
- [ ] No shared mutable static state — instance fields only

**Verify:** `task test:foundrylib`

**Steps:**

- [ ] **Step 1: Write the failing tests.** The single-flight case is the important one and is easy to write wrongly. Start three refreshes before awaiting any, then await all three:

```
func test_concurrent_refreshes_make_exactly_one_backend_call() -> void:
	_store.set_session(_expired_session())
	_fake.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	var first: Coroutine[SessionResult] = _store.refresh()
	var second: Coroutine[SessionResult] = _store.refresh()
	var third: Coroutine[SessionResult] = _store.refresh()
	var results: Array[SessionResult] = [await first, await second, await third]
	Expect.that(_fake.send_count).to_equal(1)
	for result: SessionResult in results:
		Expect.that(_describe(result).begins_with("ok:")).to_be_true()
```

Also test that a **failed** refresh leaves the store able to try again — a stuck in-flight marker would wedge the session permanently, which is the worst failure mode in this task.

- [ ] **Step 2: Implement.** Hold the in-flight refresh as a signal other callers await, mirroring `NativeRequest._settled`. Clear the marker in **both** the success and failure paths.

- [ ] **Step 3: Verify and commit**

```bash
task uids && task test:foundrylib
git add addons/FoundryKit/auth/internal/SessionStore.fs* test_project/tests/auth-session-store.test.fs
git commit -m "feat(auth): add the session store with single-flight refresh"
```

---

### Task 7: Wire `AuthSubsystem`

**Goal:** Replace the four `Configuration` stubs with the real thing, and emit the two unsolicited signals.

**Files:**
- Modify: `addons/FoundryKit/auth/AuthSubsystem.fs`
- Modify: `test_project/tests/auth-subsystem.test.fs`

**Acceptance Criteria:**
- [ ] `_exchange` posts the credential to the exchange endpoint and returns `SessionResult.Success(session)`
- [ ] `valid_access_token()` returns the current token when valid, refreshes when expired, and fails `SessionExpired` when it cannot
- [ ] `refresh_session()` delegates to `SessionStore`
- [ ] `request()` injects the token, and on a 401 refreshes and **retries exactly once**
- [ ] `tokens_refreshed` fires when a refresh rotates tokens; `session_expired` fires when a session lapses outside any call
- [ ] **With no `BackendConfig`, every path still reports `Configuration` exactly as today** — an unconfigured consumer must not regress
- [ ] The class doc comment, which currently explains why these methods report `Configuration`, is rewritten
- [ ] **An injection seam exists for the transport.** `AuthSubsystem._init(log)` today takes only a log and resolves `AuthBackendFactory` itself, so there is no way to hand it a `FakeHttpClient`. Add optional constructor parameters (`transport: HttpTransport? = null`, `config: BackendConfig? = null`) defaulting to the production objects, matching how `AppleAuthBackend.new(log, native_override)` solved the identical problem in epic B. Without this, none of this task's tests can be written

**Verify:** `task test:foundrylib && task test:scripts && task lint`

**Steps:**

- [ ] **Step 1: Write the failing tests**, including these two, which are the ones most likely to be got wrong:

```
func test_a_401_refreshes_and_retries_exactly_once() -> void:
	_fake.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	_fake.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	_fake.enqueue(HttpOutcome.Answered(200, "{}".to_utf8_buffer()))
	var result: ResponseResult = await _subsystem.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("ok:200")
	Expect.that(_fake.send_count).to_equal(3)

func test_a_second_401_after_refresh_is_session_expired_not_a_loop() -> void:
	_fake.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	_fake.enqueue(HttpOutcome.Answered(200, _fresh_session_json()))
	_fake.enqueue(HttpOutcome.Answered(401, PackedByteArray()))
	var result: ResponseResult = await _subsystem.request(HttpMethod.GET, "/me", null)
	Expect.that(_describe_response(result)).to_equal("fail:session_expired")
	Expect.that(_fake.send_count).to_equal(3)
```

The `send_count` assertions are the point: without them a retry loop passes the happy-path test and hangs in production.

- [ ] **Step 2: Implement**, keeping the unconfigured path intact.

- [ ] **Step 3: Verify and commit**

```bash
task uids && task test:foundrylib && task test:scripts && task lint
git add addons/FoundryKit/auth/AuthSubsystem.fs* test_project/tests/auth-subsystem.test.fs
git commit -m "feat(auth): exchange credentials and keep sessions valid"
```

---

### Task 8: Configuration entry point and facade check

**Goal:** Let a consumer actually supply a `BackendConfig`, and confirm the public surface still matches `AuthApi`.

**Files:**
- Modify: `addons/FoundryKit/auth/AuthApi.fs` (only if `configure_backend` must join the trait)
- Modify: `addons/FoundryKit/auth/AuthSubsystem.fs`
- Modify: `test_project/tests/auth-facade.test.fs`, `test_project/tests/foundry-kit.test.fs`

**Acceptance Criteria:**
- [ ] A consumer can supply a `BackendConfig` through `FoundryKit.auth`
- [ ] If `configure_backend` joins `AuthApi`, **every** implementor is updated — including any test double — or the suite will not compile
- [ ] `FoundryKit.auth` still resolves and the facade test still asserts the full surface
- [ ] **`@autoload` name shadowing**: inside `FoundryKit.fs` and its tests, `FoundryKit` in expression position resolves to the singleton instance, not the class. Construct via `preload(...)` if needed — see `CLAUDE.md`

**Verify:** `task test:foundrylib && task test:scripts && task lint`

**Steps:**

- [ ] **Step 1:** Decide whether `configure_backend` belongs on the trait or only on the concrete subsystem. Adding it to `AuthApi` is the honest choice **if** game code is expected to call it; state which you chose and why in the PR body.
- [ ] **Step 2:** Write the failing facade test, implement, verify, commit.

---

## Definition of Done

- [ ] `task test:foundrylib` passes
- [ ] `task test:scripts` passes — all four checks, including `test-package-product-name`
- [ ] `task lint` passes
- [ ] `Apple native` passes (it is now a required check; nothing here should affect it)
- [ ] `core/` contains no reference to `auth` — proven by `scripts/test-import-boundaries`
- [ ] No `_` wildcard over any union
- [ ] Every new `.fs` has a committed `.uid`
- [ ] **#57 is closed by Task 1**
- [ ] Concurrent refresh provably makes one backend call
- [ ] A 401 retries exactly once, proven by call count

## Deliberately out of scope

- **Keychain / secure storage** — epic E. `store_session` and `restore_session` still report `Storage`
- **Sign in with Apple** — epic E. **Android** — epic F
- **Desktop OAuth loopback** — epic D
- **Real network testing** — every test drives `FakeHttpClient`. First real-endpoint verification belongs to epic G
- **#79, #81** — upstream `Foundry-Swift-Binary` defects, unrelated to this epic
- **#64, #66** — track-only follow-ups from epic A

## Open risks

1. **Single-flight refresh is the likeliest defect.** The failure mode is a wedged in-flight marker that blocks every future refresh. Task 6 tests the failure path explicitly for that reason.
2. **A 401 retry loop passes the happy-path test.** Only the `send_count` assertions catch it.
3. **`AuthApi` has exactly one implementor today** (`AuthSubsystem`), so adding a method to the trait is cheap now and gets more expensive with every epic. If `configure_backend` belongs on the contract, add it in this epic rather than later.
4. **`HttpClient` lifetime across `await`.** `NativeRequest` needed a static registry to survive a caller dropping its reference; the same hazard applies here and is easy to miss until a test hangs.
