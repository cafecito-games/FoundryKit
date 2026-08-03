# Auth Epic D — Desktop OAuth Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sign in on Linux and Windows, where there is no native provider SDK — the OAuth 2.0 installed-app flow with PKCE, a loopback redirect listener, and the system browser.

**Architecture:** A loopback HTTP listener in `auth/internal/` accepts the redirect on `127.0.0.1` at an ephemeral port. `DesktopAuthBackend` builds the authorization URL, opens the browser, waits for the callback, verifies `state`, and exchanges the code for an ID token through epic C's `HttpTransport` — the same seam every test in epic C drives with a fake.

**Tech Stack:** Foundry Script · `TCPServer` · `OS.shell_open` · `HttpTransport` (epic C) · FoundryLib `foundry.testlib`.

**Spec:** `docs/superpowers/specs/2026-08-02-foundrykit-design.md`
**Predecessors:** epic A (#34), epic B (#68), epic C (#92) — all merged.
**Source material:** `AuthenticationKit@foundry-migration:addons/AuthenticationKit/internal/AuthenticationKitBackendDesktop.fs` (563 lines) and its 416-line test. **It already implements PKCE S256 and `state` verification.** Port the protocol; do not re-derive it.

---

## What is already true, verified — do not re-derive

| Fact | Consequence |
|---|---|
| `Platform.from_os_name` maps `Linux`/`Windows`/`X11`/`*BSD` → `DESKTOP`, and **`macOS` → `MACOS`, never `DESKTOP`** | Desktop OAuth reaches macOS only if the factory routes it there — resolved below: it does not |
| `AuthBackendFactory.for_platform` routes `IOS, MACOS → AppleAuthBackend`; `ANDROID, DESKTOP, UNKNOWN → null` | Only the `DESKTOP` arm changes in the base plan |
| `ProviderConfig.Google` already carries `desktop_client_id` | No config change needed for Google |
| Epic C shipped `HttpTransport` (trait), `HttpClient`, `FakeHttpClient`, `BackendClient`, `SessionStore` | The token exchange goes through `HttpTransport`, so it is testable with no network |
| Legacy uses `TCPServer.listen(0, "127.0.0.1")` then `get_local_port()` | Ephemeral port; the redirect URI is only known after listening |
| Legacy constants: auth `https://accounts.google.com/o/oauth2/v2/auth`, token `https://oauth2.googleapis.com/token`, 120s listen timeout, 30s token timeout | Reuse |
| Legacy sets `audience` to `desktop_client_id` on the credential | Epic B left `Credential.Google`'s `audience` empty because the native does not return it; the desktop flow **does** know it |

---

## RESOLVED DECISION — macOS does NOT fall back to desktop OAuth

**Decided by the owner, 2026-08-03: no composite.**

`AuthBackendFactory` keeps routing `IOS, MACOS → AppleAuthBackend`. Desktop OAuth is **Linux and Windows only**. A macOS build shipped without `bin/auth/` continues to have no sign-in, and an `AppleAuthBackend` with no native class continues to report `is_available() == false`.

**Invariant 6 is preserved exactly as written** and the spec is untouched:

> A missing native binary and an unsupported platform resolve identically — to a Null backend reporting `is_available() == false`, never an error.

The alternative — a `CompositeAuthBackend` that tries native and falls back to desktop OAuth — would have made a missing binary silently work through a different path. Better UX for one case; it contradicts the invariant, and amending a stated architectural invariant is not something to slip into an implementation PR.

If a macOS-without-native build turns out to matter in practice, the composite can be added later as its own issue with the spec change made deliberately. **Nothing in tasks 1–6 needs rework for that.**

## Two decisions already made, with reasoning

### 1. The loopback listener lives in `auth/internal/`, not `core/`

Epic C put the HTTP **client** in `core/` because purchase demonstrably needs one for receipt validation. The reverse argument applies here: **nothing outside auth will ever run an OAuth redirect listener.** Putting it in `core/` would be speculative generality, and `core/` is the one place where a wrong guess is expensive because three subsystems share it.

### 2. Desktop session persistence is out of scope

The legacy backend wrote the session to `user://authenticationkit_desktop_active_session.bin`. **Do not port that.** `store_session`/`restore_session` continue to return `AuthError.Storage`, exactly as the Apple and Null backends do today. Desktop has no Keychain, so "where does a desktop session live, and encrypted with what" is a genuine design question — and #112 already records that persisted sessions must be origin-bound. Solving it badly here would be worse than not solving it.

---

## File Structure

| File | Responsibility |
|---|---|
| `addons/FoundryKit/auth/internal/LoopbackServer.fs` | Listen on `127.0.0.1:0`, accept one request, parse the query, reply with HTML |
| `addons/FoundryKit/auth/internal/PkcePair.fs` | `code_verifier` / `code_challenge` (S256) and `state` generation |
| `addons/FoundryKit/auth/internal/DesktopAuthBackend.fs` | The flow; satisfies `AuthBackend` |
| `addons/FoundryKit/auth/internal/AuthBackendFactory.fs` | **Modify** — route `DESKTOP` |
| `test_project/tests/support/fake_browser.notest.fs` | Records the URL instead of opening it |
| `test_project/tests/*.test.fs` | One suite per new type |

---

## Conventions that apply to every task

From `CLAUDE.md`, plus what epics A–C paid for. **Each of these cost someone an attempt.**

- **Awaiting a `Coroutine` that suspended and already completed hangs forever** (#107) — the suite *hangs* rather than fails. Never await a coroutine you started earlier; have callers await a shared signal emitted with `call_deferred`. This is live here: the loopback wait is exactly that shape.
- **A signal declared in a cross-file trait is not flattened into the composer** (#107) — redeclare it on the composing class.
- **A function whose return type is a TRAIT fails the runtime return check** when handed a composer (#107) — assign to a trait-typed member instead. It bit #99 and #100; assume it will bite anything factory-shaped.
- **Write `Closes #N` as PLAIN TEXT, never in backticks** — GitHub ignores closing keywords in code spans; a PR merged this way leaves its issue open and silently blocks epic closure. Verify with `gh pr view <PR> --json closingIssuesReferences`.
- No `_` wildcard over a tagged union; a trailing `return` after an exhaustive `match` is required and is not a wildcard.
- **Rest parameters must be untyped**; **`int(some_variant)` fails** under `unsafe_call_argument=2` — narrow with `raw is int`.
- One head type per `.fs`; nested declarations are exempt. Class annotations follow `namespace`/`import`. **Tabs.**
- **`.uid` mandatory** — run `task uids`; `scripts/test-uid-coverage` gates it.
- `task test:scripts` runs **four** checks. **`Apple native` is a required check taking ~5 minutes**; every PR waits on it even though this epic touches no native code.
- **Assert call counts, not just outcomes** — epics B and C caught retry loops and duplicate emissions only that way.
- **Mutation-check your guards**: remove each, confirm a *named* test fails, restore. Put the results in the PR body.

---

### Task 1: `PkcePair` — verifier, challenge, state

**Goal:** The cryptographic material for RFC 7636, isolated so it can be tested without a network or a browser.

**Files:**
- Create: `addons/FoundryKit/auth/internal/PkcePair.fs`
- Create: `test_project/tests/auth-pkce.test.fs`

**Acceptance Criteria:**
- [ ] `code_verifier` is base64url, unpadded, 43–128 characters (RFC 7636 §4.1)
- [ ] `code_challenge` is `BASE64URL(SHA256(ASCII(code_verifier)))`, unpadded — verify against a **known RFC 7636 §4.1 test vector**, not just self-consistency
- [ ] `state` is independently random and never equal to the verifier
- [ ] No `+`, `/` or `=` appears in any output — those break in a URL query
- [ ] Two successive pairs differ

**Verify:** `task test:foundrylib`

**Steps:**

- [ ] **Step 1: Write the failing test.** Use the RFC's published vector so the encoding is checked against the standard rather than against itself:

```
## RFC 7636 Appendix B: this exact verifier must produce this exact challenge.
const _RFC_VERIFIER: String = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"
const _RFC_CHALLENGE: String = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"

func test_challenge_matches_the_rfc_7636_vector() -> void:
	Expect.that(PkcePair.challenge_for(_RFC_VERIFIER)).to_equal(_RFC_CHALLENGE)

func test_verifier_is_unreserved_and_within_length() -> void:
	var pair: PkcePair = PkcePair.generate()
	Expect.that(pair.code_verifier.length() >= 43).to_be_true()
	Expect.that(pair.code_verifier.length() <= 128).to_be_true()
	for forbidden: String in ["+", "/", "="]:
		Expect.that(pair.code_verifier.contains(forbidden)).to_be_false()
		Expect.that(pair.code_challenge.contains(forbidden)).to_be_false()

func test_state_is_independent_of_the_verifier() -> void:
	var pair: PkcePair = PkcePair.generate()
	Expect.that(pair.state == pair.code_verifier).to_be_false()
	Expect.that(pair.state.is_empty()).to_be_false()

func test_two_pairs_differ() -> void:
	Expect.that(PkcePair.generate().code_verifier == PkcePair.generate().code_verifier) \
		.to_be_false()
```

- [ ] **Step 2: Run to verify it fails.** `task test:foundrylib` → FAIL, `PkcePair` not defined.
- [ ] **Step 3: Implement.** A `class_name PkcePair extends RefCounted` with `code_verifier`, `code_challenge`, `state`, a `static func generate()` and a `static func challenge_for(verifier)`. Use `Crypto.generate_random_bytes` and `HashingContext`/`String.sha256_buffer`, then base64url-encode by substituting `+`→`-`, `/`→`_` and stripping `=`.
- [ ] **Step 4: Verify and commit.**

```bash
task uids && task test:foundrylib && task test:scripts && task lint
git add addons/FoundryKit/auth/internal/PkcePair.fs* test_project/tests/auth-pkce.test.fs
git commit -m "feat(auth): add PKCE verifier, challenge and state generation"
```

---

### Task 2: `LoopbackServer` — accept one redirect

**Goal:** Listen on an ephemeral loopback port, accept exactly one HTTP request, hand back its query parameters, and reply to the browser with a page the player can read.

**Files:**
- Create: `addons/FoundryKit/auth/internal/LoopbackServer.fs`
- Create: `test_project/tests/auth-loopback-server.test.fs`

**Acceptance Criteria:**
- [ ] Binds `127.0.0.1` on port **0**, then reports the assigned port — the redirect URI is not known until after listening
- [ ] **Binds loopback only.** A server reachable from the network would let anyone on the LAN deliver a forged callback. Assert the bound address
- [ ] Parses the query string of the first request line into a dictionary
- [ ] Writes an HTTP response with a readable HTML body, then closes — otherwise the player sees a browser error on success
- [ ] **Times out** and reports it, rather than waiting forever, if no callback arrives
- [ ] Stopping is idempotent; stopping while waiting settles the wait
- [ ] **Settles exactly once**

**Verify:** `task test:foundrylib`

**Steps:**

- [ ] **Step 1: READ `core/HttpClient.fs` and `core/NativeRequest.fs` first.** This class has the identical await-lifetime shape and both already solve it: a static in-flight registry so a suspended wait is not freed when its caller drops the local reference, a single-settle guard, and a settling emission deferred by one message-queue turn. **Do not rediscover these** — codex needed six rounds on `NativeRequest` and four on `HttpClient`, and #107 records why the naive version *hangs the suite instead of failing it*.

- [ ] **Step 2: Write the failing tests.** These can be genuine end-to-end tests without leaving the machine — connect a `StreamPeerTCP` to the server's own port:

```
func test_binds_loopback_and_reports_its_port() -> void:
	var server: LoopbackServer = LoopbackServer.new(_log)
	Expect.that(server.start()).to_be_true()
	Expect.that(server.port() > 0).to_be_true()
	server.stop()

func test_delivers_the_query_of_the_first_request() -> void:
	var server: LoopbackServer = LoopbackServer.new(_log)
	server.start()
	var pending: Coroutine[LoopbackOutcome] = server.await_callback(5.0)
	_connect_and_send(server.port(), "GET /?code=abc&state=xyz HTTP/1.1\r\n\r\n")
	var outcome: LoopbackOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("received:abc:xyz")
	server.stop()

func test_times_out_when_no_callback_arrives() -> void:
	var server: LoopbackServer = LoopbackServer.new(_log)
	server.start()
	var outcome: LoopbackOutcome = await server.await_callback(0.2)
	Expect.that(_describe(outcome)).to_equal("timeout")
	server.stop()

func test_stop_is_idempotent() -> void:
	var server: LoopbackServer = LoopbackServer.new(_log)
	server.start()
	server.stop()
	server.stop()
```

`LoopbackOutcome` is a small tagged union (`Received(query)` / `TimedOut` / `Failed(detail)`) and needs **its own file** — one head type per `.fs`.

- [ ] **Step 3: Implement,** mirroring `HttpClient`'s lifetime shape. Poll the `TCPServer` from a `SceneTree` timer or `process_frame`; the headless runner **does** have a `SceneTree` (established by #95).
- [ ] **Step 4: Verify and commit.**

---

### Task 3: The authorization URL

**Goal:** Build the Google authorization URL correctly, and prove it, before anything opens a browser.

**Files:**
- Modify: `addons/FoundryKit/auth/internal/DesktopAuthBackend.fs` (created here)
- Create: `test_project/tests/auth-desktop-url.test.fs`

**Acceptance Criteria:**
- [ ] Includes `client_id`, `redirect_uri`, `response_type=code`, `scope`, `code_challenge`, `code_challenge_method=S256`, `state`, `nonce`
- [ ] **Every value is percent-encoded.** An unencoded `redirect_uri` breaks the request and is the classic bug here
- [ ] The redirect URI is `http://127.0.0.1:<port>/` with the **actual** bound port
- [ ] Uses `desktop_client_id`, never `web_client_id` or `ios_client_id`
- [ ] A test asserts the full URL against an expected string with a fixed PKCE pair and port — not a substring check

**Verify:** `task test:foundrylib`

---

### Task 4: `DesktopAuthBackend` — the flow

**Goal:** Compose PKCE, the browser, the loopback listener and the token exchange into a backend that returns a `Credential`.

**Files:**
- Modify: `addons/FoundryKit/auth/internal/DesktopAuthBackend.fs`
- Create: `test_project/tests/support/fake_browser.notest.fs`
- Create: `test_project/tests/auth-desktop-backend.test.fs`

**Acceptance Criteria:**
- [ ] Satisfies `AuthBackend` via `uses`
- [ ] Takes an `HttpTransport`, a browser opener and a `LoopbackServer` factory **by injection**, so no test opens a browser or reaches the network
- [ ] **`state` mismatch fails and does not exchange the code** — assert the transport's `send_count == 0`. A callback whose `state` does not match is an attacker's, and exchanging it is the whole reason `state` exists
- [ ] An `error=` in the callback (the player pressed Cancel) → `AuthError.Cancelled`, **not** a generic failure
- [ ] A missing `code` → `AuthError.MissingField("code")`
- [ ] Success maps to `CredentialResult.Success(Credential.Google(id_token, ..., audience))` with **`audience` set to `desktop_client_id`** — epic B left it empty because the native does not return it; here it is known
- [ ] `Provider.APPLE` and `Provider.EMAIL_PASSWORD` return `Unavailable` — Google only in this epic
- [ ] `sign_in_silent` returns `Unavailable`: there is no silent path without a stored session, and storage is out of scope
- [ ] **The listener is stopped on every exit path** — success, cancel, state mismatch, timeout, and token-exchange failure. A leaked listener holds a port for the process lifetime
- [ ] Session storage returns `AuthError.Storage`
- [ ] `backend_name()` returns `"desktop"`

**Verify:** `task test:foundrylib && task test:scripts`

**Steps:**

- [ ] **Step 1: Write the failing tests**, one per criterion. The `state`-mismatch test is the one that matters most and is easy to write toothlessly — assert the **call count**, not just the returned error:

```
func test_state_mismatch_fails_without_exchanging_the_code() -> void:
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_deliver_callback("code=abc&state=WRONG")
	Expect.that(_describe(await pending)).to_equal("fail:request_failed")
	Expect.that(_transport.send_count).to_equal(0)
```

- [ ] **Step 2: Implement.** Mind the `#107` await hazard around the loopback wait, and stop the listener in a single place every path reaches.
- [ ] **Step 3: Mutation-check.** Remove the `state` comparison and confirm `test_state_mismatch_fails_without_exchanging_the_code` fails; remove the stop-on-failure and confirm a port-leak test fails. Record both in the PR body.
- [ ] **Step 4: Verify and commit.**

---

### Task 5: Route `DESKTOP` in the factory

**Goal:** Linux and Windows resolve to the desktop backend.

**Files:**
- Modify: `addons/FoundryKit/auth/internal/AuthBackendFactory.fs`
- Modify: `test_project/tests/auth-backend-factory.test.fs`

**Acceptance Criteria:**
- [ ] `PlatformKind.DESKTOP` resolves to `DesktopAuthBackend` (`backend_name() == "desktop"`)
- [ ] `IOS`/`MACOS` still resolve to `"apple"`; `ANDROID`/`UNKNOWN` still to `"null"`
- [ ] The comment naming which epic owns each remaining platform is **updated, not deleted** — Android is epic F
- [ ] **No test asserts a platform-specific name for `resolve_current()`.** The suite runs on macOS locally and ubuntu in CI; #75 already had to fix exactly this, so do not reintroduce it
- [ ] The desktop backend constructs without a browser or a network

**Verify:** `task test:foundrylib && task test:scripts && task lint`

---

### Task 6: End-to-end desktop flow test

**Goal:** One test that drives the whole flow through the seams, so the composition is verified rather than only its parts.

**Files:**
- Modify: `test_project/tests/auth-desktop-backend.test.fs`

**Acceptance Criteria:**
- [ ] Drives: configure → `sign_in` → assert the browser received a URL carrying the expected `code_challenge` and `state` → deliver a matching callback to the real `LoopbackServer` over a real loopback socket → assert the transport received the code and `code_verifier` → return a token → assert a `Credential.Google`
- [ ] Asserts `send_count == 1` for the exchange
- [ ] **States plainly in a doc comment and in the PR body what is still unverified**: no real Google endpoint, no real browser, no real user consent. First real verification belongs to epic G

**Verify:** `task test:foundrylib`

---

## Definition of Done

- [ ] `task test:foundrylib`, `task test:scripts` (four checks), `task lint` pass
- [ ] `Apple native` passes — nothing here should affect it
- [ ] `core/` still references no subsystem
- [ ] No `_` wildcard over any union; every new `.fs` has a committed `.uid`
- [ ] **`state` mismatch provably does not exchange the code** (`send_count == 0`)
- [ ] **The listener is stopped on every exit path**, proven by a port-leak test
- [ ] The PKCE challenge matches the RFC 7636 vector
- [ ] Mutation-check results in the PR bodies

## Deliberately out of scope

- **Desktop session persistence** — see "decisions already made". `store_session`/`restore_session` keep returning `Storage`
- **Providers other than Google** — Apple's web flow is epic E, email/password is backend-only
- **Android** (epic F), **Keychain** (epic E)
- **Real endpoint, real browser, real consent** — epic G
- **#107, #110, #112, #114** — open follow-ups, unrelated

## Open risks

1. **The loopback wait is exactly the shape that hangs.** #107's hazard — awaiting an already-completed coroutine — applies directly. The failure mode is a *hung suite*, not a failing test, so it looks like an infinite loop rather than a bug. Task 2 mirrors `HttpClient` for this reason.
2. **A leaked `TCPServer` holds a port for the process lifetime.** Every failure path must stop it, which is why it is a Definition-of-Done item and not just an acceptance criterion.
3. **CI runs `Foundry Script suite` on ubuntu.** Binding a loopback port there should be fine, but it is the first test in this repository to open a socket — if the runner sandboxes that, Task 2 surfaces it. Report it rather than deleting the test.
4. **`OS.shell_open` cannot be verified in CI.** It is injected and faked; that nothing actually opens is stated, not hidden.
