# FoundryKit Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the FoundryKit repository scaffolding and the shared `core/` layer — logging, platform detection, native probing, the async signal bridge, request guarding and backend selection — proven by tests with fake natives, so the auth, purchase and mobile subsystems can be built on top of it.

**Architecture:** One Foundry addon at `addons/FoundryKit/` with a `core/` module that has no knowledge of any subsystem. Core adapts signal-based native objects into `async` functions returning a `NativeOutcome` tagged union. Subsystems (built in later plans) map that union into their own typed result unions.

**Tech Stack:** Foundry Script (`.fs`), FoundryLib `foundry.testlib`, `anvil` package manager, Task (Taskfile), prek (pre-commit), ripgrep. Swift and Android work is out of scope for this plan.

**Spec:** `docs/superpowers/specs/2026-08-02-foundrykit-design.md`

**Scope note:** This is plan 1 of 5. Plans 2–5 (auth, purchase, mobile, release) are written after this one lands, informed by the real shape of core.

---

## File Structure

| File | Responsibility |
|------|----------------|
| `.gitignore` | Exclude build output, installed packages, editor caches |
| `Taskfile.yml` | Entry points: `lint`, `test:foundrylib`, `test:scripts` |
| `.pre-commit-config.yaml` | prek hooks |
| `addons/FoundryKit/plugin.cfg` | Addon manifest |
| `addons/FoundryKit/FoundryKit.fs` | `@autoload` facade: logging control, platform info, lifecycle fan-out |
| `addons/FoundryKit/core/PlatformKind.fs` | Platform enum |
| `addons/FoundryKit/core/Platform.fs` | OS-name → `PlatformKind` detection |
| `addons/FoundryKit/core/FoundryKitLog.fs` | Per-instance leveled logger with `child()` |
| `addons/FoundryKit/core/NativeOutcome.fs` | The one shared outcome union |
| `addons/FoundryKit/core/NativeBridge.fs` | Guarded `ClassDB` probe / instantiate |
| `addons/FoundryKit/core/NativeRequest.fs` | Signal → `async` outcome adapter with timeout |
| `addons/FoundryKit/core/RequestGuard.fs` | Single-flight gate + foreground-recovery detection |
| `addons/FoundryKit/core/BackendFactory.fs` | Generic backend-selection trait |
| `test_project/project.foundry` | Test project config |
| `test_project/packages.toml` | Pins FoundryLib and FoundrySwift |
| `test_project/tests/*.test.fs` | testlib suites |
| `test_project/tests/support/*.notest.fs` | Fake natives and helpers |
| `scripts/test-foundrylib` | Runs the testlib suite headless |
| `scripts/test-import-boundaries` | Enforces core/subsystem/internal import rules |
| `scripts/test-foundry-script-strict` | Structural contract checks on `.fs` files |

**Indentation is tabs** throughout `.fs` files, matching AuthenticationKit and PurchaseKit.

---

### Task 0: Repository scaffolding and test harness

**Goal:** A runnable, empty-but-green test harness plus a syntax probe that proves every Foundry Script feature this plan depends on actually works before anything is built on it.

**Files:**
- Create: `.gitignore`
- Create: `Taskfile.yml`
- Create: `.pre-commit-config.yaml`
- Create: `addons/FoundryKit/plugin.cfg`
- Create: `test_project/packages.toml`
- Create: `test_project/project.foundry`
- Create: `test_project/icon.svg`
- Create: `scripts/test-foundrylib`
- Test: `test_project/tests/syntax-probe.test.fs`

**Acceptance Criteria:**
- [ ] `task test:foundrylib` runs the headless testlib runner and reports all tests passing
- [ ] The syntax probe exercises tagged unions with payloads, `tuple_name`, a generic trait, a property accessor, a rest parameter, and exhaustive `match` with payload binds
- [ ] Installed packages under `test_project/addons/` are gitignored

**Verify:** `task test:foundrylib` → exits 0, output ends with a passing summary and no failures

**Steps:**

- [ ] **Step 1: Create `.gitignore`**

```gitignore
.DS_Store
.build/
test_project/.godot/
test_project/.foundry/
test_project/addons/foundrylib/
test_project/addons/FoundrySwift/
test_project/addons/FoundryKit/
test_project/packages.lock
addons/FoundryKit/bin/**/*.xcframework
addons/FoundryKit/bin/**/*.framework
addons/FoundryKit/bin/**/*.aar
addons/FoundryKit/bin/**/*.bundle
*.log
```

- [ ] **Step 2: Create `addons/FoundryKit/plugin.cfg`**

```ini
[plugin]

name="FoundryKit"
description="Native integrations for iOS, macOS and Android: authentication, purchases and mobile platform services"
author="CafecitoGames"
version="0.1.0"
script="export_plugin.fs"
```

Note: `export_plugin.fs` is created in a later plan. `plugin.cfg` is not loaded by the
headless test runner, so a missing script does not break this task's verification.

- [ ] **Step 3: Create `test_project/packages.toml`**

```toml
[packages]
  [packages.foundrylib]
    source      = "git"
    url         = "https://github.com/cafecito-games/FoundryLib.git"
    version     = "6df2b4d7ff43c013a4c9e9033c01cdadbdeda19a"
    source_path = "addons/foundrylib"
    install_as  = "foundrylib"
```

FoundrySwift is deliberately absent: this plan is script-only and installs no native
binaries. It is added in the plan that first builds a framework.

- [ ] **Step 4: Create `test_project/project.foundry`**

```ini
; Engine configuration file.

config_version=5

[application]

config/name="FoundryKitTest"
config/features=PackedStringArray("0.1")
config/icon="res://icon.svg"

[debug]

foundry_script/warnings/directory_rules={
"res://addons/foundrylib": 0,
"res://addons": 1
}
foundry_script/warnings/inferred_declaration=1
foundry_script/warnings/unsafe_call_argument=2
foundry_script/warnings/unsafe_cast=2
foundry_script/warnings/unsafe_method_access=2
foundry_script/warnings/unsafe_property_access=2
foundry_script/warnings/untyped_declaration=2
```

The `FoundryKit` autoload entry is added in Task 8, once the facade exists.

- [ ] **Step 5: Create `test_project/icon.svg`**

```svg
<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128"><rect width="128" height="128" rx="24" fill="#4a2c17"/></svg>
```

- [ ] **Step 6: Create `scripts/test-foundrylib`**

```bash
#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
project_dir="$repo_root/test_project"

fail() {
    echo "ERROR: $*" >&2
    exit 1
}

resolve_foundry_bin() {
    local fallback="/Users/christian/CafecitoGames/Foundry/bin/foundry.macos.editor.dev.arm64"

    if [ -n "${FOUNDRY_BIN:-}" ]; then
        [ -x "$FOUNDRY_BIN" ] || fail "FOUNDRY_BIN is set but is not executable: $FOUNDRY_BIN"
        printf '%s\n' "$FOUNDRY_BIN"
        return
    fi

    if command -v foundry >/dev/null 2>&1; then
        command -v foundry
        return
    fi

    if [ -x "$fallback" ]; then
        printf '%s\n' "$fallback"
        return
    fi

    fail "Foundry executable not found. Set FOUNDRY_BIN or put foundry on PATH."
}

command -v anvil >/dev/null 2>&1 || \
    fail "anvil is required. Install with: go install github.com/cafecito-games/foundry-tools/cmd/anvil@latest"

foundry_bin=$(resolve_foundry_bin)

if [ -z "${GITHUB_TOKEN:-}" ] && [ -n "${GH_TOKEN:-}" ]; then
    export GITHUB_TOKEN="$GH_TOKEN"
fi

if [ -z "${GITHUB_TOKEN:-}" ] && command -v gh >/dev/null 2>&1; then
    if token=$(gh auth token 2>/dev/null); then
        export GITHUB_TOKEN="$token"
    fi
fi

if [ "${FOUNDRYKIT_SKIP_ANVIL_INSTALL:-}" != "1" ]; then
    anvil pkg install --dir "$project_dir"
fi

# The engine's headless global-class scan never follows directory symlinks, so the addon
# must be a real directory inside the test project. Native binaries stay out of the copy:
# this suite is script-only.
if [ -L "$project_dir/addons/FoundryKit" ]; then
    rm "$project_dir/addons/FoundryKit"
fi
rsync -a --delete --delete-excluded \
    --exclude 'bin/' \
    --exclude '*.foundryextension' \
    "$repo_root/addons/FoundryKit/" "$project_dir/addons/FoundryKit/"

"$foundry_bin" --headless project test \
    --project "$project_dir" \
    --runner res://addons/foundrylib/testlib/cli/run.fs \
    -- --path res://tests
```

