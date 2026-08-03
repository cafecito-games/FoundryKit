# Auth Epic E — Keychain-backed secure session storage

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A session survives an application restart on iOS and macOS, stored in the Keychain, and a stored session can never be restored against a backend that did not issue it.

**Architecture:** A `SecureStore` trait sits beside the existing `AuthBackend` contract as the platform secure-storage seam, with an Apple implementation over `Security.framework` and a Null implementation everywhere else. What gets written is a `StoredSession` record that carries the **backend origin** alongside the tokens; `restore_session` takes the currently configured origin and refuses — and erases — any record that does not match it.

**Tech Stack:** Foundry Script, Swift 6 / `Security.framework`, FoundrySwift macros, `foundry.testlib`.

**Scope note:** Epic E of plan 2. **Secure storage only.** Sign in with Apple is deferred (see "Deliberately out of scope"). Desktop and Android secure storage are deferred. `#114` stays open.

---

## The decision this epic exists to get right

`#112` was filed during epic C and is a **required acceptance criterion** here:

> Once epic E adds Keychain-backed secure storage, a session persisted under backend A can be restored and installed under backend B after `configure_backend()` moves the origin. The restored access and refresh tokens — minted by A — would then be presented to B.

Epic C closed the **in-memory** version of this hole. `AuthSubsystem.configure_backend` compares `_origin_of(base_url)` before and after, and ends the session on a move (`AuthSubsystem.fs:206-217`). That protection **does not survive a restart**, which is the exact case storage introduces: process starts, game calls `configure_backend(B)`, game calls `restore_session()`, and a record written under A loads with nothing to compare it against.

**So the origin binding lives in the stored record itself.** Not in a member variable, not in the subsystem — in the bytes that go into the Keychain. That is the only form of it that a restart cannot erase.

### Why the origin travels as a parameter

`AuthBackend.store_session` / `restore_session` currently take no origin, and a backend has no `BackendConfig` — that belongs to `BackendClient` and `AuthSubsystem`. Task 5 changes both signatures to carry it:

```
abstract async func store_session(session: AuthSession, origin: String) -> CompletionResult
abstract async func restore_session(origin: String) -> SessionResult
```

Three alternatives were considered and rejected:

- **Give the backend a `BackendConfig`.** Couples every platform backend to the session layer for one string, and invites a second source of truth for the origin.
- **Compare in `AuthSubsystem` after the backend returns the session.** The session would already be materialised and the record still on disk; the comparison would be one `return` away from being skipped, and the erase-on-mismatch would live in a different file from the read.
- **A dedicated `AuthError` variant for a mismatch.** Cleaner to read, but adding a variant to `AuthError` breaks exhaustiveness at every `match` in the addon and both test suites — real churn for a case a caller cannot act on differently than any other storage failure. `AuthError.Storage(detail)` is used, with a detail string a human can act on. **If a later epic needs to distinguish it programmatically, add the variant then and take the churn deliberately.**

Changing the trait signature is deliberate: it produces a compile error in `NullAuthBackend`, `AppleAuthBackend` and `DesktopAuthBackend`, so no implementor can silently keep the origin-blind version.

---

## File structure

| File | Responsibility |
|---|---|
| `auth/internal/StoredSession.fs` | The persisted record: origin, tokens, provider, schema version. Serialize / deserialize. Pure. |
| `auth/internal/StoredSessionOutcome.fs` | `Parsed` / `Malformed` / `VersionUnsupported` |
| `auth/internal/SecureStore.fs` | `trait_name` — the platform secure-storage seam |
| `auth/internal/SecureLoadOutcome.fs` | `Loaded(bytes)` / `Absent` / `Failed(detail)` |
| `auth/internal/NullSecureStore.fs` | Unavailable everywhere secure storage is not implemented |
| `auth/internal/AppleSecureStore.fs` | Drives the Keychain native class; probes `ClassDB` |
| `native/.../FoundryKitAuth/Keychain.swift` | `Security.framework` wrapper, registered as `iOSKeychain` |
| `native/.../FoundryKitAuth/KeychainStatus.swift` | `OSStatus` → outcome mapping |

One global head type per `.fs` file — `StoredSessionOutcome` and `SecureLoadOutcome` get their own files, not nested declarations, because they are referenced from other files.

---

## Traps this epic will hit

Each of these has already cost an attempt somewhere in this project, or is specific to the Keychain.

