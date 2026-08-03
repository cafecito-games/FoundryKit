# Auth Epic B — Apple Native (Google Sign-In) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove FoundryKit's entire native pipeline on the narrowest slice that can prove it — a Swift multi-target package producing a signed xcframework, registered through a `.foundryextension`, driving Google Sign-In on Apple platforms, and returning a `Credential` to the merged auth layer.

**Architecture:** `FoundryKitCore.framework` ships as a shared dynamic dependency; `FoundryKitAuth.xcframework` is the first extension binary. The native protocol carries a **per-call correlation token** from the outset, and `core/NativeRequest` filters on it — the requirement recorded in the design spec (#29) that neither epic-A component could satisfy alone.

**Tech Stack:** Swift 6 · SwiftPM + xcodegen · Foundry-Swift-Binary `0.1.0-alpha.2` · GoogleSignIn-iOS · GitHub Actions `macos-26` · Foundry Script.

**Spec:** `docs/superpowers/specs/2026-08-02-foundrykit-design.md`
**Predecessor:** epic A (#34) — merged, 143 tests green.

**Scope note:** Epic B of seven in plan 2. **Google only.** Sign in with Apple and Keychain storage are epic E; Android is epic F; the backend exchange is epic C.

---

## Why this epic exists

Everything merged so far is script-only, on a pipeline epic 1 proved. **Nothing about the native layer has ever been exercised.** Five epics depend on it. This one runs the whole path end to end on one provider so surprises surface now, not after five more epics assume it works.

Four things are genuinely unproven, in rough order of risk:

1. **macOS CI** — the workflow is ubuntu-only today. Swift needs `macos-26`, at roughly 10× the minute cost.
2. **Foundry-Swift-Binary `alpha.1 → alpha.2`** — deferred since the spec, never validated against the extension-loading path.
3. **The correlation token** — specified in #29, never implemented against a real native.
4. **`.foundryextension` registration** of a multi-target package.

---

## Verified facts — do not re-derive

Checked directly against the repositories, not assumed:

| Fact | Consequence |
|---|---|
| `Foundry-Swift-Binary` is **public**, tags `0.1.0-alpha.1` and `0.1.0-alpha.2`, **no GitHub releases** | SwiftPM resolves by **tag**. No App token needed. The legacy `prepare-foundryswift-binary` script's private-asset download via `gh` is obsolete — do not port it wholesale |
| `Foundry` and `Foundry-Tools` are public | The epic-1 CI pattern (download pinned engine, `go install anvil`) needs no credentials |
| Legacy CI used `runs-on: macos-26` | Use the same |
| Legacy Swift used `@Foundry class X: RefCounted`, `@Signal("a","b") var s: SignalWithArguments<...>`, `@Callable func f()`, `#initFoundryExtension(cdecl:types:)` | The macro surface to target |
| Legacy signals were `signInSuccess(id_token, email, display_name, authorization_code)` and `signInFailed(code, message)` | **No correlation token.** This epic adds one |
| `NativeRequest.await_outcome(target, success_signal, payload_fields, failure_signal, timeout_seconds)` | The signature the token must thread through |
| `async func` with no `await` compiles fine | Backends may resolve synchronously |

---

## The correlation token — read before Task 2

The spec requires every `NativeRequest`-shaped native operation to carry a per-call token echoed in **both** its success and failure emissions, and the script side to **filter incoming emissions by it**. Echoing without filtering accomplishes nothing — that was codex's finding on #29.

The failure it prevents:

1. Request A awaits; the native never answers.
2. The watchdog fires; A settles `TimedOut` and disconnects.
3. `RequestGuard.end()` releases single-flight.
4. Request B begins and connects to the same target and signal names.
5. The original operation finally emits — and B takes it as its own result.

This changes **merged core code** (`core/NativeRequest.fs`). That is deliberate and in scope: it is the one component that can filter, and epic A could not do it without a native to correlate against.

**Design:** `await_outcome` gains a `correlation_token: String` parameter. When it is non-empty, the handler compares the emission's **first** payload field against the token and **ignores non-matching emissions without settling**. When it is empty, behaviour is exactly as today — every existing caller and every test in epic 1 must keep passing unchanged.

---

## File Structure

| File | Responsibility |
|---|---|
| `native/apple/FoundryKitNative/Package.swift` | SwiftPM manifest — Core + Auth targets |
| `native/apple/FoundryKitNative/project.yml` | xcodegen config — framework targets per platform |
| `native/apple/FoundryKitNative/Sources/FoundryKitCore/CorrelatedSignal.swift` | Shared: token-carrying emission helper |
| `native/apple/FoundryKitNative/Sources/FoundryKitAuth/FoundryKitAuthEntry.swift` | `#initFoundryExtension` entry point |
| `native/apple/FoundryKitNative/Sources/FoundryKitAuth/GoogleSignIn.swift` | The `iOSGoogleSignIn` native class |
| `native/apple/FoundryKitNative/Sources/FoundryKitAuth/SignInOutcome.swift` | Sendable outcome carrier + pure mapping functions |
| `native/apple/FoundryKitNative/Tests/FoundryKitAuthTests/` | Swift unit tests over the pure functions |
| `addons/FoundryKit/core/NativeRequest.fs` | **Modify** — correlation-token filtering |
| `addons/FoundryKit/auth/internal/AppleAuthBackend.fs` | Apple backend, Google only |
| `addons/FoundryKit/auth/internal/AuthBackendFactory.fs` | **Modify** — IOS/MACOS → Apple backend |
| `addons/FoundryKit/FoundryKitAuth.foundryextension` | Extension registration |
| `addons/FoundryKit/bin/auth/`, `bin/core/` | Binary output, gitignored |
| `Taskfile.yml` | **Modify** — `apple:auth` build target |
| `scripts/build-apple-auth` | xcodegen + xcodebuild + xcframework assembly |
| `.github/workflows/pr-check.yml` | **Modify** — add `apple` job on `macos-26` |

---

## Conventions that apply to every task

From `CLAUDE.md` — read it first; these have each cost an attempt in earlier epics:

- **Every addon script needs a committed `.uid`.** Run `task uids`; `scripts/test-uid-coverage` is inside the required `Scripts` check.
- One global type per `.fs` file. `trait_name` + `uses`, never `abstract class_name`, never path-form `extends`.
- No `_` wildcard over a tagged union. A trailing `return` after an exhaustive match is required and is not a wildcard.
- Class annotations follow `namespace`/`import`; `@autoload` may precede it.
- `test_project/project.foundry` sets `untyped_declaration` and five `unsafe_*` warnings to **error**.
- Generic-trait concrete-method returns must be bound to a typed local before calling methods on them.
- Inline lambdas connected to signals do not reliably observe captured-variable mutation — use a connected method.
- **zsh does not word-split unquoted `$var`** — use `while IFS= read -r x; … <<< "$list"` or an array.
- Indentation is **tabs** in `.fs`; `prek` enforces whitespace/EOF/line endings and trips on shell and YAML edits.

---

### Task 1: Swift package skeleton with a Core target

**Goal:** A buildable two-target SwiftPM package pinned to Foundry-Swift-Binary `0.1.0-alpha.2`, with `FoundryKitCore` as a shared **dynamic** product and `FoundryKitAuth` depending on it — proving the alpha.2 bump and the multi-target layout before any auth code exists.

**Files:**
- Create: `native/apple/FoundryKitNative/Package.swift`
- Create: `native/apple/FoundryKitNative/Sources/FoundryKitCore/CorrelatedSignal.swift`
- Create: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/Placeholder.swift`
- Create: `native/apple/FoundryKitNative/Tests/FoundryKitCoreTests/CorrelatedSignalTests.swift`
- Modify: `.gitignore`

**Acceptance Criteria:**
- [ ] `swift build` succeeds against Foundry-Swift-Binary **0.1.0-alpha.2**
- [ ] `swift test` runs and passes
- [ ] `FoundryKitCore` is `.dynamic`; `FoundryKitAuth` depends on it
- [ ] `FoundryKitCore` holds **no shared mutable static state** — the spec's rule, load-bearing because it is loaded once and shared by three dynamically loaded frameworks
- [ ] `.build/` and `*.xcodeproj` are gitignored
- [ ] The `alpha.1 → alpha.2` bump is confirmed working, or the failure is reported rather than worked around

**Verify:** `cd native/apple/FoundryKitNative && swift test` → all tests pass

**Steps:**

- [ ] **Step 1: Create `native/apple/FoundryKitNative/Package.swift`**

```swift
// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "FoundryKitNative",
    platforms: [
        .iOS(.v17),
        .macOS(.v14),
    ],
    products: [
        // Shared dependency, not an extension: no entry symbol, listed in each
        // extension's [dependencies] block.
        .library(name: "FoundryKitCore", type: .dynamic, targets: ["FoundryKitCore"]),
        .library(name: "FoundryKitAuth", type: .dynamic, targets: ["FoundryKitAuth"]),
    ],
    dependencies: [
        .package(
            url: "https://github.com/cafecito-games/Foundry-Swift-Binary.git",
            exact: "0.1.0-alpha.2"
        ),
    ],
    targets: [
        .target(
            name: "FoundryKitCore",
            dependencies: [
                .product(name: "FoundrySwift", package: "Foundry-Swift-Binary"),
            ],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .target(
            name: "FoundryKitAuth",
            dependencies: [
                "FoundryKitCore",
                .product(name: "FoundrySwift", package: "Foundry-Swift-Binary"),
            ],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "FoundryKitCoreTests",
            dependencies: ["FoundryKitCore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
```

- [ ] **Step 2: Write the failing test**

Create `native/apple/FoundryKitNative/Tests/FoundryKitCoreTests/CorrelatedSignalTests.swift`:

```swift
import XCTest
@testable import FoundryKitCore

final class CorrelatedSignalTests: XCTestCase {

    func testTokenIsGeneratedNonEmpty() {
        XCTAssertFalse(CorrelationToken.make().isEmpty)
    }

    func testTokensAreUnique() {
        let first = CorrelationToken.make()
        let second = CorrelationToken.make()
        XCTAssertNotEqual(first, second)
    }

    func testTokenMatchesItself() {
        let token = CorrelationToken.make()
        XCTAssertTrue(CorrelationToken.matches(token, token))
    }

    func testDifferentTokensDoNotMatch() {
        XCTAssertFalse(CorrelationToken.matches(CorrelationToken.make(), CorrelationToken.make()))
    }

    func testEmptyExpectationMatchesAnything() {
        // An empty expected token means "no correlation required" — the
        // pre-correlation behaviour every existing caller relies on.
        XCTAssertTrue(CorrelationToken.matches("", "anything"))
        XCTAssertTrue(CorrelationToken.matches("", ""))
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd native/apple/FoundryKitNative && swift test`
Expected: FAIL — `CorrelationToken` is not defined.

- [ ] **Step 4: Create `Sources/FoundryKitCore/CorrelatedSignal.swift`**

```swift
import Foundation

/// Per-call correlation tokens for the native request/response protocol.
///
/// A native operation echoes the token it was given in both its success and its
/// failure emission, so the script side can tell whether an emission belongs to the
/// request it is currently awaiting. Without this, a late reply from a timed-out
/// request can be misattributed to the next request on the same target — see the
/// design spec's "Protocol requirement: per-call correlation token".
///
/// This type holds no state. `FoundryKitCore` is loaded once and shared by every
/// extension framework, so a stored property here would be shared across subsystems.
public enum CorrelationToken {

    /// Returns a fresh token. UUIDs are used because the value only needs to be
    /// unique within a process, not unguessable.
    public static func make() -> String {
        UUID().uuidString
    }

    /// Returns whether an emission carrying `actual` answers a request expecting
    /// `expected`.
    ///
    /// An empty `expected` means the caller opted out of correlation and accepts any
    /// emission — the behaviour that predates this protocol.
    public static func matches(_ expected: String, _ actual: String) -> Bool {
        if expected.isEmpty {
            return true
        }
        return expected == actual
    }
}
```

- [ ] **Step 5: Create the auth placeholder**

`Sources/FoundryKitAuth/Placeholder.swift` — this exists only so the target compiles before Task 3 adds real code. Task 3 deletes it.

```swift
import FoundryKitCore

/// Placeholder so `FoundryKitAuth` has a source file before the native class lands.
/// Deleted in the task that adds `GoogleSignIn.swift`.
enum FoundryKitAuthPlaceholder {
    static let correlationTokenIsAvailable = !CorrelationToken.make().isEmpty
}
```

- [ ] **Step 6: Run to verify it passes**

Run: `cd native/apple/FoundryKitNative && swift test`
Expected: PASS — 5 tests in `CorrelatedSignalTests`.

**If resolution against `0.1.0-alpha.2` fails**, stop and report the exact error. Do not fall back to `alpha.1` — validating that bump is one of this epic's four goals, and silently reverting it hides the finding.

- [ ] **Step 7: Add build output to `.gitignore`**

Append:

```gitignore
native/apple/FoundryKitNative/.build/
native/apple/FoundryKitNative/*.xcodeproj/
```

- [ ] **Step 8: Commit**

```bash
git add native/apple/FoundryKitNative .gitignore
git commit -m "feat(native): scaffold FoundryKitNative Swift package on alpha.2"
```

---

### Task 2: Correlation-token filtering in NativeRequest

**Goal:** Teach the merged `core/NativeRequest` to ignore emissions that do not answer the request it is awaiting — closing the misattribution gap the design spec requires, without changing behaviour for any existing caller.

**Files:**
- Modify: `addons/FoundryKit/core/NativeRequest.fs`
- Modify: `test_project/tests/support/fake_native.notest.fs`
- Modify: `test_project/tests/native-request.test.fs`

**Acceptance Criteria:**
- [ ] `await_outcome` accepts a `correlation_token: String` parameter defaulting to `""`
- [ ] With a **non-empty** token, an emission whose first payload field differs is **ignored without settling** — the request stays live
- [ ] With a non-empty token, a matching emission settles normally and the token is **not** included in the resulting payload
- [ ] With an **empty** token, behaviour is byte-for-byte today's: no filtering, no field consumed
- [ ] The failure signal is filtered by the same rule
- [ ] **Every pre-existing `NativeRequestTests` case still passes unchanged** — that is the compatibility proof
- [ ] A test proves the stale-emission scenario: settle request A by timeout, start B with a new token, emit A's late reply, confirm B does not settle

**Verify:** `task test:foundrylib` → `NativeRequestTests` pass, including 4 new correlation cases; the suite gains 4 tests

**Steps:**

- [ ] **Step 1: Extend the fake native to emit a leading token**

Modify `test_project/tests/support/fake_native.notest.fs`, keeping the existing signals and adding correlated ones:

```
## Emitted with a leading correlation token, matching the real native protocol.
signal correlated_success(request_token: String, first: String, second: String)
signal correlated_failed(request_token: String, code: int, message: String)

func emit_correlated_success(request_token: String, first: String, second: String) -> void:
	correlated_success.emit(request_token, first, second)

func emit_correlated_failure(request_token: String, code: int, message: String) -> void:
	correlated_failed.emit(request_token, code, message)

func correlated_success_connection_count() -> int:
	return correlated_success.get_connections().size()
```

- [ ] **Step 2: Write the failing tests**

Append to `test_project/tests/native-request.test.fs`:

```
const _CORRELATED_FIELDS: Array[String] = ["id_token", "email"]

func _start_correlated(token: String, timeout_seconds: float) -> Coroutine[NativeOutcome]:
	var request: NativeRequest = NativeRequest.new(_log)
	return request.await_outcome(
			_native, "correlated_success", _CORRELATED_FIELDS, "correlated_failed",
			timeout_seconds, token)

func test_matching_token_settles_and_token_is_not_in_payload() -> void:
	var pending: Coroutine[NativeOutcome] = _start_correlated("tok-a", 5.0)
	_native.emit_correlated_success("tok-a", "token-value", "user@example.com")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("ok:token-value:user@example.com")
	Expect.that(_payload_size(outcome)).to_equal(2)

func test_mismatched_token_does_not_settle_then_matching_one_does() -> void:
	var pending: Coroutine[NativeOutcome] = _start_correlated("tok-b", 5.0)
	# A late reply from a previous request must be ignored entirely.
	_native.emit_correlated_success("tok-stale", "wrong", "wrong@example.com")
	_native.emit_correlated_success("tok-b", "right", "right@example.com")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("ok:right:right@example.com")

func test_mismatched_failure_token_is_ignored() -> void:
	var pending: Coroutine[NativeOutcome] = _start_correlated("tok-c", 5.0)
	_native.emit_correlated_failure("tok-stale", 4, "not mine")
	_native.emit_correlated_failure("tok-c", 7, "mine")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("fail:7:mine")

func test_empty_token_accepts_any_emission() -> void:
	var request: NativeRequest = NativeRequest.new(_log)
	var pending: Coroutine[NativeOutcome] = request.await_outcome(
			_native, "operation_success", _FIELDS, "operation_failed", 5.0, "")
	_native.emit_success("token-value", "user@example.com")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("ok:token-value:user@example.com")
```

- [ ] **Step 3: Run to verify they fail**

Run: `task test:foundrylib`
Expected: FAIL — `await_outcome` takes five arguments, not six.

- [ ] **Step 4: Modify `addons/FoundryKit/core/NativeRequest.fs`**

First **replace the class doc comment's final paragraph**, which today explicitly
disclaims correlation and is now wrong:

```
## Each request may carry a per-call correlation token. When it does, the native echoes
## that token as the **first** argument of both its success and failure signals, and this
## adapter ignores any emission carrying a different one. That closes the window where a
## timed-out or [method abandon]ed request's late reply is mistaken for the result of a
## later request connected to the same target and signal names. A request started with an
## empty token opts out and accepts any emission, which is the correct behaviour for a
## native that predates the token protocol.
```

Add a field beside the existing ones:

```
var _correlation_token: String = ""
```

Extend the signature and store the token — everything else in `await_outcome` is unchanged:

```
async func await_outcome(
		target: Object,
		success_signal: String,
		payload_fields: Array[String],
		failure_signal: String,
		timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS,
		correlation_token: String = "") -> NativeOutcome:
	_correlation_token = correlation_token
```

Replace the two handlers:

```
## Accepts an emission only when it answers this request.
##
## With a correlation token, the native echoes it as the **first** signal argument;
## a non-matching emission belongs to an earlier request that timed out or was
## abandoned, and must be ignored without settling — settling on it would hand one
## request's result to another.
func _on_native_success(...values: Array) -> void:
	var offset: int = 0
	if not _correlation_token.is_empty():
		if values.is_empty():
			return
		if str(values[0]) != _correlation_token:
			_log.debug("ignoring success emission for a different request")
			return
		offset = 1
	var payload: Dictionary[String, Variant] = {}
	var available: int = values.size() - offset
	var count: int = mini(_payload_fields.size(), available)
	for index: int in range(count):
		payload[_payload_fields[index]] = values[index + offset]
	_resolve(NativeOutcome.Succeeded(payload))

func _on_native_failed(...values: Array) -> void:
	var offset: int = 0
	if not _correlation_token.is_empty():
		if values.is_empty():
			return
		if str(values[0]) != _correlation_token:
			_log.debug("ignoring failure emission for a different request")
			return
		offset = 1
	if values.size() < offset + 2:
		return
	var code: int = 0
	var raw_code: Variant = values[offset]
	if raw_code is int:
		code = raw_code
	_resolve(NativeOutcome.Failed(code, str(values[offset + 1])))
```

Note `_on_native_failed` becomes a rest-parameter handler so it can absorb the leading token. Rest parameters must stay **untyped** (`...values: Array`) — a typed array is rejected. `raw_code is int` narrowing avoids `int(some_variant)`, which the strict warnings reject.

- [ ] **Step 5: Run to verify they pass**

Run: `task test:foundrylib`
Expected: PASS — 4 new tests. Confirm the **pre-existing** `NativeRequestTests` still pass; if any changed behaviour, the default-empty path is wrong and must be fixed rather than the test adjusted.

- [ ] **Step 6: Commit**

```bash
task uids
git add addons/FoundryKit/core/NativeRequest.fs test_project/tests/
git commit -m "feat(core): correlate native emissions to their originating request"
```

---

### Task 3: The Google Sign-In native class

**Goal:** The Swift class that drives GoogleSignIn and emits correlated results — the first real native in FoundryKit.

**Files:**
- Create: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/SignInOutcome.swift`
- Create: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/GoogleSignIn.swift`
- Create: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/FoundryKitAuthEntry.swift`
- Create: `native/apple/FoundryKitNative/Tests/FoundryKitAuthTests/SignInOutcomeTests.swift`
- Delete: `native/apple/FoundryKitNative/Sources/FoundryKitAuth/Placeholder.swift`
- Modify: `native/apple/FoundryKitNative/Package.swift` (add GoogleSignIn dependency and the auth test target)

**Acceptance Criteria:**
- [ ] `signIn(nonce:requestToken:)` and `signInSilent(requestToken:)` take a correlation token
- [ ] `signInSuccess` emits `(request_token, id_token, email, display_name, authorization_code)` — token **first**
- [ ] `signInFailed` emits `(request_token, code, message)` — token **first**
- [ ] The pure mapping functions (`extractOutcome`, `validatedClientID`, `reversedClientID`) are `internal` free functions, unit-tested **without a host bundle**
- [ ] `#initFoundryExtension` uses cdecl `foundry_kit_auth_entry_point`
- [ ] No diagnostic detail from the provider reaches the emitted `message` — only fixed, non-identifying text
- [ ] `swift test` passes

**Verify:** `cd native/apple/FoundryKitNative && swift test` → `SignInOutcomeTests` pass

**Steps:**

- [ ] **Step 1: Add GoogleSignIn to `Package.swift`**

Add to `dependencies`:

```swift
        .package(url: "https://github.com/google/GoogleSignIn-iOS", exact: "9.1.0"),
```

Add to the `FoundryKitAuth` target's dependencies:

```swift
                .product(name: "GoogleSignIn", package: "GoogleSignIn-iOS"),
```

Add a test target:

```swift
        .testTarget(
            name: "FoundryKitAuthTests",
            dependencies: ["FoundryKitAuth"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
```

- [ ] **Step 2: Write the failing test**

Create `Tests/FoundryKitAuthTests/SignInOutcomeTests.swift`:

```swift
import XCTest
@testable import FoundryKitAuth

final class SignInOutcomeTests: XCTestCase {

    func testEmptyClientIDIsRejected() {
        XCTAssertNil(validatedClientID(nil))
        XCTAssertNil(validatedClientID(""))
    }

    func testNonEmptyClientIDIsAccepted() {
        XCTAssertEqual(validatedClientID("abc.apps.googleusercontent.com"),
                       "abc.apps.googleusercontent.com")
    }

    func testReversedClientIDReversesDotSeparatedComponents() {
        XCTAssertEqual(reversedClientID("123-abc.apps.googleusercontent.com"),
                       "com.googleusercontent.apps.123-abc")
    }

    func testOutcomeCarriesTokenAndProfile() {
        let outcome = SignInOutcome.success(
            idToken: "id", email: "a@b.c", displayName: "Ada")
        guard case let .success(idToken, email, displayName) = outcome else {
            return XCTFail("expected success")
        }
        XCTAssertEqual(idToken, "id")
        XCTAssertEqual(email, "a@b.c")
        XCTAssertEqual(displayName, "Ada")
    }

    func testFailureMessagesAreFixedAndNonIdentifying() {
        // Provider diagnostics must never reach the emitted message — they can
        // carry account identifiers. Only fixed strings are emitted.
        guard case let .failure(code, message) = SignInOutcome.cancelled else {
            return XCTFail("expected failure")
        }
        XCTAssertEqual(code, errorCancelled)
        XCTAssertEqual(message, "The player cancelled the sign-in flow.")
    }
}
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd native/apple/FoundryKitNative && swift test`
Expected: FAIL — `SignInOutcome`, `validatedClientID`, `reversedClientID` are not defined.

- [ ] **Step 4: Create `Sources/FoundryKitAuth/SignInOutcome.swift`**

```swift
import Foundation
import GoogleSignIn

/// Error codes mirrored by `AuthError.from_native` on the script side.
let errorCancelled = 0
let errorNoCredential = 1
let errorUnavailable = 2
let errorGeneric = 3

/// A resolved sign-in outcome, safe to send across an actor boundary.
///
/// Every field is extracted inside the GoogleSignIn completion handler before any
/// actor hop, so the value carries no reference to SDK types.
enum SignInOutcome: Sendable {
    case success(idToken: String, email: String, displayName: String)
    case failure(code: Int, message: String)

    static let cancelled = SignInOutcome.failure(
        code: errorCancelled, message: "The player cancelled the sign-in flow.")
    static let noCredential = SignInOutcome.failure(
        code: errorNoCredential, message: "Sign-in returned no credential.")
    static let generic = SignInOutcome.failure(
        code: errorGeneric, message: "Sign-in failed.")
}

/// Converts a GoogleSignIn completion payload into an outcome.
///
/// A free function with no actor isolation so it can be called synchronously from
/// completion handlers that arrive on any thread.
func extractOutcome(user: GIDGoogleUser?, error: Error?) -> SignInOutcome {
    if let user {
        guard let idToken = user.idToken?.tokenString else {
            return .noCredential
        }
        return .success(
            idToken: idToken,
            email: user.profile?.email ?? "",
            displayName: user.profile?.name ?? "")
    }
    guard let error else {
        return .generic
    }
    let nsError = error as NSError
    let isCancelled = nsError.domain == kGIDSignInErrorDomain
        && nsError.code == GIDSignInError.Code.canceled.rawValue
    return isCancelled ? .cancelled : .generic
}

/// Returns `raw` when it is a non-empty client ID, otherwise nil.
func validatedClientID(_ raw: String?) -> String? {
    guard let raw, !raw.isEmpty else { return nil }
    return raw
}

/// Computes the reversed-client-ID URL scheme GoogleSignIn expects in the host
/// app's Info.plist.
func reversedClientID(_ iosClientID: String) -> String {
    iosClientID.split(separator: ".").reversed().joined(separator: ".")
}
```

- [ ] **Step 5: Create `Sources/FoundryKitAuth/GoogleSignIn.swift`**

```swift
import FoundryKitCore
import FoundrySwift
import GoogleSignIn
import OSLog

#if canImport(UIKit)
    import UIKit
#elseif canImport(AppKit)
    import AppKit
#endif

/// Drives GoogleSignIn and reports results to `AppleAuthBackend.fs`.
///
/// Every emission carries the correlation token its request was started with, as the
/// **first** signal argument, so `NativeRequest` can tell whether a reply answers the
/// request it is awaiting. Without it, a late reply from a timed-out request can be
/// misattributed to the next one.
///
/// GoogleSignIn invokes completion handlers on the main thread, so signals are emitted
/// directly from them.
@Foundry
class iOSGoogleSignIn: RefCounted {

    @Signal("request_token", "id_token", "email", "display_name", "authorization_code")
    var signInSuccess: SignalWithArguments<String, String, String, String, String>

    @Signal("request_token", "code", "message")
    var signInFailed: SignalWithArguments<String, Int, String>

    @Signal("request_token")
    var signOutComplete: SignalWithArguments<String>

    private static let logger = Logger(
        subsystem: "games.cafecito.foundrykit", category: "auth.google")

    /// Written and read only on the main thread — Foundry drives every `@Callable`
    /// there, and GoogleSignIn hops to the main actor before reading them.
    private var debugLogging = false
    private var isConfigured = false

    /// Configures GoogleSignIn. The iOS/macOS client ID comes from the host app's
    /// `Info.plist` (`GIDClientID`); `webClientId` becomes the `serverClientID` so the
    /// returned ID token is minted for the game's backend.
    ///
    /// An empty `webClientId` or a missing `GIDClientID` leaves the extension
    /// unconfigured, so `isAvailable()` reports false and no sign-in is attempted.
    @Callable
    func initialize(webClientId: String) {
        guard let iosClientID = validatedClientID(Self.infoPlistClientID()),
              !webClientId.isEmpty
        else {
            isConfigured = false
            Self.logger.error("Google Sign-In is not configured; sign-in is unavailable.")
            return
        }
        GIDSignIn.sharedInstance.configuration = GIDConfiguration(
            clientID: iosClientID, serverClientID: webClientId)
        isConfigured = true
        logDebug("configured with serverClientID")
    }

    @Callable
    func isAvailable() -> Bool { isConfigured }

    @Callable
    func setDebugLogging(_ enabled: Bool) { debugLogging = enabled }

    @Callable
    func signIn(nonce: String, requestToken: String) {
        guard isConfigured, let anchor = Self.presentingAnchor() else {
            emit(.failure(code: errorUnavailable, message: "Sign-in is unavailable."),
                 requestToken: requestToken)
            return
        }
        GIDSignIn.sharedInstance.signIn(withPresenting: anchor, hint: nil, additionalScopes: nil, nonce: nonce) {
            [weak self] result, error in
            self?.emit(extractOutcome(user: result?.user, error: error),
                       requestToken: requestToken)
        }
    }

    @Callable
    func signInSilent(requestToken: String) {
        guard isConfigured else {
            emit(.failure(code: errorUnavailable, message: "Sign-in is unavailable."),
                 requestToken: requestToken)
            return
        }
        GIDSignIn.sharedInstance.restorePreviousSignIn { [weak self] user, error in
            self?.emit(extractOutcome(user: user, error: error), requestToken: requestToken)
        }
    }

    @Callable
    func signOut(requestToken: String) {
        GIDSignIn.sharedInstance.signOut()
        signOutComplete.emit(requestToken)
    }

    private func emit(_ outcome: SignInOutcome, requestToken: String) {
        switch outcome {
        case .success(let idToken, let email, let displayName):
            logDebug("sign_in_success (id_token len=\(idToken.count))")
            signInSuccess.emit(requestToken, idToken, email, displayName, "")
        case .failure(let code, let message):
            Self.logger.error("sign-in failed (code=\(code))")
            signInFailed.emit(requestToken, code, message)
        }
    }

    private func logDebug(_ message: String) {
        if debugLogging {
            Self.logger.notice("\(message, privacy: .public)")
        }
    }

    private static func infoPlistClientID() -> String? {
        Bundle.main.object(forInfoDictionaryKey: "GIDClientID") as? String
    }

    #if canImport(UIKit)
        private static func presentingAnchor() -> UIViewController? {
            let scene = UIApplication.shared.connectedScenes
                .compactMap { $0 as? UIWindowScene }
                .first { $0.activationState == .foregroundActive }
            return scene?.keyWindow?.rootViewController
        }
    #elseif canImport(AppKit)
        private static func presentingAnchor() -> NSWindow? {
            NSApplication.shared.keyWindow ?? NSApplication.shared.windows.first
        }
    #endif
}
```

- [ ] **Step 6: Create the entry point**

`Sources/FoundryKitAuth/FoundryKitAuthEntry.swift`:

```swift
import FoundrySwift

/// Registers the auth subsystem's native classes.
///
/// One entry symbol per subsystem binary — this is what
/// `FoundryKitAuth.foundryextension` names in its `entry_symbol`.
#initFoundryExtension(
    cdecl: "foundry_kit_auth_entry_point",
    types: [
        iOSGoogleSignIn.self,
    ]
)
```

- [ ] **Step 7: Delete the placeholder and run tests**

```bash
rm native/apple/FoundryKitNative/Sources/FoundryKitAuth/Placeholder.swift
cd native/apple/FoundryKitNative && swift test
```
Expected: PASS — `CorrelatedSignalTests` and `SignInOutcomeTests`.

If the macros require a `-load-plugin-executable` flag that plain `swift test` cannot supply, report it — the legacy `project.yml` passed one via `OTHER_SWIFT_FLAGS`, and that constraint decides whether `swift test` or `xcodebuild test` is the CI entry point.

- [ ] **Step 8: Commit**

```bash
git add native/apple/FoundryKitNative
git commit -m "feat(native): add correlated Google Sign-In native class"
```

---

### Task 4: xcframework build

**Goal:** A reproducible build producing `FoundryKitAuth.xcframework` and `FoundryKitCore.framework`, driven by a `task` target.

**Files:**
- Create: `native/apple/FoundryKitNative/project.yml`
- Create: `scripts/build-apple-auth`
- Modify: `Taskfile.yml`
- Modify: `.gitignore`

**Acceptance Criteria:**
- [ ] `task apple:auth` produces `addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework` with device **and** simulator slices, plus `bin/core/FoundryKitCore.framework`
- [ ] Binary outputs are gitignored — they are release artefacts, not source
- [ ] The script fails loudly with a named missing tool rather than a cryptic xcodebuild error
- [ ] `BUILD_LIBRARY_FOR_DISTRIBUTION` is set, as the legacy project did
- [ ] `LD_RUNPATH_SEARCH_PATHS` lets `FoundryKitAuth` find both `FoundrySwift` and `FoundryKitCore` at load time
- [ ] The script is idempotent — running it twice leaves the same tree

**Verify:** `task apple:auth` → both binaries exist; `ls addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework` succeeds

**Steps:**

- [ ] **Step 1: Create `native/apple/FoundryKitNative/project.yml`**

```yaml
name: FoundryKitNative
options:
  bundleIdPrefix: games.cafecito.foundrykit
  deploymentTarget:
    iOS: "17.0"
    macOS: "14.0"
packages:
  FoundrySwiftBinary:
    url: https://github.com/cafecito-games/Foundry-Swift-Binary.git
    exactVersion: 0.1.0-alpha.2
  GoogleSignIn:
    url: https://github.com/google/GoogleSignIn-iOS
    exactVersion: 9.1.0
targets:
  FoundryKitCore:
    type: framework
    platform: [iOS, macOS]
    sources: [Sources/FoundryKitCore]
    settings:
      base: &frameworkSettings
        SWIFT_VERSION: "6.0"
        BUILD_LIBRARY_FOR_DISTRIBUTION: YES
        SKIP_INSTALL: NO
        DEFINES_MODULE: YES
        GENERATE_INFOPLIST_FILE: YES
        # @loader_path is the .framework directory. The trailing entry reaches the
        # conventional addons/FoundrySwift/ install location when the editor loads
        # the extension from addons/FoundryKit/bin/macos_arm64/.
        LD_RUNPATH_SEARCH_PATHS: "@executable_path/Frameworks @loader_path/../ @loader_path/../../../ @loader_path/../../../../FoundrySwift/bin/macos_arm64"
    dependencies:
      - package: FoundrySwiftBinary
        product: FoundrySwift
  FoundryKitAuth:
    type: framework
    platform: [iOS, macOS]
    sources: [Sources/FoundryKitAuth]
    settings:
      base: *frameworkSettings
    dependencies:
      - target: FoundryKitCore
      - package: FoundrySwiftBinary
        product: FoundrySwift
      - package: GoogleSignIn
        product: GoogleSignIn
schemes:
  FoundryKitAuth_iOS:
    build:
      targets: { FoundryKitAuth_iOS: all }
  FoundryKitAuth_macOS:
    build:
      targets: { FoundryKitAuth_macOS: all }
```

- [ ] **Step 2: Create `scripts/build-apple-auth`**

```bash
#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
package_dir="$repo_root/native/apple/FoundryKitNative"
derived_data="$repo_root/.build/xcodebuild"
auth_bin="$repo_root/addons/FoundryKit/bin/auth"
core_bin="$repo_root/addons/FoundryKit/bin/core"

fail() { echo "ERROR: $*" >&2; exit 1; }

for tool in xcodegen xcodebuild; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool is required. Install with: brew install $tool"
done

cd "$package_dir"
xcodegen generate

build() {
    xcodebuild build \
        -scheme "$1" \
        -destination "$2" \
        -configuration Release \
        -derivedDataPath "$derived_data" \
        -skipPackagePluginValidation -skipMacroValidation \
        ${3:-}
}

build FoundryKitAuth_iOS "generic/platform=iOS"
build FoundryKitAuth_iOS "generic/platform=iOS Simulator" "ARCHS=arm64 CODE_SIGNING_ALLOWED=NO"
build FoundryKitAuth_macOS "generic/platform=macOS" "ARCHS=arm64"

mkdir -p "$auth_bin" "$core_bin"
rm -rf "$auth_bin"/*.xcframework "$core_bin"/*.framework

xcodebuild -create-xcframework \
    -framework "$derived_data/Build/Products/Release-iphoneos/FoundryKitAuth.framework" \
    -framework "$derived_data/Build/Products/Release-iphonesimulator/FoundryKitAuth.framework" \
    -output "$auth_bin/FoundryKitAuth.xcframework"

cp -R "$derived_data/Build/Products/Release/FoundryKitCore.framework" "$core_bin/"

echo "PASS: built FoundryKitAuth.xcframework and FoundryKitCore.framework"
```

Then `chmod +x scripts/build-apple-auth`.

- [ ] **Step 3: Add the task target**

In `Taskfile.yml`:

```yaml
  apple:auth:
    desc: Build the Apple auth xcframework and shared core framework
    cmds:
      - scripts/build-apple-auth
```

- [ ] **Step 4: Gitignore the binaries**

Append to `.gitignore`:

```gitignore
addons/FoundryKit/bin/auth/
addons/FoundryKit/bin/core/
```

- [ ] **Step 5: Build and verify idempotence**

```bash
task apple:auth
ls addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework
ls addons/FoundryKit/bin/core/FoundryKitCore.framework
task apple:auth
git status --porcelain
```
Expected: both paths exist after the first run; `git status` clean after the second.

- [ ] **Step 6: Commit**

```bash
git add native/apple/FoundryKitNative/project.yml scripts/build-apple-auth Taskfile.yml .gitignore
git commit -m "build(native): add Apple auth xcframework build"
```

---

### Task 5: Extension registration

**Goal:** Register the auth binary with the engine so `iOSGoogleSignIn` appears in `ClassDB`.

**Files:**
- Create: `addons/FoundryKit/FoundryKitAuth.foundryextension`
- Create: `addons/FoundryKit/bin/auth/.gitkeep`, `addons/FoundryKit/bin/core/.gitkeep`

**Acceptance Criteria:**
- [ ] `entry_symbol` is `foundry_kit_auth_entry_point`, matching the `#initFoundryExtension` cdecl exactly
- [ ] `[libraries]` covers iOS device, iOS simulator and macOS arm64
- [ ] `[dependencies]` lists `FoundryKitCore.framework` for every slice — it is a shared dependency, not an extension
- [ ] `.gitkeep` files keep the bin directories present despite the gitignore
- [ ] With binaries **absent**, `task test:foundrylib` still passes — proving a partial install degrades rather than breaks

**Verify:** `task test:foundrylib` → the full suite still passes with no binaries present

**Steps:**

- [ ] **Step 1: Create `addons/FoundryKit/FoundryKitAuth.foundryextension`**

```ini
[configuration]
entry_symbol = "foundry_kit_auth_entry_point"
compatibility_minimum = "0.1.0"

[libraries]
ios.debug             = "res://addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework"
ios.release           = "res://addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework"
ios.simulator.debug   = "res://addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework"
ios.simulator.release = "res://addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework"
macos.arm64           = "res://addons/FoundryKit/bin/auth/FoundryKitAuth.xcframework"

[dependencies]
ios.debug             = { "res://addons/FoundryKit/bin/core/FoundryKitCore.framework": "" }
ios.release           = { "res://addons/FoundryKit/bin/core/FoundryKitCore.framework": "" }
ios.simulator.debug   = { "res://addons/FoundryKit/bin/core/FoundryKitCore.framework": "" }
ios.simulator.release = { "res://addons/FoundryKit/bin/core/FoundryKitCore.framework": "" }
macos.arm64           = { "res://addons/FoundryKit/bin/core/FoundryKitCore.framework": "" }
```

- [ ] **Step 2: Keep the bin directories**

```bash
mkdir -p addons/FoundryKit/bin/auth addons/FoundryKit/bin/core
touch addons/FoundryKit/bin/auth/.gitkeep addons/FoundryKit/bin/core/.gitkeep
git add -f addons/FoundryKit/bin/auth/.gitkeep addons/FoundryKit/bin/core/.gitkeep
```

- [ ] **Step 3: Confirm the absent-binary path still works**

`scripts/test-foundrylib` already excludes `*.foundryextension` and `bin/` when it rsyncs the addon into `test_project`, so the headless suite runs without binaries by construction. Verify that assumption holds rather than trusting it:

```bash
grep -n "exclude" scripts/test-foundrylib
task test:foundrylib
```
Expected: the excludes are present; the full suite passes.

- [ ] **Step 4: Commit**

```bash
git add addons/FoundryKit/FoundryKitAuth.foundryextension addons/FoundryKit/bin
git commit -m "feat(native): register the auth extension"
```

---

### Task 6: AppleAuthBackend

**Goal:** The Foundry Script backend that drives the native class through `NativeRequest` and returns a `Credential` — closing the loop from native to the merged auth layer.

**Files:**
- Create: `addons/FoundryKit/auth/internal/AppleAuthBackend.fs`
- Create: `test_project/tests/auth-apple-backend.test.fs`
- Create: `test_project/tests/support/fake_google_native.notest.fs`

**Acceptance Criteria:**
- [ ] Satisfies `AuthBackend` via `uses`
- [ ] `sign_in(Provider.GOOGLE)` generates a **fresh correlation token per call**, passes it to the native, and awaits through `NativeRequest` with that token
- [ ] A success emission maps to `CredentialResult.Success(Credential.Google(...))`
- [ ] A failure emission maps through `AuthError.from_native`
- [ ] `Provider.APPLE` and `Provider.EMAIL_PASSWORD` return `Unavailable` — **this backend is Google-only in this epic**
- [ ] With the native class absent, `is_available` is false and `sign_in` returns `Unavailable` without hanging
- [ ] Session storage operations return `Storage` — Keychain is epic E
- [ ] `backend_name()` returns `"apple"`

**Verify:** `task test:foundrylib` → 8 tests in `AppleAuthBackendTests` pass

**Steps:**

- [ ] **Step 1: Create the fake native**

`test_project/tests/support/fake_google_native.notest.fs`:

```
namespace games.cafecito.foundrykit.tests.support

## Stands in for the `iOSGoogleSignIn` native class.
##
## Mirrors the real signal shape exactly, including the leading correlation token,
## so the backend is exercised against the protocol it will meet in production.
class_name FakeGoogleNative extends Object

signal sign_in_success(request_token: String, id_token: String, email: String,
		display_name: String, authorization_code: String)
signal sign_in_failed(request_token: String, code: int, message: String)
signal sign_out_complete(request_token: String)

var last_request_token: String = ""
var last_nonce: String = ""
var configured_web_client_id: String = ""

func initialize(web_client_id: String) -> void:
	configured_web_client_id = web_client_id

func isAvailable() -> bool:
	return not configured_web_client_id.is_empty()

func setDebugLogging(_enabled: bool) -> void:
	pass

func signIn(nonce: String, request_token: String) -> void:
	last_nonce = nonce
	last_request_token = request_token

func signInSilent(request_token: String) -> void:
	last_request_token = request_token

func signOut(request_token: String) -> void:
	sign_out_complete.emit(request_token)

func emit_success(request_token: String, id_token: String, email: String,
		display_name: String) -> void:
	sign_in_success.emit(request_token, id_token, email, display_name, "")

func emit_failure(request_token: String, code: int, message: String) -> void:
	sign_in_failed.emit(request_token, code, message)
```

- [ ] **Step 2: Write the failing test**

`test_project/tests/auth-apple-backend.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.auth.internal
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

class_name AppleAuthBackendTests
extends RefCounted
uses Test

const _TOKEN: String = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c2VyLTEyMyIsImV4cCI6MTc1MDAwMDAwMH0.sig"

var _native: FakeGoogleNative
var _backend: AppleAuthBackend

func before_each() -> void:
	_native = FakeGoogleNative.new()
	_backend = AppleAuthBackend.new(FoundryKitLog.new("test"), _native)

func after_each() -> void:
	_native.free()

func _describe(result: CredentialResult) -> String:
	match result:
		CredentialResult.Success(credential):
			return "ok:%s:%s" % [_provider_name(Credential.provider_of(credential)),
					Credential.subject_of(credential)]
		CredentialResult.Failure(error):
			return "fail:%s" % _error_name(error)
	return "unreachable"

func _provider_name(provider: Provider) -> String:
	match provider:
		Provider.GOOGLE: return "google"
		Provider.APPLE: return "apple"
		Provider.EMAIL_PASSWORD: return "email_password"
	return "unreachable"

func _error_name(error: AuthError) -> String:
	match error:
		AuthError.Cancelled: return "cancelled"
		AuthError.NoCredential: return "no_credential"
		AuthError.Unavailable(_p): return "unavailable"
		AuthError.Configuration(_d): return "configuration"
		AuthError.Storage(_d): return "storage"
		AuthError.RequestFailed(_s, _b): return "request_failed"
		AuthError.InvalidResponse(_d): return "invalid_response"
		AuthError.MissingField(_f): return "missing_field"
		AuthError.SessionExpired(_e): return "session_expired"
		AuthError.TimedOut(_t): return "timed_out"
	return "unreachable"

func test_backend_name() -> void:
	Expect.that(_backend.backend_name()).to_equal("apple")

func test_unconfigured_is_not_available() -> void:
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_false()

func test_configure_passes_web_client_id_to_the_native() -> void:
	_backend.configure(ProviderConfig.Google("web-id", "ios-id", "desktop-id"))
	Expect.that(_native.configured_web_client_id).to_equal("web-id")
	Expect.that(_backend.is_configured(Provider.GOOGLE)).to_be_true()

func test_sign_in_success_yields_a_google_credential() -> void:
	_backend.configure(ProviderConfig.Google("web-id", "ios-id", "desktop-id"))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_success(_native.last_request_token, _TOKEN, "a@b.c", "Ada")
	Expect.that(_describe(await pending)).to_equal("ok:google:user-123")

func test_each_sign_in_uses_a_fresh_correlation_token() -> void:
	_backend.configure(ProviderConfig.Google("web-id", "ios-id", "desktop-id"))
	var first_pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var first_token: String = _native.last_request_token
	_native.emit_success(first_token, _TOKEN, "a@b.c", "Ada")
	await first_pending
	var second_pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	var second_token: String = _native.last_request_token
	_native.emit_success(second_token, _TOKEN, "a@b.c", "Ada")
	await second_pending
	Expect.that(first_token != second_token).to_be_true()
	Expect.that(first_token.is_empty()).to_be_false()

func test_native_failure_maps_through_auth_error() -> void:
	_backend.configure(ProviderConfig.Google("web-id", "ios-id", "desktop-id"))
	var pending: Coroutine[CredentialResult] = _backend.sign_in(Provider.GOOGLE)
	_native.emit_failure(_native.last_request_token, 4, "boom")
	Expect.that(_describe(await pending)).to_equal("fail:request_failed")

func test_apple_and_email_providers_are_unavailable_in_this_epic() -> void:
	_backend.configure(ProviderConfig.Google("web-id", "ios-id", "desktop-id"))
	Expect.that(_describe(await _backend.sign_in(Provider.APPLE))).to_equal("fail:unavailable")
	Expect.that(_describe(await _backend.sign_in(Provider.EMAIL_PASSWORD))) \
		.to_equal("fail:unavailable")

func test_storage_is_unavailable_until_keychain_lands() -> void:
	var stored: CompletionResult = await _backend.store_session(
			AuthSession.new("a", "r", Provider.GOOGLE, {}, {}))
	var described: String = ""
	match stored:
		CompletionResult.Success:
			described = "ok"
		CompletionResult.Failure(error):
			described = _error_name(error)
	Expect.that(described).to_equal("storage")
```

- [ ] **Step 3: Run to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `AppleAuthBackend` is not defined.

- [ ] **Step 4: Create `addons/FoundryKit/auth/internal/AppleAuthBackend.fs`**

```
namespace games.cafecito.foundrykit.auth.internal

import games.cafecito.foundrykit.auth
import games.cafecito.foundrykit.core

## Google Sign-In on Apple platforms.
##
## Drives the `iOSGoogleSignIn` native class through [NativeRequest], generating a
## fresh correlation token for every call so a late reply from an abandoned request
## can never be mistaken for the current one.
##
## Google only in this epic. Sign in with Apple and Keychain-backed session storage
## are a later epic; both report unavailable here rather than pretending to work.
class_name AppleAuthBackend extends RefCounted
uses AuthBackend

const _GOOGLE_NATIVE_CLASS: String = "iOSGoogleSignIn"
const _SUCCESS_SIGNAL: String = "sign_in_success"
const _FAILURE_SIGNAL: String = "sign_in_failed"
const _PAYLOAD_FIELDS: Array[String] = ["id_token", "email", "display_name",
		"authorization_code"]
const _STORAGE_DETAIL: String = "secure session storage is not implemented on this backend yet"

var _log: FoundryKitLog
var _native: Object? = null

## [param native_override] lets tests inject a fake; production passes null so the
## backend probes ClassDB itself.
func _init(log: FoundryKitLog, native_override: Object? = null) -> void:
	_log = log
	if native_override != null:
		_native = native_override
		return
	var bridge: NativeBridge = NativeBridge.new(log)
	_native = bridge.instantiate(_GOOGLE_NATIVE_CLASS)

func backend_name() -> String:
	return "apple"

func configure(config: ProviderConfig) -> void:
	match config:
		ProviderConfig.Google(web_client_id, _ios_client_id, _desktop_client_id):
			var native: Object? = _native
			if native == null:
				_log.debug("configure(Google) ignored: native class absent")
				return
			var target: Object = native
			target.call("initialize", web_client_id)
		ProviderConfig.Apple(_service_id, _redirect_uri):
			_log.debug("configure(Apple) ignored: not supported by this backend yet")
		ProviderConfig.EmailPassword:
			_log.debug("configure(EmailPassword) ignored: not a native provider")

func is_available(provider: Provider) -> bool:
	match provider:
		Provider.GOOGLE:
			return _native != null
		Provider.APPLE, Provider.EMAIL_PASSWORD:
			return false
	return false

func is_configured(provider: Provider) -> bool:
	match provider:
		Provider.GOOGLE:
			var native: Object? = _native
			if native == null:
				return false
			var target: Object = native
			return target.call("isAvailable") == true
		Provider.APPLE, Provider.EMAIL_PASSWORD:
			return false
	return false

async func sign_in(provider: Provider) -> CredentialResult:
	return await _run_sign_in(provider, false)

async func sign_in_silent(provider: Provider) -> CredentialResult:
	return await _run_sign_in(provider, true)

async func sign_out(provider: Provider) -> CompletionResult:
	var native: Object? = _native
	if native == null or provider != Provider.GOOGLE:
		return CompletionResult.Success
	var target: Object = native
	target.call("signOut", _new_token())
	return CompletionResult.Success

async func store_session(_session: AuthSession) -> CompletionResult:
	return CompletionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

async func restore_session() -> SessionResult:
	return SessionResult.Failure(AuthError.Storage(_STORAGE_DETAIL))

func has_stored_session() -> bool:
	return false

async func clear_stored_session() -> CompletionResult:
	return CompletionResult.Success

## Generates a per-call correlation token.
##
## Uniqueness within the process is all that is required — the token distinguishes
## concurrent or overlapping requests, it is not a security boundary.
func _new_token() -> String:
	return "%d-%d" % [Time.get_ticks_usec(), randi()]

async func _run_sign_in(provider: Provider, silent: bool) -> CredentialResult:
	if provider != Provider.GOOGLE:
		return CredentialResult.Failure(AuthError.Unavailable(provider))
	var native: Object? = _native
	if native == null:
		return CredentialResult.Failure(AuthError.Unavailable(provider))
	var target: Object = native

	var token: String = _new_token()
	var request: NativeRequest = NativeRequest.new(_log)
	var pending: Coroutine[NativeOutcome] = request.await_outcome(
			target, _SUCCESS_SIGNAL, _PAYLOAD_FIELDS, _FAILURE_SIGNAL,
			NativeRequest.DEFAULT_TIMEOUT_SECONDS, token)

	if silent:
		target.call("signInSilent", token)
	else:
		target.call("signIn", "", token)

	var outcome: NativeOutcome = await pending
	match outcome:
		NativeOutcome.Succeeded(payload):
			return _credential_from(payload)
		NativeOutcome.Failed(code, message):
			return CredentialResult.Failure(
					AuthError.from_native(NativeOutcome.Failed(code, message), provider))
		NativeOutcome.TimedOut(elapsed_seconds):
			return CredentialResult.Failure(AuthError.TimedOut(elapsed_seconds))
		NativeOutcome.Abandoned:
			return CredentialResult.Failure(AuthError.Cancelled)
		NativeOutcome.Unavailable(_missing_class):
			return CredentialResult.Failure(AuthError.Unavailable(provider))
	return CredentialResult.Failure(AuthError.Unavailable(provider))

func _credential_from(payload: Dictionary[String, Variant]) -> CredentialResult:
	var id_token: String = str(payload.get("id_token", ""))
	if id_token.is_empty():
		return CredentialResult.Failure(AuthError.MissingField("id_token"))
	return CredentialResult.Success(Credential.Google(
			id_token,
			str(payload.get("email", "")),
			str(payload.get("display_name", "")),
			""))
```

Note the `audience` argument is empty: the native does not return it, and inventing one would be worse than leaving it blank for epic C to populate from the configured web client ID.

- [ ] **Step 5: Run to verify it passes**

Run: `task uids && task test:foundrylib`
Expected: PASS — 8 tests in `AppleAuthBackendTests`.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/auth/internal/AppleAuthBackend.fs test_project/tests/
git commit -m "feat(auth): add Apple backend for Google sign-in"
```

---

### Task 7: Wire the factory

**Goal:** Route iOS and macOS to the Apple backend, so `FoundryKit.auth` uses it on those platforms and the Null backend everywhere else.

**Files:**
- Modify: `addons/FoundryKit/auth/internal/AuthBackendFactory.fs`
- Modify: `test_project/tests/auth-backend-factory.test.fs`

**Acceptance Criteria:**
- [ ] `PlatformKind.IOS` and `PlatformKind.MACOS` resolve to `AppleAuthBackend`
- [ ] `ANDROID`, `DESKTOP` and `UNKNOWN` still resolve to `NullAuthBackend`
- [ ] The comment naming which epic changes each remaining platform is updated, not deleted
- [ ] On a machine **without** the native binary, the Apple backend still constructs and reports unavailable — resolution must not depend on the binary being present
- [ ] **`test_resolve_current_returns_a_backend` no longer asserts `"null"`.** It currently does, and the headless suite runs on macOS locally and Linux in CI — after this change it would pass in CI and fail on every developer's machine. Assert a non-empty backend name instead

**Verify:** `task test:foundrylib` → `AuthBackendFactoryTests` pass with the new routing

**Steps:**

- [ ] **Step 1: Update the test**

Replace the all-null expectation in `test_project/tests/auth-backend-factory.test.fs`:

```
func test_apple_platforms_resolve_to_the_apple_backend() -> void:
	for platform: PlatformKind in [PlatformKind.IOS, PlatformKind.MACOS]:
		var backend: AuthBackend = _factory.resolve(platform)
		Expect.that(backend.backend_name()).to_equal("apple")

func test_other_platforms_still_resolve_to_null() -> void:
	for platform: PlatformKind in [PlatformKind.ANDROID, PlatformKind.DESKTOP,
			PlatformKind.UNKNOWN]:
		var backend: AuthBackend = _factory.resolve(platform)
		Expect.that(backend.backend_name()).to_equal("null")

func test_apple_backend_reports_unavailable_without_the_native_binary() -> void:
	# The headless suite runs with no binaries, so the native class is absent.
	var backend: AuthBackend = _factory.resolve(PlatformKind.IOS)
	Expect.that(backend.is_available(Provider.GOOGLE)).to_be_false()

## `resolve_current` returns whatever the host platform maps to, which is now the Apple
## backend on a developer's Mac and the Null backend on the Linux CI runner. Asserting a
## specific name here would make the suite pass in one place and fail in the other.
func test_resolve_current_returns_a_usable_backend() -> void:
	var backend: AuthBackend = _factory.resolve_current()
	Expect.that(backend.backend_name().is_empty()).to_be_false()
```

Delete the three cases these replace: `test_every_platform_resolves_to_null_backend_for_now`,
`test_resolved_backend_reports_unavailable`, and `test_resolve_current_returns_a_backend`.
Also update `test_each_resolve_returns_a_usable_instance`, which asserts `"null"` for
`PlatformKind.ANDROID` — Android is unchanged by this epic, so that case still passes and
needs no edit. Verify that rather than assuming it.

- [ ] **Step 2: Run to verify they fail**

Run: `task test:foundrylib`
Expected: FAIL — Apple platforms still resolve to `"null"`.

- [ ] **Step 3: Update `for_platform`**

```
## Returns the platform backend, or null when none applies.
##
## Android is epic F and the desktop OAuth loopback is epic D; both still fall back to
## the Null backend, which is the correct answer until those exist.
func for_platform(platform: PlatformKind) -> AuthBackend?:
	match platform:
		PlatformKind.IOS, PlatformKind.MACOS:
			return AppleAuthBackend.new(_log)
		PlatformKind.ANDROID, PlatformKind.DESKTOP, PlatformKind.UNKNOWN:
			return null
	return null
```

- [ ] **Step 4: Run to verify they pass**

Run: `task test:foundrylib`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/auth/internal/AuthBackendFactory.fs test_project/tests/auth-backend-factory.test.fs
git commit -m "feat(auth): route Apple platforms to the Apple backend"
```

---

### Task 8: macOS CI

**Goal:** Build the Swift package and run its tests on every PR, so the native layer cannot silently rot.

**Files:**
- Modify: `.github/workflows/pr-check.yml`

**Acceptance Criteria:**
- [ ] A new `apple` job runs on `macos-26`
- [ ] It runs `swift test` for the package **and** `task apple:auth` to prove the xcframework assembles
- [ ] Derived data is cached, keyed on `Package.resolved` and `project.yml`
- [ ] **No App token step** — Foundry-Swift-Binary and GoogleSignIn are both public; add one only if a step genuinely fails without it, and say so
- [ ] The three existing jobs are untouched and keep their exact names — they are the required checks
- [ ] The new job is **not** added to branch protection in this task; that is a deliberate decision after seeing its runtime and flakiness

**Verify:** the PR's own run shows `apple` green alongside the three existing checks

**Steps:**

- [ ] **Step 1: Add the job**

Append to `.github/workflows/pr-check.yml`:

```yaml
  apple:
    name: Apple native
    runs-on: macos-26
    timeout-minutes: 40
    steps:
      - uses: actions/checkout@v4

      - name: Select Xcode
        uses: maxim-lobanov/setup-xcode@v1
        with:
          xcode-version: "26.4.1"

      - name: Install xcodegen
        run: brew install xcodegen

      - name: Install Task
        uses: arduino/setup-task@v2
        with:
          repo-token: ${{ secrets.GITHUB_TOKEN }}

      - name: Cache SwiftPM and derived data
        uses: actions/cache@v4
        with:
          path: |
            .build/xcodebuild
            native/apple/FoundryKitNative/.build
          key: apple-${{ hashFiles('native/apple/FoundryKitNative/Package.swift', 'native/apple/FoundryKitNative/project.yml') }}
          restore-keys: apple-

      - name: Run Swift unit tests
        working-directory: native/apple/FoundryKitNative
        run: swift test

      - name: Build xcframework
        run: task apple:auth
```

- [ ] **Step 2: Verify on the PR itself**

There is no way to test a workflow without pushing it. Open the PR, then read the run:

```bash
gh pr checks <PR> --repo cafecito-games/FoundryKit
gh run view <run-id> --repo cafecito-games/FoundryKit --log | tail -40
```

Expected: `apple` completes green. **Record its wall-clock duration in the PR body** — that number decides whether it becomes a required check, and it is the main ongoing cost of the native layer.

If `swift test` cannot load the Foundry macros without `-load-plugin-executable`, switch that step to `xcodebuild test -scheme FoundryKitAuth_macOS` and say so in the PR — that is a real constraint, not a workaround.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/pr-check.yml
git commit -m "ci: build and test the Apple native package"
```

---

## Definition of Done

- [ ] `task test:foundrylib` passes, with 12 tests added across Tasks 2 and 6
- [ ] `task test:scripts` passes
- [ ] `task lint` passes
- [ ] `cd native/apple/FoundryKitNative && swift test` passes
- [ ] `task apple:auth` produces both binaries and is idempotent
- [ ] The `apple` CI job is green on the final PR, with its duration recorded
- [ ] The `alpha.1 → alpha.2` bump is confirmed working
- [ ] Every pre-existing `NativeRequestTests` case still passes unchanged

## Deliberately out of scope

- **Sign in with Apple** and **Keychain storage** — epic E
- **Backend credential exchange** — epic C. `sign_in` still ends in `Configuration` at the subsystem level; this epic only makes the *credential* real
- **Android** — epic F
- **Making `apple` a required check** — decide after seeing its runtime
- **Fixing #57** (`Jwt.expiry_from` zero ambiguity), **#64**, **#66** — track-only, unrelated to this epic

## Open risks

1. **Macro loading under `swift test`.** The legacy project passed `-load-plugin-executable` via `OTHER_SWIFT_FLAGS`. If plain `swift test` cannot resolve the macros, Task 3 surfaces it and Task 8 switches to `xcodebuild test`.
2. **`presentingAnchor()` on macOS.** `NSApplication.shared.keyWindow` can be nil in a headless or windowless context; the guard returns `Unavailable` rather than crashing, but real-device behaviour is unverified until someone runs it.
3. **No device testing.** Nothing here proves a real Google sign-in completes on hardware. The Swift tests cover pure functions; the `.fs` tests cover the protocol against a fake. First real-device verification belongs in epic E or G.