Then: `chmod +x scripts/test-foundrylib`

- [ ] **Step 7: Create `Taskfile.yml`**

```yaml
version: '3'

tasks:
  default:
    desc: Run all script-level checks
    deps: [test:foundrylib]

  lint:
    desc: Run all linters and formatters via prek
    cmds:
      - prek run --all-files

  test:foundrylib:
    desc: Run the FoundryLib testlib suite headless
    cmds:
      - scripts/test-foundrylib

  test:scripts:
    desc: Run build-script regression tests
    cmds:
      - scripts/test-import-boundaries
      - scripts/test-foundry-script-strict
```

`test:scripts` references scripts created in Task 9; it is not run until then.

- [ ] **Step 8: Create `.pre-commit-config.yaml`**

```yaml
repos:
  - repo: https://github.com/pre-commit/pre-commit-hooks
    rev: v5.0.0
    hooks:
      - id: trailing-whitespace
      - id: end-of-file-fixer
      - id: check-yaml
      - id: check-merge-conflict
      - id: mixed-line-ending
        args: [--fix=lf]
```

- [ ] **Step 9: Write the syntax probe test**

This is the failing test for this task. It exercises every language feature the rest of
the plan depends on, so an unsupported assumption surfaces now rather than in Task 5.

Create `test_project/tests/syntax-probe.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib

## Proves the Foundry Script features this plan depends on are available.
##
## If any assertion here fails to compile, the affected core design must be revised
## before proceeding past Task 0.
class_name SyntaxProbeTests
extends RefCounted
uses Test

enum Shape:
	Empty
	Circle(radius: float)
	Rect(width: float, height: float)

tuple Pair(first: int, second: int)

trait Holder[T]:
	abstract func held() -> T

class IntHolder
uses Holder[int]:
	var _value: int = 7

	func held() -> int:
		return _value

var _backing: int = 0

var doubled: int:
	get:
		return _backing * 2
	set(value):
		_backing = value / 2

func _sum_all(...values: Array[int]) -> int:
	var total: int = 0
	for value: int in values:
		total += value
	return total

func test_tagged_union_matches_exhaustively_with_payload_binds() -> void:
	var shape: Shape = Shape.Rect(3.0, 4.0)
	var area: float = 0.0
	match shape:
		Shape.Empty:
			area = 0.0
		Shape.Circle(radius):
			area = 3.14159 * radius * radius
		Shape.Rect(width, height):
			area = width * height
	Expect.that(area).to_equal(12.0)

func test_tagged_union_payload_less_case_is_a_value() -> void:
	var shape: Shape = Shape.Empty
	var matched: bool = false
	match shape:
		Shape.Empty:
			matched = true
		Shape.Circle(_radius):
			matched = false
		Shape.Rect(_width, _height):
			matched = false
	Expect.that(matched).to_be_true()

func test_named_tuple_fields_are_readable() -> void:
	var pair: Pair = Pair(2, 5)
	Expect.that(pair.first).to_equal(2)
	Expect.that(pair.second).to_equal(5)

func test_generic_trait_is_satisfiable() -> void:
	var holder: IntHolder = IntHolder.new()
	Expect.that(holder.held()).to_equal(7)

func test_property_accessors_run() -> void:
	doubled = 10
	Expect.that(_backing).to_equal(5)
	Expect.that(doubled).to_equal(10)

func test_rest_parameter_collects_arguments() -> void:
	Expect.that(_sum_all(1, 2, 3, 4)).to_equal(10)

func test_nullable_type_accepts_null() -> void:
	var maybe: RefCounted? = null
	Expect.that(maybe == null).to_be_true()
```

- [ ] **Step 10: Run the probe and confirm the harness works**

Run: `task test:foundrylib`

Expected on first run: `anvil` installs FoundryLib into `test_project/addons/foundrylib/`,
then the runner reports 7 passing tests in `SyntaxProbeTests`.

If any test fails to **compile**, stop and record which feature is unavailable — the core
design in the spec depends on it and must be revised before Task 1. If a test fails on an
assertion, fix the probe. Do not proceed with a red probe.

- [ ] **Step 11: Commit**

```bash
git add .gitignore Taskfile.yml .pre-commit-config.yaml addons/FoundryKit/plugin.cfg \
        test_project/packages.toml test_project/project.foundry test_project/icon.svg \
        scripts/test-foundrylib test_project/tests/syntax-probe.test.fs
git commit -m "feat: scaffold FoundryKit repo and testlib harness"
```

---

### Task 1: Platform detection

**Goal:** `PlatformKind` enum and a `Platform` helper that maps `OS.get_name()` onto it, so backend selection never repeats an OS-name string match.

**Files:**
- Create: `addons/FoundryKit/core/PlatformKind.fs`
- Create: `addons/FoundryKit/core/Platform.fs`
- Test: `test_project/tests/platform.test.fs`

**Acceptance Criteria:**
- [ ] Every OS name handled by AuthenticationKit's `_select_backend` maps to a `PlatformKind`
- [ ] Unknown OS names map to `PlatformKind.UNKNOWN` rather than crashing
- [ ] `Platform.current()` returns a value consistent with `OS.get_name()` on the host

**Verify:** `task test:foundrylib` → `PlatformTests` all pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/platform.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name PlatformTests
extends RefCounted
uses Test

func test_apple_mobile_maps_to_ios() -> void:
	Expect.that(Platform.from_os_name("iOS")).to_equal(PlatformKind.IOS)

func test_apple_desktop_maps_to_macos() -> void:
	Expect.that(Platform.from_os_name("macOS")).to_equal(PlatformKind.MACOS)

func test_android_maps_to_android() -> void:
	Expect.that(Platform.from_os_name("Android")).to_equal(PlatformKind.ANDROID)

func test_every_desktop_os_name_maps_to_desktop() -> void:
	for name: String in ["Linux", "Windows", "X11", "FreeBSD", "NetBSD", "OpenBSD", "BSD"]:
		Expect.that(Platform.from_os_name(name)).to_equal(PlatformKind.DESKTOP)

func test_unknown_os_name_maps_to_unknown() -> void:
	Expect.that(Platform.from_os_name("Dreamcast")).to_equal(PlatformKind.UNKNOWN)

func test_empty_os_name_maps_to_unknown() -> void:
	Expect.that(Platform.from_os_name("")).to_equal(PlatformKind.UNKNOWN)

func test_current_matches_host_os_name() -> void:
	Expect.that(Platform.current()).to_equal(Platform.from_os_name(OS.get_name()))

func test_is_apple_is_true_only_for_ios_and_macos() -> void:
	Expect.that(Platform.is_apple(PlatformKind.IOS)).to_be_true()
	Expect.that(Platform.is_apple(PlatformKind.MACOS)).to_be_true()
	Expect.that(Platform.is_apple(PlatformKind.ANDROID)).to_be_false()
	Expect.that(Platform.is_apple(PlatformKind.DESKTOP)).to_be_false()
	Expect.that(Platform.is_apple(PlatformKind.UNKNOWN)).to_be_false()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `Platform` and `PlatformKind` are not defined.

- [ ] **Step 3: Create `addons/FoundryKit/core/PlatformKind.fs`**