**1. Keychain unit tests fail on a headless CI runner.** `SecItemAdd` from an unsigned XCTest bundle with no host application returns `errSecMissingEntitlement` (**-34018**) on macOS. The `Apple native` CI job runs exactly that way. **Swift tests therefore cover query construction and `OSStatus` mapping only — never a live Keychain round-trip.** Do not attempt to make a live round-trip pass in CI by adding entitlements or a test host; that is a much larger change than this epic, and the `.fs` side is already covered against a fake. State the limitation in the test file and the PR body.

**2. `.uid` files are not produced by the test runner.** Six new `.fs` files means six new `.uid` files. `task test:foundrylib` will be green and the commit still incomplete. Run `foundry --headless project import --project .` from the repository root and commit what it writes. `scripts/test-uid-coverage` gates this.

**3. Awaiting an already-completed coroutine hangs the suite** rather than failing it (`#107`). Keychain calls are **synchronous** — this is the one native surface in the project that does not need `NativeRequest` or a correlation token. Keep them synchronous. If a `SecureStore` method is declared `async` for contract uniformity, it must still return without suspending.

**4. Never log a token.** `BackendClient` established this and it applies with more force here: `StoredSession` serializes tokens, so its `_to_string`, its logging and any error detail must name fields, never values.

**5. Cross-platform test assertions.** The suite runs on macOS locally and ubuntu in CI. Never assert a platform-specific result from `resolve_current()` or from any store that probes `ClassDB` — the native class is absent in both environments, but assert that fact through the injected fake, not through the host. `#75` and `#121` both had to fix this exact defect.

**6. Language rules.** No `_` wildcard over a union. No cross-file `extends` — contracts are `trait_name` composed with `uses`. Inner classes need `uses` on the declaration line. Rest params untyped (`...values: Array`). Never `int(some_variant)` under `unsafe_call_argument=2`. The test project sets `untyped_declaration`, `unsafe_cast`, `unsafe_call_argument`, `unsafe_method_access` and `unsafe_property_access` to **error**.

---

### Task 1: `StoredSession` — the record and its origin binding

**Goal:** The persisted form of a session, carrying the origin that issued it, with a versioned wire format that fails loudly on anything it does not understand.

**Files:**
- Create: `addons/FoundryKit/auth/internal/StoredSession.fs`
- Create: `addons/FoundryKit/auth/internal/StoredSessionOutcome.fs`
- Create: `test_project/tests/auth-stored-session.test.fs`

**Acceptance Criteria:**
- [ ] Carries `schema_version`, `origin`, `access_token`, `refresh_token`, `provider`, `raw`, `extras`
- [ ] `to_bytes()` / `from_bytes()` round-trip **every** field, including a nested `raw` dictionary and non-ASCII values in `extras`
- [ ] An unrecognised `schema_version` yields `VersionUnsupported(version)` — never a crash, never a silent default
- [ ] Truncated bytes, empty bytes and non-JSON bytes each yield `Malformed(detail)`
- [ ] A record missing `origin` is `Malformed` — **not** treated as an empty origin, which would match an unconfigured backend
- [ ] No token value appears in any detail string, log line or `_to_string`
- [ ] Exhaustive `match` over `StoredSessionOutcome`, no `_` wildcard

**The `origin` field is the whole point.** A round-trip test that omits it passes while leaving the epic's security property unimplemented — assert it explicitly.

**Steps:**

- [ ] **Step 1: Write the failing tests**

```
## A record missing `origin` is malformed, not origin-less. An absent origin that
## deserialized to "" would compare equal to an unconfigured backend's origin and
## restore against it — which is the disclosure this epic exists to prevent.
func test_a_record_without_an_origin_is_malformed() -> void:
	var bytes: PackedByteArray = JSON.stringify({
		"schema_version": 1, "access_token": "a", "refresh_token": "r",
	}).to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("malformed")

func test_the_origin_survives_a_round_trip() -> void:
	var record: StoredSession = _record_for("https://api.example.com")
	var parsed: StoredSession = _parsed(StoredSession.from_bytes(record.to_bytes()))
	Expect.that(parsed.origin).to_equal("https://api.example.com")

func test_an_unknown_schema_version_is_reported_not_guessed() -> void:
	var bytes: PackedByteArray = JSON.stringify({
		"schema_version": 99, "origin": "https://api.example.com",
	}).to_utf8_buffer()
	Expect.that(_outcome_name(StoredSession.from_bytes(bytes))).to_equal("version_unsupported")
```

