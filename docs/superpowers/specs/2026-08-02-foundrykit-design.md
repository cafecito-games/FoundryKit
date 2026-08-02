# FoundryKit Design

Date: 2026-08-02
Status: Approved

## Purpose

FoundryKit unifies three previously independent Godot addons into one Foundry addon
providing native platform integrations for iOS, macOS and Android:

| Source | Branch ported from | State |
|--------|--------------------|-------|
| AuthenticationKit | `foundry-migration` | Complete `.fs` port, on FoundrySwift |
| PurchaseKit | `foundry-port` | Partial `.fs` port, on FoundrySwift |
| MobileKit | `main` | Not started: GDScript, SwiftGodot, Vest |

The merge exists to do three things at once: remove the triplicated native bridge,
logging and backend-selection code; adopt Foundry Script language features that
postdate all three ports (tagged unions, tuples, generics, nullable types); and give
consumers a single install.

## Decisions

| Decision | Choice |
|----------|--------|
| Packaging | One addon, modular subsystems |
| Native binaries | Split per subsystem from day one |
| API style | Full redesign: `async` + result tagged unions |
| Mobile scope | Virtual keyboard only, structured for growth |
| Git history | Fresh start; source repos stay active for bugfixes |

## Language constraints

Verified against `Foundry/modules/foundry_script/GRAMMAR.md`:

- `enum_name` accepts **no type parameters**. Tagged unions cannot be generic, so a
  shared `Result[T, E]` is impossible. Result unions are declared concretely per
  subsystem and named by payload shape so they can be reused across operations.
- Classes, traits and functions **are** generic (`class Box[T]`, `trait Container[T]`,
  `func swap[T](...)`), with optional upper bounds. Shared behaviour therefore travels
  through generic traits, not generic unions.
- Tagged union payload fields must be named. Case tags are ordinal; `= expression` is a
  parse error anywhere in a union.
- `match` supports payload binds without `var`, pattern guards, and exhaustiveness
  checking over tagged unions.
- Nullable types (`T?`), structural tuples (`(T1, T2)`), `tuple_name`, `Coroutine[T]`
  and typed `Signal[[...]]` are all available.
- `@autoload` supports `depends_on` and `order_id`.

## Repository layout

```
FoundryKit/
  addons/FoundryKit/
    FoundryKit.fs                          @autoload facade
    plugin.cfg
    export_plugin.fs
    FoundryKitAuth.foundryextension        -> foundry_kit_auth_entry_point
    FoundryKitPurchase.foundryextension    -> foundry_kit_purchase_entry_point
    FoundryKitMobile.foundryextension      -> foundry_kit_mobile_entry_point
    android-dependencies/
      auth.txt  purchase.txt  mobile.txt
    core/
    auth/       auth/internal/
    purchase/   purchase/internal/
    mobile/     mobile/internal/
    bin/
      core/     ios/ macos_arm64/ android/{debug,release}/
      auth/     ios/ macos_arm64/ android/{debug,release}/
      purchase/ ios/ macos_arm64/ android/{debug,release}/
      mobile/   ios/ macos_arm64/ android/{debug,release}/
  native/
    apple/FoundryKitNative/                one SwiftPM package, four products
    android/                               :core :auth :purchase :mobile
  test_project/
    project.foundry  packages.toml  tests/
  scripts/
  docs/
    provenance/{auth,purchase,mobile}.md
    migration/{auth,purchase,mobile}.md
  Taskfile.yml
```

Namespaces mirror folders: `games.cafecito.foundrykit`, `.core`, `.auth`,
`.auth.internal`, `.purchase`, `.purchase.internal`, `.mobile`, `.mobile.internal`.

## Architecture

### One addon, one autoload

`FoundryKit.fs` is the only `@autoload`. Subsystems are members reached as
`FoundryKit.auth`, `FoundryKit.purchase`, `FoundryKit.mobile`, constructed lazily
through property accessors:

```
@autoload
namespace games.cafecito.foundrykit

class_name FoundryKit extends Node

var _auth: AuthSubsystem = null

var auth: AuthSubsystem:
	get:
		if _auth == null:
			_auth = AuthSubsystem.new(_log.child("auth"))
		return _auth
```

A single autoload avoids `depends_on` / `order_id` ordering between four autoloads and
gives one owner for lifecycle notifications (`NOTIFICATION_APPLICATION_FOCUS_IN`,
`NOTIFICATION_APPLICATION_PAUSED`, and their counterparts), which today exist only in
AuthenticationKit and are needed by every subsystem that shows a native sheet.

Lazy construction means a game that only reads keyboard height never instantiates the
auth backend and never touches Keychain.

### Native layer

Four Apple binaries and four AARs:

| Artifact | Kind | Entry symbol |
|----------|------|--------------|
| `FoundryKitCore.framework` | dependency, not an extension | none |
| `FoundryKitAuth.xcframework` | extension | `foundry_kit_auth_entry_point` |
| `FoundryKitPurchase.xcframework` | extension | `foundry_kit_purchase_entry_point` |
| `FoundryKitMobile.xcframework` | extension | `foundry_kit_mobile_entry_point` |