```
namespace games.cafecito.foundrykit.core

## The platform families FoundryKit selects backends for.
##
## `DESKTOP` covers every non-Apple desktop OS; macOS is separate because it can run
## either the Apple native backend or the desktop backend depending on configuration.
enum_name PlatformKind:
	UNKNOWN = 0
	IOS = 1
	MACOS = 2
	ANDROID = 3
	DESKTOP = 4
```

- [ ] **Step 4: Create `addons/FoundryKit/core/Platform.fs`**

```
namespace games.cafecito.foundrykit.core

## Maps engine OS names onto [PlatformKind].
class_name Platform extends RefCounted

## Returns the platform family the game is currently running on.
static func current() -> PlatformKind:
	return from_os_name(OS.get_name())

## Maps an engine OS name onto a platform family.
##
## Unrecognised names return [constant PlatformKind.UNKNOWN] so callers fall back to a
## null backend rather than failing.
static func from_os_name(os_name: String) -> PlatformKind:
	match os_name:
		"iOS":
			return PlatformKind.IOS
		"macOS":
			return PlatformKind.MACOS
		"Android":
			return PlatformKind.ANDROID
		"Linux", "Windows", "X11", "FreeBSD", "NetBSD", "OpenBSD", "BSD":
			return PlatformKind.DESKTOP
		_:
			return PlatformKind.UNKNOWN

## Returns whether the platform is an Apple platform.
static func is_apple(platform: PlatformKind) -> bool:
	match platform:
		PlatformKind.IOS, PlatformKind.MACOS:
			return true
		_:
			return false
```

- [ ] **Step 5: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 8 tests in `PlatformTests`.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/core/PlatformKind.fs addons/FoundryKit/core/Platform.fs \
        test_project/tests/platform.test.fs
git commit -m "feat(core): add platform detection"
```

---

### Task 2: Leveled logger

**Goal:** `FoundryKitLog` — a per-instance leveled logger with named children, so three dynamically loaded frameworks never contend on a shared static logging flag.

**Files:**
- Create: `addons/FoundryKit/core/FoundryKitLog.fs`
- Test: `test_project/tests/log.test.fs`

**Acceptance Criteria:**
- [ ] Log level is per-instance; changing one instance's level does not affect another
- [ ] `child(name)` produces a logger whose messages are prefixed with the full dotted path
- [ ] A child inherits its parent's level at creation time
- [ ] Messages below the active level are not emitted

**Verify:** `task test:foundrylib` → `FoundryKitLogTests` all pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/log.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name FoundryKitLogTests
extends RefCounted
uses Test

var _log: FoundryKitLog

func before_each() -> void:
	_log = FoundryKitLog.new("foundrykit")
	_log.set_level(LogLevel.DEBUG)
	_log.set_capture_enabled(true)

func test_default_level_suppresses_debug() -> void:
	var log: FoundryKitLog = FoundryKitLog.new("quiet")
	log.set_capture_enabled(true)
	log.debug("hidden")
	Expect.that(log.captured().size()).to_equal(0)

func test_debug_level_emits_debug() -> void:
	_log.debug("visible")
	Expect.that(_log.captured().size()).to_equal(1)

func test_message_is_prefixed_with_logger_name() -> void:
	_log.debug("hello")
	Expect.that(_log.captured()[0]).to_equal("[foundrykit] hello")

func test_child_prefixes_with_dotted_path() -> void:
	var child: FoundryKitLog = _log.child("auth")
	child.set_capture_enabled(true)
	child.debug("hello")
	Expect.that(child.captured()[0]).to_equal("[foundrykit.auth] hello")

func test_child_inherits_parent_level_at_creation() -> void:
	var child: FoundryKitLog = _log.child("auth")
	Expect.that(child.level()).to_equal(LogLevel.DEBUG)

func test_child_level_is_independent_of_parent() -> void:
	var child: FoundryKitLog = _log.child("auth")
	child.set_level(LogLevel.ERROR)
	Expect.that(_log.level()).to_equal(LogLevel.DEBUG)
	Expect.that(child.level()).to_equal(LogLevel.ERROR)

func test_sibling_loggers_do_not_share_level() -> void:
	var first: FoundryKitLog = FoundryKitLog.new("a")
	var second: FoundryKitLog = FoundryKitLog.new("b")
	first.set_level(LogLevel.DEBUG)
	Expect.that(second.level()).to_equal(LogLevel.WARN)

func test_level_ordering_filters_lower_severities() -> void:
	_log.set_level(LogLevel.WARN)
	_log.debug("no")
	_log.info("no")
	_log.warn("yes")
	_log.error("yes")
	Expect.that(_log.captured().size()).to_equal(2)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `FoundryKitLog` and `LogLevel` are not defined.

- [ ] **Step 3: Create `addons/FoundryKit/core/FoundryKitLog.fs`**

```
namespace games.cafecito.foundrykit.core

## Severity levels, ordered least to most severe.
enum_name LogLevel:
	DEBUG = 0
	INFO = 1
	WARN = 2
	ERROR = 3

## A named, leveled logger.
##
## Instances are deliberately independent: [code]FoundryKitCore[/code] is shared by three
## dynamically loaded frameworks, so a static level flag would be contended across
## subsystems. Each subsystem receives its own logger via [method child].
class_name FoundryKitLog extends RefCounted

const _DEFAULT_LEVEL: LogLevel = LogLevel.WARN

var _name: String = ""
var _level: LogLevel = _DEFAULT_LEVEL
var _capture_enabled: bool = false
var _captured: Array[String] = []

func _init(logger_name: String) -> void:
	_name = logger_name

## Returns a child logger named [code]<parent>.<child_name>[/code], inheriting the
## parent's current level.
func child(child_name: String) -> FoundryKitLog:
	var created: FoundryKitLog = FoundryKitLog.new("%s.%s" % [_name, child_name])
	created.set_level(_level)
	return created

func level() -> LogLevel:
	return _level

func set_level(new_level: LogLevel) -> void:
	_level = new_level

## Enables in-memory capture instead of engine output. Used by tests.
func set_capture_enabled(enabled: bool) -> void:
	_capture_enabled = enabled
	if not enabled:
		_captured.clear()

## Returns messages captured since capture was enabled.
func captured() -> Array[String]:
	return _captured.duplicate()

func debug(message: String) -> void:
	_emit(LogLevel.DEBUG, message)

func info(message: String) -> void:
	_emit(LogLevel.INFO, message)

func warn(message: String) -> void:
	_emit(LogLevel.WARN, message)

func error(message: String) -> void:
	_emit(LogLevel.ERROR, message)

func _emit(severity: LogLevel, message: String) -> void:
	if severity < _level:
		return
	var formatted: String = "[%s] %s" % [_name, message]
	if _capture_enabled:
		_captured.append(formatted)
		return
	match severity:
		LogLevel.DEBUG, LogLevel.INFO:
			print(formatted)
		LogLevel.WARN:
			push_warning(formatted)
		LogLevel.ERROR:
			push_error(formatted)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 8 tests in `FoundryKitLogTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/core/FoundryKitLog.fs test_project/tests/log.test.fs
git commit -m "feat(core): add per-instance leveled logger"
```

---

### Task 3: Native outcome union

**Goal:** `NativeOutcome`, the single untyped outcome shape core speaks. Every subsystem maps it into its own typed result union.

**Files:**
- Create: `addons/FoundryKit/core/NativeOutcome.fs`
- Test: `test_project/tests/native-outcome.test.fs`

**Acceptance Criteria:**
- [ ] All five cases construct and match with payload binds
- [ ] A `match` over `NativeOutcome` with all five cases compiles without a wildcard branch
- [ ] `Succeeded` carries a `Dictionary[String, Variant]` payload

**Verify:** `task test:foundrylib` → `NativeOutcomeTests` all pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/native-outcome.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name NativeOutcomeTests
extends RefCounted
uses Test

## Classifies an outcome without a wildcard branch, proving exhaustiveness holds.
func _describe(outcome: NativeOutcome) -> String:
	match outcome:
		NativeOutcome.Succeeded(payload):
			return "ok:%d" % payload.size()
		NativeOutcome.Failed(code, message):
			return "fail:%d:%s" % [code, message]
		NativeOutcome.TimedOut(elapsed_seconds):
			return "timeout:%.1f" % elapsed_seconds
		NativeOutcome.Abandoned:
			return "abandoned"
		NativeOutcome.Unavailable(missing_class):
			return "unavailable:%s" % missing_class
	return "unreachable"