- [ ] **Step 2: Run and watch them fail** — `task test:foundrylib`. Expected: `Could not find type "StoredSession"`.

- [ ] **Step 3: Implement `StoredSessionOutcome.fs`**

```
namespace games.cafecito.foundrykit.auth.internal

## How an attempt to read a stored session record ended.
enum_name StoredSessionOutcome:
	## The bytes parsed into a record this build understands.
	Parsed(record: StoredSession)
	## The bytes were not a record at all.
	Malformed(detail: String)
	## A record, but written by a schema this build does not know.
	VersionUnsupported(version: int)
```

- [ ] **Step 4: Implement `StoredSession.fs`**, then run the tests to green.

- [ ] **Step 5: Generate `.uid` files and commit**

```bash
foundry --headless project import --project .
git add addons/FoundryKit/auth/internal/StoredSession*.fs \
        addons/FoundryKit/auth/internal/StoredSession*.fs.uid \
        test_project/tests/auth-stored-session.test.fs
git commit -m "feat(auth): the stored session record, bound to its backend origin"
```

**Verify:** `task test:foundrylib && task test:scripts`

```json:metadata
{"files": ["addons/FoundryKit/auth/internal/StoredSession.fs", "addons/FoundryKit/auth/internal/StoredSessionOutcome.fs"], "verifyCommand": "task test:foundrylib && task test:scripts", "acceptanceCriteria": ["origin survives the round trip", "a record without an origin is malformed", "unknown schema version reported not guessed", "no token in any detail string"]}
```

---

### Task 2: `SecureStore` trait and the Null implementation

**Goal:** The platform secure-storage seam, plus the honest answer on every platform that does not have one.

**Files:**
- Create: `addons/FoundryKit/auth/internal/SecureStore.fs`
- Create: `addons/FoundryKit/auth/internal/SecureLoadOutcome.fs`
- Create: `addons/FoundryKit/auth/internal/NullSecureStore.fs`
- Create: `test_project/tests/support/fake_secure_store.notest.fs`
- Create: `test_project/tests/auth-null-secure-store.test.fs`

**Acceptance Criteria:**
- [ ] `trait_name SecureStore` declares `is_available() -> bool`, `async store(bytes) -> CompletionResult`, `async load() -> SecureLoadOutcome`, `async erase() -> CompletionResult`
- [ ] `SecureLoadOutcome` distinguishes `Loaded(bytes)`, `Absent` and `Failed(detail)` — **"nothing stored" is not a failure**, and a caller must be able to tell them apart
- [ ] `NullSecureStore.is_available()` is `false`; `store`/`erase` fail with `AuthError.Storage`; `load` returns `Absent`
- [ ] `FakeSecureStore` records `store_count`, `erase_count`, `last_stored_bytes`, and can be primed with a `SecureLoadOutcome`
- [ ] No method suspends — every implementation returns without awaiting (see trap 3)

**Why `Absent` is a distinct case:** a first launch and a Keychain failure produce the same `SessionResult.Failure` at the API boundary, but they must not produce the same *behaviour* internally. Collapsing them means a transient Keychain error looks like "no session stored" and the record is never retried or reported.

**Verify:** `task test:foundrylib && task test:scripts`

```json:metadata
{"files": ["addons/FoundryKit/auth/internal/SecureStore.fs", "addons/FoundryKit/auth/internal/SecureLoadOutcome.fs", "addons/FoundryKit/auth/internal/NullSecureStore.fs", "test_project/tests/support/fake_secure_store.notest.fs"], "verifyCommand": "task test:foundrylib && task test:scripts", "acceptanceCriteria": ["Absent is distinct from Failed", "null store reports unavailable", "no method suspends"]}
```

---

### Task 3: The `iOSKeychain` native class

**Goal:** A `Security.framework` wrapper exposed to Foundry Script, synchronous, with `OSStatus` mapped to a small outcome vocabulary.