`FoundryKitCore` is a dynamic framework listed in each extension's `[dependencies]`
block. Because it is loaded once and shared by three dynamically loaded frameworks, it
must hold **no shared mutable state** — logging configuration and bridge registries are
per-instance values handed in at construction, never statics.

Each subsystem target invokes `#initFoundryExtension(cdecl:types:)` with its own cdecl
and its own registered native classes. `LD_RUNPATH_SEARCH_PATHS` wiring that locates
`FoundrySwift.framework` is replicated per framework target in `project.yml`.

Android mirrors this as `:core` plus three library modules producing four AARs.
`export_plugin.fs` emits Maven dependencies only for enabled subsystems, driven by
project settings `foundry_kit/subsystems/{auth,purchase,mobile}_enabled`. Without this,
a keyboard-only game inherits Play Billing and Credentials dependencies for binaries it
deleted.

### Partial installs are supported

A consumer may delete any subsystem's `bin/` directory. The `.fs` layer must degrade
rather than error. Each subsystem resolves its backend at construction and falls back to
a Null backend when its native classes are absent, reporting `is_available() == false`.

A missing binary and an unsupported platform deliberately follow the same path and are
indistinguishable to consumers.

## Core layer

```
core/
  FoundryKitLog.fs      per-subsystem instance, level-controlled
  Platform.fs           enum_name PlatformKind + detection
  NativeBridge.fs       guarded ClassDB probe/instantiate -> Object?
  NativeOutcome.fs      the one shared outcome shape
  NativeRequest.fs      one-shot signal broker
  RequestGuard.fs       single-flight + watchdog + foreground recovery
```

Core knows nothing about authentication, purchases or keyboards. It declares no
subsystem types and imports no subsystem namespace. CI enforces this.

### The async bridge

Native classes are signal-based (`sign_in_success` / `sign_in_failed`); the public API
is `async func … -> Result`. `NativeRequest` adapts between them: it connects one-shot
to a native object's success and failure signals, awaits whichever fires first, arms a
timeout, and survives the app being backgrounded mid-sheet.

```
enum_name NativeOutcome:
	Succeeded(payload: Dictionary[String, Variant])
	Failed(code: int, message: String)
	TimedOut(elapsed_seconds: float)
	Unavailable(missing_class: String)
```

Core stops at `NativeOutcome`. Each subsystem maps it into its own typed union; that
mapping is the only place `Dictionary[String, Variant]` appears, and it never reaches
consumers.

`RequestGuard` generalises AuthenticationKit's request watchdog, single-flight guard and
foreground-recovery grace period, which PurchaseKit and MobileKit currently lack.

### Backend selection

```
trait_name BackendFactory[TBackend]

abstract func for_platform(platform: PlatformKind) -> TBackend?
abstract func null_backend() -> TBackend
```

This replaces three copies of a `match _current_os_name()` chain. A `null` return —
unsupported platform or deleted binary — yields the Null backend.

## Public API

### Auth

Provider configuration becomes a tagged union, making invalid configuration
unrepresentable rather than a runtime `push_error`:

```
enum_name ProviderConfig:
	Google(web_client_id: String, ios_client_id: String)
	Apple(service_id: String, redirect_uri: String)
	EmailPassword

enum_name AuthError:
	Cancelled
	NoCredential
	Unavailable(provider: Provider)
	Configuration(detail: String)
	Storage(detail: String)
	RequestFailed(status: int, body: String)
	InvalidResponse(detail: String)
	MissingField(field: String)
	SessionExpired(expired_at: int)
	TimedOut(elapsed_seconds: float)
```

`AuthError` replaces `AuthenticationErrorCode`, the ten parallel `ERROR_*` string
constants, `AuthenticationFailure`, and the three conversion functions
(`code_to_string`, `error_code_from_string`, `normalize_native_code`) in
`AuthenticationKitTypes`.

Results are named by payload shape and reused across operations, since unions cannot be
generic. Four unions cover roughly nineteen methods:

```
enum_name SessionResult:    Success(session: AuthSession)   / Failure(error: AuthError)
enum_name TokenResult:      Success(token: String)          / Failure(error: AuthError)
enum_name ResponseResult:   Success(response: AuthResponse) / Failure(error: AuthError)
enum_name CompletionResult: Success                         / Failure(error: AuthError)

abstract async func sign_in(config: ProviderConfig) -> SessionResult
abstract async func get_valid_access_token() -> TokenResult
abstract async func request(method: HttpMethod, path: String,
                           body: Variant = null) -> ResponseResult
```

Ten of the twelve current signals become return values. Two survive because they are
genuinely unsolicited: `session_expired(error: AuthError)` and
`tokens_refreshed(session: AuthSession)`.

### Purchase

Identical shape: `PurchaseError`, plus `ProductsResult`, `PurchaseResult` and
`RestoreResult`. `Product`, `Purchase` and `Offer` remain `RefCounted` classes rather
than tuples — they carry many fields, are stored and passed around, and benefit from
methods such as `localized_price()`.

### Mobile

Mobile has no result unions, because nothing it does can fail asynchronously:

```
tuple_name VirtualKeyboardState(visible: bool, height_px: int)

signal virtual_keyboard_changed(state: VirtualKeyboardState)

func keyboard_state() -> VirtualKeyboardState
func is_available() -> bool
```

This replaces three signals (`virtual_keyboard_visibility_changed`,
`virtual_keyboard_opened`, `virtual_keyboard_closed`) and the redundant
`get_virtual_keyboard_height` / `get_virtual_keyboard_height_px` pair, which exists only
because the signal used different argument naming.

The subsystem and its native targets are structured so that safe area, haptics, share
sheet and similar additions slot in without rework, but none are in scope for v1.

## Error handling

- Every failure path terminates in a subsystem error union. No `push_error` as a control
  path; logging is observation only.
- **No `_` wildcard branches over error unions** in subsystem code. Exhaustiveness
  checking is the main safety payoff of the redesign: adding a variant must surface
  every site that needs updating.
- Timeouts are errors, not hangs. `RequestGuard` guarantees every `async` public method
  resolves.
- Absent natives produce `Unavailable`, never a crash or a null dereference.

## Testing

| Layer | Tooling | Current state |
|-------|---------|---------------|
| Foundry Script units | `foundry.testlib` (FoundryLib) | AK has suites; PK and MK have none in `.fs` |
| Swift units | macOS-hosted | AK has four suites; MK has one; PK has none |
| Android JVM units | Gradle | AK has four; PK has one |
| Contract scripts | `scripts/test-*` | Present in AK and PK |
| Simulator probe | `run-ios-sim-probe` | MK only |

Two checks are new and specific to this design:

- **Import-boundary lint** — fails if `core/` imports a subsystem, if a subsystem
  imports another subsystem, or if a non-`internal` file imports an `internal` one. This
  is what keeps the four-way native split from silently re-coupling.
- **Partial-install test** — runs `test_project` with subsystem `bin/` directories
  deleted; every subsystem must report `is_available() == false` and log cleanly with no
  errors.

## CI

Three near-identical workflow pairs consolidate into one `pr-check` and one `release`.

`pr-check` jobs: lint and import boundary; `.fs` strict parse and `foundry.testlib`
suites; Swift tests; Android tests; then the build matrix of 4 frameworks ×
(iOS, iOS-simulator, macOS-arm64) plus 4 AARs.

The private `Foundry-Swift-Binary` dependency requires the GitHub App token step
currently used in PurchaseKit's workflow.

The build matrix is roughly four times the Apple build cost of a single-artifact layout.
This is the accepted price of per-subsystem binaries.

## Provenance and back-porting

AuthenticationKit, PurchaseKit and MobileKit remain active for bugfixes during the
transition. `docs/provenance/{auth,purchase,mobile}.md` maps every source file to its
FoundryKit destination and records the FoundryKit commit it was ported at.

Back-porting is manual and one-directional: fixes flow old → new. FoundryKit changes do
not flow back.

`docs/migration/{auth,purchase,mobile}.md` documents the consumer-facing API changes.
There is no compatibility shim — a shim would preserve the paired-signal pattern this
redesign exists to remove.

## Sequencing

MobileKit carries migration debt the other two do not: GDScript → `.fs`, SwiftGodot →
FoundrySwift, and Vest → `foundry.testlib`. Its Swift sources do exist
(`addons/MobileKit/iOSMobileKit/`), and already separate `iOSMobileKitCore` from the
extension target — a useful precedent for the `FoundryKitCore` split. The port is
therefore a rewrite of the binding layer against FoundrySwift rather than a from-scratch
implementation, with `VirtualKeyboardState.swift` mapping onto the `VirtualKeyboardState`
tuple. It has the smallest API surface but still the largest port delta, and should be
sequenced after the core layer is proven by at least one subsystem.

Suggested order:

1. Repository scaffolding, `Taskfile`, `test_project`, CI skeleton.
2. Core layer with its own tests, exercised by a null-only subsystem.
3. Auth — the most complete source port and the best exercise of the async bridge.
4. Purchase — validates that core generalises rather than being auth-shaped.
5. Mobile — largest port delta, smallest surface.
6. Partial-install test, provenance and migration docs, release workflow.

## Breaking changes

This is fully breaking for existing AuthenticationKit and PurchaseKit consumers. Every
call site changes from "call, then await one of two signals" to "await, then match".
MobileKit consumers additionally move from GDScript class names to namespaced `.fs`
types.

## Pinned dependencies

| Dependency | Version | Note |
|------------|---------|------|
| FoundryLib (`foundry.testlib`) | `6df2b4d7ff43c013a4c9e9033c01cdadbdeda19a` | Current `main`; includes test adapter protocol v1 |
| Foundry-Swift-Binary | `0.1.0-alpha.2` | AK and PK pin `alpha.1`; alpha.2 syncs the Foundry alpha.14 extension API |

Both are pinned exactly in `packages.toml` and `Package.swift`, matching existing
practice in AuthenticationKit and PurchaseKit.

The Foundry-Swift-Binary bump from `alpha.1` to `alpha.2` is a port-time change, not a
carry-over: it must be validated against the extension-loading path before the auth
subsystem is considered done.