func test_succeeded_carries_payload() -> void:
	var payload: Dictionary[String, Variant] = {"id_token": "abc", "email": "a@b.c"}
	Expect.that(_describe(NativeOutcome.Succeeded(payload))).to_equal("ok:2")

func test_failed_carries_code_and_message() -> void:
	Expect.that(_describe(NativeOutcome.Failed(3, "boom"))).to_equal("fail:3:boom")

func test_timed_out_carries_elapsed_seconds() -> void:
	Expect.that(_describe(NativeOutcome.TimedOut(1.5))).to_equal("timeout:1.5")

func test_abandoned_is_a_payload_less_value() -> void:
	Expect.that(_describe(NativeOutcome.Abandoned)).to_equal("abandoned")

func test_unavailable_carries_missing_class_name() -> void:
	Expect.that(_describe(NativeOutcome.Unavailable("iOSGoogleSignIn"))) \
		.to_equal("unavailable:iOSGoogleSignIn")

func test_succeeded_with_empty_payload_is_valid() -> void:
	var empty: Dictionary[String, Variant] = {}
	Expect.that(_describe(NativeOutcome.Succeeded(empty))).to_equal("ok:0")
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `NativeOutcome` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/core/NativeOutcome.fs`**

```
namespace games.cafecito.foundrykit.core

## The single outcome shape shared by every native request.
##
## Core stops here deliberately. Tagged unions cannot be generic, so there is no shared
## [code]Result[T, E][/code]; each subsystem maps this union into its own typed result
## union, and that mapping is the only place an untyped payload dictionary appears.
enum_name NativeOutcome:
	## The native reported success. Payload keys are defined by the calling subsystem.
	Succeeded(payload: Dictionary[String, Variant])
	## The native reported a failure with its own numeric code.
	Failed(code: int, message: String)
	## The native never answered within the watchdog window.
	TimedOut(elapsed_seconds: float)
	## The app regained focus with the request still outstanding, which means the user
	## dismissed the native sheet without the native emitting anything. Distinct from
	## [code]TimedOut[/code]: detectable in about a second rather than after the full
	## watchdog window. Subsystems map this to their own cancellation case.
	Abandoned
	## A required native class is not registered — an unsupported platform, or a
	## subsystem binary the consumer removed. The two are deliberately indistinguishable.
	Unavailable(missing_class: String)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 6 tests in `NativeOutcomeTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/core/NativeOutcome.fs test_project/tests/native-outcome.test.fs
git commit -m "feat(core): add native outcome union"
```

---

### Task 4: Native bridge

**Goal:** `NativeBridge` — guarded `ClassDB` probing and instantiation, so a missing subsystem binary yields `null` rather than a crash.

**Files:**
- Create: `addons/FoundryKit/core/NativeBridge.fs`
- Test: `test_project/tests/native-bridge.test.fs`

**Acceptance Criteria:**
- [ ] `is_available(name)` is false for an unregistered class and does not error
- [ ] `instantiate(name)` returns `null` for an unregistered class and logs at debug level
- [ ] `instantiate(name)` returns an instance for a class that exists and can be instantiated
- [ ] A class that exists but cannot be instantiated is treated as unavailable

**Verify:** `task test:foundrylib` → `NativeBridgeTests` all pass

**Steps:**

- [ ] **Step 1: Write the failing test**

`RefCounted` is a real, instantiable engine class, so it stands in for a present native.
`Object` subclasses that cannot be instantiated are represented by an abstract engine
class name.

Create `test_project/tests/native-bridge.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name NativeBridgeTests
extends RefCounted
uses Test

var _log: FoundryKitLog
var _bridge: NativeBridge

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_log.set_level(LogLevel.DEBUG)
	_log.set_capture_enabled(true)
	_bridge = NativeBridge.new(_log)

func test_unregistered_class_is_unavailable() -> void:
	Expect.that(_bridge.is_available("NoSuchNativeClass")).to_be_false()

func test_registered_instantiable_class_is_available() -> void:
	Expect.that(_bridge.is_available("RefCounted")).to_be_true()

func test_instantiate_returns_null_for_unregistered_class() -> void:
	Expect.that(_bridge.instantiate("NoSuchNativeClass") == null).to_be_true()

func test_instantiate_returns_instance_for_registered_class() -> void:
	var created: Object? = _bridge.instantiate("RefCounted")
	Expect.that(created != null).to_be_true()

func test_missing_class_is_logged_at_debug() -> void:
	_bridge.instantiate("NoSuchNativeClass")
	Expect.that(_log.captured().size()).to_equal(1)

func test_registered_but_abstract_class_is_unavailable() -> void:
	# Texture is registered but abstract, so it exists and cannot be instantiated.
	Expect.that(ClassDB.class_exists("Texture")).to_be_true()
	Expect.that(_bridge.is_available("Texture")).to_be_false()

func test_empty_class_name_is_unavailable() -> void:
	Expect.that(_bridge.is_available("")).to_be_false()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `NativeBridge` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/core/NativeBridge.fs`**

```
namespace games.cafecito.foundrykit.core

## Guarded access to natively registered classes.
##
## A subsystem's binary may be absent because the platform does not support it, or
## because the consumer removed it from a partial install. Both cases resolve to the same
## unavailable result rather than an error.
class_name NativeBridge extends RefCounted

var _log: FoundryKitLog

func _init(log: FoundryKitLog) -> void:
	_log = log

## Returns whether a native class is registered and can be instantiated.
func is_available(native_class_name: String) -> bool:
	if native_class_name.is_empty():
		return false
	if not ClassDB.class_exists(native_class_name):
		return false
	return ClassDB.can_instantiate(native_class_name)

## Instantiates a native class, or returns null when it is unavailable.
func instantiate(native_class_name: String) -> Object?:
	if not is_available(native_class_name):
		_log.debug("native class '%s' is absent" % native_class_name)
		return null
	return ClassDB.instantiate(native_class_name)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 7 tests in `NativeBridgeTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/core/NativeBridge.fs test_project/tests/native-bridge.test.fs
git commit -m "feat(core): add guarded native bridge"
```

---

### Task 5: Async signal bridge

**Goal:** `NativeRequest` — converts a signal-based native call into an `async` function returning `NativeOutcome`, with a watchdog timeout.

**Files:**
- Create: `addons/FoundryKit/core/NativeRequest.fs`
- Create: `test_project/tests/support/fake_native.notest.fs`
- Test: `test_project/tests/native-request.test.fs`

**Acceptance Criteria:**
- [ ] Resolves `Succeeded` when the success signal fires, zipping signal arguments into the payload by declared field names
- [ ] Resolves `Failed` when the failure signal fires, reading `(code, message)`
- [ ] Resolves `TimedOut` when neither fires within the timeout
- [ ] Resolves only once — a late second signal is ignored
- [ ] Disconnects from the native after settling, leaving no dangling connections

**Verify:** `task test:foundrylib` → `NativeRequestTests` all pass

**Steps:**

- [ ] **Step 1: Write the fake native**

Create `test_project/tests/support/fake_native.notest.fs`:

```
namespace games.cafecito.foundrykit.tests.support

## Stands in for a native extension object in tests.
##
## Mirrors the signal shape FoundryKit's natives use: a success signal with
## subsystem-specific arguments, and a failure signal carrying (code, message).
class_name FakeNative extends Object

signal operation_success(first: String, second: String)
signal operation_failed(code: int, message: String)

func emit_success(first: String, second: String) -> void:
	operation_success.emit(first, second)

func emit_failure(code: int, message: String) -> void:
	operation_failed.emit(code, message)

func success_connection_count() -> int:
	return operation_success.get_connections().size()

func failure_connection_count() -> int:
	return operation_failed.get_connections().size()