**Files:**
- Create: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/Keychain.swift`
- Create: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/KeychainStatus.swift`
- Modify: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/FoundryKitAuthEntry.swift`
- Create: `native/apple/FoundryKitNative/Tests/FoundryKitAuthTests/KeychainStatusTests.swift`

**Acceptance Criteria:**
- [ ] Registered as `iOSKeychain` in the entry symbol's `types:` list, alongside `iOSGoogleSignIn`
- [ ] `@Callable` methods are **synchronous** — no correlation token, no signals, no `NativeRequest` (trap 3)
- [ ] Item class `kSecClassGenericPassword`; service derived from the bundle identifier so two apps cannot collide
- [ ] Accessibility is **`kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`** — the record must not sync to iCloud Keychain and must not be readable before first unlock
- [ ] A store over an existing item **updates** rather than duplicating (`SecItemUpdate` after `errSecDuplicateItem`, or delete-then-add)
- [ ] `errSecItemNotFound` maps to "absent", not to an error
- [ ] `errSecMissingEntitlement` (-34018) maps to an error whose detail names the entitlement problem — this is what an unsigned CI runner produces and a human will need to recognise it
- [ ] Swift tests cover `OSStatus` mapping and query construction; **no test performs a live Keychain round-trip** (trap 1), and the file says so

**Do not** add entitlements, a test host, or a signing identity to make a live round-trip work in CI. That is a bigger change than this epic and is not required — the `.fs` side is covered against `FakeSecureStore`, and first real-device verification belongs to epic G.

**Verify:** `task apple:test` (or the repository's current Apple test task) and `scripts/build-apple-auth`

```json:metadata
{"files": ["native/apple/FoundryKitNative/Sources/FoundryKitAuth/Keychain.swift", "native/apple/FoundryKitNative/Sources/FoundryKitAuth/KeychainStatus.swift"], "verifyCommand": "task apple:test", "acceptanceCriteria": ["registered in the entry symbol", "synchronous, no correlation token", "AfterFirstUnlockThisDeviceOnly", "store updates rather than duplicating", "errSecItemNotFound is absent not error", "no live keychain round-trip in CI"]}
```

---

### Task 4: `AppleSecureStore` — the Foundry Script side

**Goal:** Drive `iOSKeychain` through the `SecureStore` seam, and resolve to unavailable when the binary is absent.

**Files:**
- Create: `addons/FoundryKit/auth/internal/AppleSecureStore.fs`
- Create: `test_project/tests/auth-apple-secure-store.test.fs`

**Blocked by:** Tasks 2, 3

**Acceptance Criteria:**
- [ ] `class_name AppleSecureStore extends RefCounted` / `uses SecureStore`
- [ ] Constructor takes `(log, native_override: Object? = null)`, matching `AppleAuthBackend._init` — production passes null and probes `ClassDB` through `NativeBridge`
- [ ] **A missing binary is not an error.** With the native absent, `is_available()` is `false`, `load()` is `Absent`, and `store`/`erase` fail with `AuthError.Storage` — never a crash, never a null dereference. This is invariant 6.
- [ ] Every native status is mapped exhaustively; no `_` wildcard
- [ ] No test touches a real Keychain — all drive an injected fake native

**Verify:** `task test:foundrylib && task test:scripts`

```json:metadata
{"files": ["addons/FoundryKit/auth/internal/AppleSecureStore.fs"], "verifyCommand": "task test:foundrylib && task test:scripts", "acceptanceCriteria": ["missing binary resolves to unavailable, not an error", "injected native override for tests", "exhaustive status mapping"]}
```

---

### Task 5: Origin-bound `store_session` / `restore_session`

**Goal:** The security property. A stored session is refused and erased when it did not come from the backend now configured.

**Files:**
- Modify: `addons/FoundryKit/auth/internal/AuthBackend.fs` (both signatures)
- Modify: `addons/FoundryKit/auth/internal/AppleAuthBackend.fs`
- Modify: `addons/FoundryKit/auth/internal/NullAuthBackend.fs`
- Modify: `addons/FoundryKit/auth/internal/DesktopAuthBackend.fs`
- Modify: `test_project/tests/auth-apple-backend.test.fs`

**Blocked by:** Tasks 1, 4

**Acceptance Criteria:**
- [ ] `store_session(session, origin)` and `restore_session(origin)` on the trait and all three implementors
- [ ] `AppleAuthBackend` delegates to an injected `SecureStore`, defaulting to `AppleSecureStore`
- [ ] `store_session` writes a `StoredSession` whose `origin` is the parameter — not a value read from anywhere else
- [ ] **A restore whose stored origin differs from the parameter returns `AuthError.Storage` AND erases the record.** Assert the erase (`erase_count == 1`), not only the returned error — leaving a rejected record on disk means every later launch re-reads a session it will never accept
- [ ] A restore with a matching origin returns the session with every field intact
- [ ] `Malformed` and `VersionUnsupported` records are also erased — an unreadable record is not going to become readable
- [ ] `has_stored_session()` reflects the store, and returns `false` when the store is unavailable
- [ ] `NullAuthBackend` and `DesktopAuthBackend` keep returning `AuthError.Storage`, with the new signatures

**Mutation-check (required, both results in the PR body):**
1. Remove the origin comparison → confirm a **named** test fails, and that it fails by *restoring a foreign session*, not merely by returning the wrong error.
2. Remove the erase-on-mismatch → confirm the `erase_count` assertion fails.

**Verify:** `task test:foundrylib && task test:scripts && task lint`

```json:metadata
{"files": ["addons/FoundryKit/auth/internal/AuthBackend.fs", "addons/FoundryKit/auth/internal/AppleAuthBackend.fs"], "verifyCommand": "task test:foundrylib && task test:scripts && task lint", "acceptanceCriteria": ["origin mismatch refuses AND erases", "matching origin restores intact", "malformed records erased", "all three implementors updated"]}
```

---

### Task 6: Wire `AuthSubsystem`, and the cross-origin test `#112` asks for