```

- [ ] **Step 2: Write the failing test**

Create `test_project/tests/native-request.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

class_name NativeRequestTests
extends RefCounted
uses Test

const _FIELDS: Array[String] = ["id_token", "email"]

var _log: FoundryKitLog
var _native: FakeNative

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_native = FakeNative.new()

func after_each() -> void:
	_native.free()

func _start(timeout_seconds: float) -> Coroutine[NativeOutcome]:
	var request: NativeRequest = NativeRequest.new(_log)
	return request.await_outcome(
			_native, "operation_success", _FIELDS, "operation_failed", timeout_seconds)

## Renders an outcome as a comparable string, so assertions never need a failure helper.
func _describe(outcome: NativeOutcome) -> String:
	match outcome:
		NativeOutcome.Succeeded(payload):
			return "ok:%s:%s" % [str(payload.get("id_token", "")), str(payload.get("email", ""))]
		NativeOutcome.Failed(code, message):
			return "fail:%d:%s" % [code, message]
		NativeOutcome.TimedOut(elapsed_seconds):
			return "timeout:%s" % ("positive" if elapsed_seconds > 0.0 else "zero")
		NativeOutcome.Abandoned:
			return "abandoned"
		NativeOutcome.Unavailable(missing_class):
			return "unavailable:%s" % missing_class
	return "unreachable"

## Returns the number of payload keys, or -1 when the outcome is not a success.
func _payload_size(outcome: NativeOutcome) -> int:
	match outcome:
		NativeOutcome.Succeeded(payload):
			return payload.size()
		NativeOutcome.Failed(_code, _message):
			return -1
		NativeOutcome.TimedOut(_elapsed_seconds):
			return -1
		NativeOutcome.Abandoned:
			return -1
		NativeOutcome.Unavailable(_missing_class):
			return -1
	return -1

func test_success_signal_resolves_succeeded_with_named_payload() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_success("token-value", "user@example.com")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("ok:token-value:user@example.com")

func test_failure_signal_resolves_failed_with_code_and_message() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_failure(4, "storage unavailable")
	var outcome: NativeOutcome = await pending
	Expect.that(_describe(outcome)).to_equal("fail:4:storage unavailable")

func test_no_signal_within_timeout_resolves_timed_out() -> void:
	var outcome: NativeOutcome = await _start(0.05)
	Expect.that(_describe(outcome)).to_equal("timeout:positive")

func test_late_second_signal_is_ignored() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_success("first", "a@b.c")
	var outcome: NativeOutcome = await pending
	_native.emit_failure(9, "too late")
	Expect.that(_describe(outcome)).to_equal("ok:first:a@b.c")

func test_connections_are_released_after_settling() -> void:
	var pending: Coroutine[NativeOutcome] = _start(5.0)
	_native.emit_success("a", "b")
	await pending
	Expect.that(_native.success_connection_count()).to_equal(0)
	Expect.that(_native.failure_connection_count()).to_equal(0)

func test_connections_are_released_after_timeout() -> void:
	await _start(0.05)
	Expect.that(_native.success_connection_count()).to_equal(0)
	Expect.that(_native.failure_connection_count()).to_equal(0)

func test_fewer_signal_arguments_than_fields_leaves_missing_keys_absent() -> void:
	var request: NativeRequest = NativeRequest.new(_log)
	var pending: Coroutine[NativeOutcome] = request.await_outcome(
			_native,
			"operation_success",
			["id_token", "email", "display_name"],
			"operation_failed",
			5.0)
	_native.emit_success("token", "user@example.com")
	var outcome: NativeOutcome = await pending
	# Only two arguments were emitted, so "display_name" must be absent rather than null.
	Expect.that(_payload_size(outcome)).to_equal(2)
	Expect.that(_describe(outcome)).to_equal("ok:token:user@example.com")
```

- [ ] **Step 3: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `NativeRequest` is not defined.

- [ ] **Step 4: Create `addons/FoundryKit/core/NativeRequest.fs`**

```
namespace games.cafecito.foundrykit.core

## Adapts a signal-based native call into a single awaited [NativeOutcome].
##
## FoundryKit's natives report results by emitting a success signal with
## subsystem-specific arguments, or a failure signal carrying (code, message). Consumers
## await a result instead, so this is the one place that translation happens.
##
## An instance handles exactly one request and settles exactly once.
class_name NativeRequest extends RefCounted

const DEFAULT_TIMEOUT_SECONDS: float = 120.0

signal _settled(outcome: NativeOutcome)

var _log: FoundryKitLog
var _has_settled: bool = false
var _payload_fields: Array[String] = []
var _target: Object? = null
var _success_signal: String = ""
var _failure_signal: String = ""
var _started_ticks_ms: int = 0

func _init(log: FoundryKitLog) -> void:
	_log = log

## Awaits whichever native signal fires first, or times out.
##
## [param payload_fields] names the success signal's arguments in order; they are zipped
## into the [code]Succeeded[/code] payload. Extra field names beyond the emitted argument
## count are omitted rather than filled with nulls.
async func await_outcome(
		target: Object,
		success_signal: String,
		payload_fields: Array[String],
		failure_signal: String,
		timeout_seconds: float = DEFAULT_TIMEOUT_SECONDS) -> NativeOutcome:
	_target = target
	_success_signal = success_signal
	_failure_signal = failure_signal
	_payload_fields = payload_fields.duplicate()
	_started_ticks_ms = Time.get_ticks_msec()

	target.connect(success_signal, _on_native_success)
	target.connect(failure_signal, _on_native_failed)

	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree != null:
		var timer: SceneTreeTimer = tree.create_timer(timeout_seconds)
		timer.timeout.connect(_on_timeout)

	var outcome: NativeOutcome = await _settled
	return outcome

## Settles the request as abandoned. Called by [RequestGuard] when the app regains focus
## with the request still outstanding.
func abandon() -> void:
	_resolve(NativeOutcome.Abandoned)

func _on_native_success(...values: Array[Variant]) -> void:
	var payload: Dictionary[String, Variant] = {}
	var count: int = mini(_payload_fields.size(), values.size())
	for index: int in range(count):
		payload[_payload_fields[index]] = values[index]
	_resolve(NativeOutcome.Succeeded(payload))

func _on_native_failed(code: int, message: String) -> void:
	_resolve(NativeOutcome.Failed(code, message))

func _on_timeout() -> void:
	var elapsed_seconds: float = float(Time.get_ticks_msec() - _started_ticks_ms) / 1000.0
	_log.warn("native request timed out after %.1fs" % elapsed_seconds)
	_resolve(NativeOutcome.TimedOut(elapsed_seconds))

func _resolve(outcome: NativeOutcome) -> void:
	if _has_settled:
		return
	_has_settled = true
	_disconnect_native()
	_settled.emit(outcome)

func _disconnect_native() -> void:
	if _target == null:
		return
	var target: Object = _target
	if target.is_connected(_success_signal, _on_native_success):
		target.disconnect(_success_signal, _on_native_success)
	if target.is_connected(_failure_signal, _on_native_failed):
		target.disconnect(_failure_signal, _on_native_failed)
	_target = null
```

- [ ] **Step 5: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 7 tests in `NativeRequestTests`.

If `test_no_signal_within_timeout_resolves_timed_out` hangs, the headless runner has no
`SceneTree` main loop; in that case record it and gate the timeout tests behind a check
of `Engine.get_main_loop() as SceneTree != null` rather than removing them.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/core/NativeRequest.fs \
        test_project/tests/support/fake_native.notest.fs \
        test_project/tests/native-request.test.fs
git commit -m "feat(core): add async native signal bridge"
```

---

### Task 6: Request guard

**Goal:** `RequestGuard` — single-flight gating plus foreground-recovery detection, generalizing logic that exists only in AuthenticationKit today.

**Files:**
- Create: `addons/FoundryKit/core/RequestGuard.fs`
- Test: `test_project/tests/request-guard.test.fs`

**Acceptance Criteria:**
- [ ] A second `begin()` while a request is active is rejected
- [ ] `end()` releases the gate for the next request
- [ ] Losing focus during an active request marks it backgrounded
- [ ] Regaining focus on a backgrounded active request emits `recovery_due` after the grace period
- [ ] Regaining focus with no active request emits nothing
- [ ] Focus loss without an active request does not mark anything backgrounded

**Verify:** `task test:foundrylib` → `RequestGuardTests` all pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/request-guard.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core

class_name RequestGuardTests
extends RefCounted
uses Test

var _log: FoundryKitLog
var _guard: RequestGuard

func before_each() -> void:
	_log = FoundryKitLog.new("test")
	_guard = RequestGuard.new(_log)
	_guard.set_grace_seconds(0.02)

func test_first_begin_is_accepted() -> void:
	Expect.that(_guard.begin()).to_be_true()

func test_second_begin_while_active_is_rejected() -> void:
	_guard.begin()
	Expect.that(_guard.begin()).to_be_false()

func test_end_releases_the_gate() -> void:
	_guard.begin()
	_guard.end()
	Expect.that(_guard.begin()).to_be_true()

func test_is_active_reflects_gate_state() -> void:
	Expect.that(_guard.is_active()).to_be_false()
	_guard.begin()
	Expect.that(_guard.is_active()).to_be_true()
	_guard.end()
	Expect.that(_guard.is_active()).to_be_false()

func test_focus_loss_during_active_request_marks_backgrounded() -> void:
	_guard.begin()
	_guard.notify_focus_lost()
	Expect.that(_guard.was_backgrounded()).to_be_true()

func test_focus_loss_without_active_request_does_not_mark_backgrounded() -> void:
	_guard.notify_focus_lost()
	Expect.that(_guard.was_backgrounded()).to_be_false()

func test_focus_regain_on_backgrounded_request_emits_recovery_due() -> void:
	var fired: bool = false
	_guard.recovery_due.connect(func() -> void: fired = true)
	_guard.begin()
	_guard.notify_focus_lost()
	_guard.notify_focus_gained()
	await _guard.recovery_due
	Expect.that(fired).to_be_true()

func test_focus_regain_without_active_request_emits_nothing() -> void:
	var fired: bool = false
	_guard.recovery_due.connect(func() -> void: fired = true)
	_guard.notify_focus_gained()
	Expect.that(fired).to_be_false()

func test_end_clears_backgrounded_state() -> void:
	_guard.begin()
	_guard.notify_focus_lost()
	_guard.end()
	Expect.that(_guard.was_backgrounded()).to_be_false()
```

If inline lambda syntax (`func() -> void: ...`) proves unavailable, replace the two
lambda connections with a counter method on the test class and connect that instead.

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `RequestGuard` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/core/RequestGuard.fs`**

```
namespace games.cafecito.foundrykit.core

## Serialises native requests and detects abandoned native sheets.
##
## Native sign-in and purchase sheets take over the screen, so at most one request may be
## outstanding at a time. A user can also dismiss such a sheet without the native emitting
## anything; the only observable signal is the app regaining focus with a request still
## active. After a short grace period — long enough for a real native response to arrive
## first — this emits [signal recovery_due] so the subsystem can abandon the request.
class_name RequestGuard extends RefCounted

const _DEFAULT_GRACE_SECONDS: float = 1.0

## Emitted when an active request should be treated as abandoned.
signal recovery_due()

var _log: FoundryKitLog
var _is_active: bool = false
var _was_backgrounded: bool = false
var _grace_seconds: float = _DEFAULT_GRACE_SECONDS

func _init(log: FoundryKitLog) -> void:
	_log = log

## Claims the gate. Returns false when a request is already outstanding.
func begin() -> bool:
	if _is_active:
		_log.warn("rejected a request while another is still in progress")
		return false
	_is_active = true
	_was_backgrounded = false
	return true

## Releases the gate.
func end() -> void:
	_is_active = false
	_was_backgrounded = false

func is_active() -> bool:
	return _is_active

func was_backgrounded() -> bool:
	return _was_backgrounded

func set_grace_seconds(seconds: float) -> void:
	_grace_seconds = seconds

## Records that the app lost focus. Only meaningful while a request is active.
func notify_focus_lost() -> void:
	if not _is_active:
		return
	_was_backgrounded = true
	_log.debug("request backgrounded")

## Records that the app regained focus, scheduling recovery when a backgrounded request
## is still outstanding.
func notify_focus_gained() -> void:
	if not _is_active or not _was_backgrounded:
		return
	_log.debug("request returned to foreground; scheduling recovery")
	var tree: SceneTree = Engine.get_main_loop() as SceneTree
	if tree == null:
		recovery_due.emit()
		return
	var timer: SceneTreeTimer = tree.create_timer(_grace_seconds)
	timer.timeout.connect(_on_grace_elapsed)

func _on_grace_elapsed() -> void:
	if not _is_active:
		return
	recovery_due.emit()
```

- [ ] **Step 4: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 9 tests in `RequestGuardTests`.

- [ ] **Step 5: Commit**

```bash
git add addons/FoundryKit/core/RequestGuard.fs test_project/tests/request-guard.test.fs
git commit -m "feat(core): add request guard with foreground recovery"
```

---

### Task 7: Backend factory trait

**Goal:** `BackendFactory[TBackend]` — the generic trait that replaces three copies of an OS-name match chain, resolving to a null backend when a platform is unsupported or a binary is absent.

**Files:**
- Create: `addons/FoundryKit/core/BackendFactory.fs`
- Create: `test_project/tests/support/fake_backends.notest.fs`
- Test: `test_project/tests/backend-factory.test.fs`

**Acceptance Criteria:**
- [ ] A concrete factory satisfying `BackendFactory[T]` compiles and resolves per platform
- [ ] `resolve()` returns the platform backend when `for_platform` yields one
- [ ] `resolve()` returns the null backend when `for_platform` yields `null`
- [ ] `PlatformKind.UNKNOWN` always resolves to the null backend

**Verify:** `task test:foundrylib` → `BackendFactoryTests` all pass

**Steps:**

- [ ] **Step 1: Write the fake backends**

Create `test_project/tests/support/fake_backends.notest.fs`:

```
namespace games.cafecito.foundrykit.tests.support

import games.cafecito.foundrykit.core

## Minimal backend contract used to exercise [BackendFactory] without a real subsystem.
abstract class_name FakeBackend extends RefCounted

abstract func backend_name() -> String

abstract func is_available() -> bool

## Stands in for a working platform backend.
class_name FakePlatformBackend extends FakeBackend

var _name: String = ""

func _init(name: String) -> void:
	_name = name

func backend_name() -> String:
	return _name

func is_available() -> bool:
	return true

## Stands in for the no-op backend used on unsupported platforms and partial installs.
class_name FakeNullBackend extends FakeBackend

func backend_name() -> String:
	return "null"

func is_available() -> bool:
	return false

## A factory that supplies platform backends for Apple and Android only.
class_name FakeBackendFactory extends RefCounted
uses BackendFactory[FakeBackend]

func for_platform(platform: PlatformKind) -> FakeBackend?:
	match platform:
		PlatformKind.IOS, PlatformKind.MACOS:
			return FakePlatformBackend.new("apple")
		PlatformKind.ANDROID:
			return FakePlatformBackend.new("android")
		PlatformKind.DESKTOP, PlatformKind.UNKNOWN:
			return null
	return null

func null_backend() -> FakeBackend:
	return FakeNullBackend.new()
```

If a single `.fs` file cannot declare multiple `class_name` types, split these into
`fake_backend.notest.fs`, `fake_platform_backend.notest.fs`, `fake_null_backend.notest.fs`
and `fake_backend_factory.notest.fs`, one type per file, keeping the same namespace.

- [ ] **Step 2: Write the failing test**