**Goal:** Sessions are written when they are obtained and rotated, restored against the configured origin, and erased on `clear_session`.

**Files:**
- Modify: `addons/FoundryKit/auth/AuthSubsystem.fs`
- Modify: `test_project/tests/auth-subsystem.test.fs`

**Blocked by:** Task 5

**Acceptance Criteria:**
- [ ] A successful credential exchange stores the session, with `_origin_of(_config.base_url)` as the origin
- [ ] A refresh that rotates tokens re-stores; a refresh that fails does not
- [ ] `restore_session()` passes the configured origin through to the backend
- [ ] `clear_session()` erases the stored record — it already calls `clear_stored_session`; assert the record is gone, not just that the call returned
- [ ] A store failure **does not fail the sign-in.** The player is signed in; they simply will not be next launch. Log it and return the session.
- [ ] The generation guards in `restore_session` (`AuthSubsystem.fs:358-371`) still hold — a restore landing after a sign-out or a sign-in is still discarded
- [ ] With no `BackendConfig`, every path reports `Configuration` exactly as today

**The test `#112` requires**, stated verbatim there:

> A test stores under origin A, reconfigures to origin B, restores, and asserts no token from A is ever presented to B

Assert all three: the restore fails, the record is erased, and **the fake transport never receives a request carrying A's access or refresh token** — check the recorded headers and bodies, not just a call count. A test that asserts only the returned error passes while the token is presented.

**Verify:** `task test:foundrylib && task test:scripts && task lint`

```json:metadata
{"files": ["addons/FoundryKit/auth/AuthSubsystem.fs"], "verifyCommand": "task test:foundrylib && task test:scripts && task lint", "acceptanceCriteria": ["stores on exchange and rotation", "restore passes the configured origin", "a store failure does not fail the sign-in", "no token from A ever reaches B"]}
```

---

## Deliberately out of scope

Each of these is filed as its own tracking issue so it cannot be lost between epics.

- **Sign in with Apple** — native `ASAuthorization` on iOS/macOS, and the desktop web flow that `ProviderConfig.Apple(service_id, redirect_uri)` already anticipates. `Provider.APPLE` continues to report `Unavailable`.
- **Desktop and Android secure storage** — Linux, Windows and Android keep returning `AuthError.Storage`. Honest, and consistent with invariant 6.
- **`#114`** — `sign_out` racing an in-flight refresh. Unchanged; nothing here touches that path.
- **`#125`** — the loopback listener settling on the first request.
- **Real device and real endpoint verification** — epic G. Nothing here proves a Keychain write survives a real restart on real hardware; the Swift tests cannot even reach the Keychain in CI (trap 1).

## What "green" will and will not mean

The `.fs` layer is proven against fakes and the origin binding is proven by mutation. **No test in this epic writes to a real Keychain**, on CI or locally, so "secure storage works" means the protocol and the refusal logic are correct — not that `Security.framework` accepted the query on a device. First real verification belongs to epic G, and the test files say so.