Create `test_project/tests/backend-factory.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit.core
import games.cafecito.foundrykit.tests.support

class_name BackendFactoryTests
extends RefCounted
uses Test

var _factory: FakeBackendFactory

func before_each() -> void:
	_factory = FakeBackendFactory.new()

func test_ios_resolves_to_apple_backend() -> void:
	Expect.that(_factory.resolve(PlatformKind.IOS).backend_name()).to_equal("apple")

func test_macos_resolves_to_apple_backend() -> void:
	Expect.that(_factory.resolve(PlatformKind.MACOS).backend_name()).to_equal("apple")

func test_android_resolves_to_android_backend() -> void:
	Expect.that(_factory.resolve(PlatformKind.ANDROID).backend_name()).to_equal("android")

func test_desktop_resolves_to_null_backend() -> void:
	Expect.that(_factory.resolve(PlatformKind.DESKTOP).backend_name()).to_equal("null")

func test_unknown_resolves_to_null_backend() -> void:
	Expect.that(_factory.resolve(PlatformKind.UNKNOWN).backend_name()).to_equal("null")

func test_null_backend_reports_unavailable() -> void:
	Expect.that(_factory.resolve(PlatformKind.DESKTOP).is_available()).to_be_false()

func test_platform_backend_reports_available() -> void:
	Expect.that(_factory.resolve(PlatformKind.IOS).is_available()).to_be_true()
```

- [ ] **Step 3: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `BackendFactory` is not defined.

- [ ] **Step 4: Create `addons/FoundryKit/core/BackendFactory.fs`**

```
namespace games.cafecito.foundrykit.core

## Selects a subsystem's backend for the running platform.
##
## Implementations supply only [method for_platform] and [method null_backend];
## [method resolve] applies the shared fallback rule. Returning null from
## [method for_platform] covers both an unsupported platform and a subsystem binary the
## consumer removed — the two are deliberately indistinguishable.
trait_name BackendFactory[TBackend]

## Returns the backend for a platform, or null when none applies.
abstract func for_platform(platform: PlatformKind) -> TBackend?

## Returns the no-op backend used whenever no platform backend applies.
abstract func null_backend() -> TBackend

## Returns the backend for a platform, falling back to the null backend.
func resolve(platform: PlatformKind) -> TBackend:
	var selected: TBackend? = for_platform(platform)
	if selected == null:
		return null_backend()
	return selected

## Returns the backend for the platform the game is running on.
func resolve_current() -> TBackend:
	return resolve(Platform.current())
```

If a trait cannot carry a concrete method body alongside `abstract func` requirements,
move `resolve` and `resolve_current` into a `BackendResolver` static helper taking the
factory as a parameter, and update the tests to call
`BackendResolver.resolve(_factory, PlatformKind.IOS)`.

- [ ] **Step 5: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 7 tests in `BackendFactoryTests`.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/core/BackendFactory.fs \
        test_project/tests/support/fake_backends.notest.fs \
        test_project/tests/backend-factory.test.fs
git commit -m "feat(core): add generic backend factory trait"
```

---

### Task 8: FoundryKit autoload facade

**Goal:** The `@autoload` facade owning the root logger, platform reporting, and application lifecycle fan-out to registered guards. Subsystem accessors are added by later plans.

**Files:**
- Create: `addons/FoundryKit/FoundryKit.fs`
- Modify: `test_project/project.foundry` (add the `[autoload]` section)
- Test: `test_project/tests/foundry-kit.test.fs`

**Acceptance Criteria:**
- [ ] Registers as an autoload named `FoundryKit`
- [ ] `set_debug_logging(true)` sets the root logger to `DEBUG`, `false` restores `WARN`
- [ ] Registered guards receive focus-lost and focus-gained notifications
- [ ] Unregistered guards receive nothing
- [ ] `platform()` reports the host platform

**Verify:** `task test:foundrylib` → `FoundryKitTests` all pass

**Steps:**

- [ ] **Step 1: Write the failing test**

Create `test_project/tests/foundry-kit.test.fs`:

```
namespace games.cafecito.foundrykit.tests

import foundry.testlib
import games.cafecito.foundrykit
import games.cafecito.foundrykit.core

class_name FoundryKitTests
extends RefCounted
uses Test

var _kit: FoundryKit

func before_each() -> void:
	_kit = FoundryKit.new()

func after_each() -> void:
	_kit.free()

func test_default_log_level_is_warn() -> void:
	Expect.that(_kit.log().level()).to_equal(LogLevel.WARN)

func test_enabling_debug_logging_sets_debug_level() -> void:
	_kit.set_debug_logging(true)
	Expect.that(_kit.log().level()).to_equal(LogLevel.DEBUG)

func test_disabling_debug_logging_restores_warn_level() -> void:
	_kit.set_debug_logging(true)
	_kit.set_debug_logging(false)
	Expect.that(_kit.log().level()).to_equal(LogLevel.WARN)

func test_platform_matches_detected_platform() -> void:
	Expect.that(_kit.platform()).to_equal(Platform.current())

func test_registered_guard_receives_focus_lost() -> void:
	var guard: RequestGuard = RequestGuard.new(_kit.log())
	_kit.register_guard(guard)
	guard.begin()
	_kit.notify_focus_lost()
	Expect.that(guard.was_backgrounded()).to_be_true()

func test_unregistered_guard_receives_nothing() -> void:
	var guard: RequestGuard = RequestGuard.new(_kit.log())
	_kit.register_guard(guard)
	_kit.unregister_guard(guard)
	guard.begin()
	_kit.notify_focus_lost()
	Expect.that(guard.was_backgrounded()).to_be_false()

func test_registering_the_same_guard_twice_registers_once() -> void:
	var guard: RequestGuard = RequestGuard.new(_kit.log())
	_kit.register_guard(guard)
	_kit.register_guard(guard)
	Expect.that(_kit.guard_count()).to_equal(1)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `task test:foundrylib`
Expected: FAIL — `FoundryKit` is not defined.

- [ ] **Step 3: Create `addons/FoundryKit/FoundryKit.fs`**

```
@autoload
namespace games.cafecito.foundrykit

import games.cafecito.foundrykit.core

## The FoundryKit autoload: the single entry point for every subsystem.
##
## One autoload rather than one per subsystem, so there is a single owner for application
## lifecycle notifications and no autoload ordering to manage. Subsystem accessors are
## lazy, so a game that uses one subsystem never constructs the others.
class_name FoundryKit extends Node

const _ROOT_LOGGER_NAME: String = "foundrykit"

var _log: FoundryKitLog = FoundryKitLog.new(_ROOT_LOGGER_NAME)
var _guards: Array[RequestGuard] = []

## Returns the root logger. Subsystems receive children of it.
func log() -> FoundryKitLog:
	return _log

## Returns the platform family the game is running on.
func platform() -> PlatformKind:
	return Platform.current()

## Enables or disables verbose FoundryKit logging.
func set_debug_logging(enabled: bool) -> void:
	_log.set_level(LogLevel.DEBUG if enabled else LogLevel.WARN)

## Registers a guard to receive application lifecycle notifications.
func register_guard(guard: RequestGuard) -> void:
	if _guards.has(guard):
		return
	_guards.append(guard)

func unregister_guard(guard: RequestGuard) -> void:
	_guards.erase(guard)

func guard_count() -> int:
	return _guards.size()

## Notifies every registered guard that the app lost focus.
func notify_focus_lost() -> void:
	for guard: RequestGuard in _guards:
		guard.notify_focus_lost()

## Notifies every registered guard that the app regained focus.
func notify_focus_gained() -> void:
	for guard: RequestGuard in _guards:
		guard.notify_focus_gained()

func _notification(what: int) -> void:
	match what:
		NOTIFICATION_APPLICATION_FOCUS_OUT, NOTIFICATION_APPLICATION_PAUSED:
			notify_focus_lost()
		NOTIFICATION_APPLICATION_FOCUS_IN, NOTIFICATION_APPLICATION_RESUMED:
			notify_focus_gained()
```

- [ ] **Step 4: Register the autoload in `test_project/project.foundry`**

Insert this section after `[application]`:

```ini
[autoload]

FoundryKit="*res://addons/FoundryKit/FoundryKit.fs"
```

- [ ] **Step 5: Run test to verify it passes**

Run: `task test:foundrylib`
Expected: PASS — 7 tests in `FoundryKitTests`.

- [ ] **Step 6: Commit**

```bash
git add addons/FoundryKit/FoundryKit.fs test_project/project.foundry \
        test_project/tests/foundry-kit.test.fs
git commit -m "feat: add FoundryKit autoload facade"
```

---

### Task 9: Boundary enforcement and CI

**Goal:** Make the architectural rules mechanical — import boundaries and structural contracts checked by scripts, wired into a PR workflow.

**Files:**
- Create: `scripts/test-import-boundaries`
- Create: `scripts/test-foundry-script-strict`
- Create: `.github/workflows/pr-check.yml`
- Test: the scripts are their own tests; they must fail on a seeded violation

**Acceptance Criteria:**
- [ ] `scripts/test-import-boundaries` fails when `core/` imports a subsystem namespace
- [ ] It fails when one subsystem imports another
- [ ] It fails when a non-`internal` file imports an `internal` namespace
- [ ] It passes on the current tree
- [ ] `scripts/test-foundry-script-strict` asserts the facade's autoload, namespace and class declarations
- [ ] `task test:scripts` runs both and exits 0

**Verify:** `task test:scripts` → exits 0; seeding a violation makes it exit 1

**Steps:**

- [ ] **Step 1: Create `scripts/test-import-boundaries`**

```bash
#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
addon="$repo_root/addons/FoundryKit"
status=0

fail() {
    echo "FAIL: $*" >&2
    status=1
}

command -v rg >/dev/null 2>&1 || { echo "ERROR: ripgrep (rg) is required" >&2; exit 2; }

subsystems=(auth purchase mobile)

# Core must not know about any subsystem.
if [ -d "$addon/core" ]; then
    for subsystem in "${subsystems[@]}"; do
        while IFS= read -r hit; do
            [ -n "$hit" ] || continue
            fail "core/ imports subsystem namespace '$subsystem': $hit"
        done < <(rg -l "^import games\.cafecito\.foundrykit\.${subsystem}\b" "$addon/core" || true)
    done
fi

# No subsystem may import another subsystem.
for subsystem in "${subsystems[@]}"; do
    [ -d "$addon/$subsystem" ] || continue
    for other in "${subsystems[@]}"; do
        [ "$subsystem" = "$other" ] && continue
        while IFS= read -r hit; do
            [ -n "$hit" ] || continue
            fail "subsystem '$subsystem' imports subsystem '$other': $hit"
        done < <(rg -l "^import games\.cafecito\.foundrykit\.${other}\b" "$addon/$subsystem" || true)
    done
done

# Only files under an internal/ directory may import an internal namespace.
while IFS= read -r hit; do
    [ -n "$hit" ] || continue
    case "$hit" in
        */internal/*) ;;
        *) fail "non-internal file imports an internal namespace: $hit" ;;
    esac
done < <(rg -l "^import games\.cafecito\.foundrykit\.[a-z]+\.internal\b" "$addon" || true)

if [ "$status" -eq 0 ]; then
    echo "PASS: import boundaries hold"
fi

exit "$status"
```

Then: `chmod +x scripts/test-import-boundaries`

- [ ] **Step 2: Verify the boundary check catches a real violation**

Run:

```bash
mkdir -p addons/FoundryKit/core
printf 'namespace games.cafecito.foundrykit.core\n\nimport games.cafecito.foundrykit.auth\n' \
    > addons/FoundryKit/core/_violation_probe.fs
scripts/test-import-boundaries; echo "exit=$?"
rm addons/FoundryKit/core/_violation_probe.fs
```

Expected: prints `FAIL: core/ imports subsystem namespace 'auth'` and `exit=1`.

Then run `scripts/test-import-boundaries` again on the clean tree.
Expected: prints `PASS: import boundaries hold`, exit 0.

- [ ] **Step 3: Create `scripts/test-foundry-script-strict`**

```bash
#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
addon="$repo_root/addons/FoundryKit"

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

assert_contains() {
    local file=$1
    local pattern=$2
    local message=$3
    rg -q "$pattern" "$file" || fail "$message"
}

command -v rg >/dev/null 2>&1 || { echo "ERROR: ripgrep (rg) is required" >&2; exit 2; }

facade="$addon/FoundryKit.fs"
[[ -f "$facade" ]] || fail "FoundryKit.fs must define the autoload facade"

assert_contains "$facade" '^@autoload$' \
    "FoundryKit.fs must register itself as the FoundryKit autoload"
assert_contains "$facade" '^namespace games\.cafecito\.foundrykit$' \
    "FoundryKit.fs must use the root FoundryKit namespace"
assert_contains "$facade" '^class_name FoundryKit extends Node$' \
    "FoundryKit.fs must be a globally named Node autoload"

for core_file in PlatformKind Platform FoundryKitLog NativeOutcome NativeBridge \
                 NativeRequest RequestGuard BackendFactory
do
    path="$addon/core/${core_file}.fs"
    [[ -f "$path" ]] || fail "core/${core_file}.fs is missing"
    assert_contains "$path" '^namespace games\.cafecito\.foundrykit\.core$' \
        "core/${core_file}.fs must use the core namespace"
done

# Error unions must be matched exhaustively; a wildcard branch defeats the checking that
# makes the union redesign safe.
while IFS= read -r path; do
    [ -n "$path" ] || continue
    fail "core file uses a wildcard match branch over an outcome union: $path"
done < <(rg -l '^\s+NativeOutcome\._:' "$addon" || true)

echo "PASS: Foundry Script structure checks hold"
```

Then: `chmod +x scripts/test-foundry-script-strict`

- [ ] **Step 4: Run both scripts**

Run: `task test:scripts`
Expected: both print `PASS`, exit 0.

- [ ] **Step 5: Create `.github/workflows/pr-check.yml`**

```yaml
name: PR Check

on:
  pull_request:
    branches: [main]

permissions:
  contents: read

jobs:
  scripts:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4

      - name: Install ripgrep
        run: sudo apt-get update && sudo apt-get install -y ripgrep

      - name: Install Task
        uses: arduino/setup-task@v2
        with:
          repo-token: ${{ secrets.GITHUB_TOKEN }}

      - name: Run script checks
        run: task test:scripts

  lint:
    runs-on: ubuntu-latest
    timeout-minutes: 10
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-python@v5
        with:
          python-version: '3.12'

      - name: Install prek
        run: pip install prek

      - name: Run linters
        run: prek run --all-files
```

The `foundry.testlib` suite is not yet in CI: it needs a Foundry engine binary, which has
no published runner image. Wiring that up is a task in the release plan; until then
`task test:foundrylib` is run locally and is a required pre-merge step.

- [ ] **Step 6: Commit**

```bash
git add scripts/test-import-boundaries scripts/test-foundry-script-strict \
        .github/workflows/pr-check.yml
git commit -m "ci: enforce import boundaries and script structure"
```

---

## Definition of Done

- [ ] `task test:foundrylib` passes with suites: `SyntaxProbe`, `Platform`, `FoundryKitLog`, `NativeOutcome`, `NativeBridge`, `NativeRequest`, `RequestGuard`, `BackendFactory`, `FoundryKit`
- [ ] `task test:scripts` passes
- [ ] `task lint` passes
- [ ] `addons/FoundryKit/core/` contains no reference to `auth`, `purchase` or `mobile`
- [ ] No subsystem code exists yet — this plan ships core only

## Follow-on Plans

| Plan | Depends on | Scope |
|------|-----------|-------|
| 2 — Auth | This plan | `auth/` surface, `AuthError`, four result unions, Apple/Android/Desktop/Null backends, Swift + Android natives |
| 3 — Purchase | Plan 2 | `purchase/` surface, validates core generalises beyond auth |
| 4 — Mobile | Plan 1 | `mobile/` surface, GDScript→`.fs`, SwiftGodot→FoundrySwift rebinding |
| 5 — Release | Plans 2–4 | Per-subsystem xcframeworks and AARs, partial-install test, provenance and migration docs, release workflow, testlib in CI |
