# FoundryKit

Native platform integrations for iOS, macOS and Android, delivered as a single Foundry
addon: authentication, purchases, and mobile platform services.

FoundryKit merges three previously independent Godot addons — AuthenticationKit,
PurchaseKit and MobileKit — onto modern Foundry Script. The three source repositories
remain active for bugfixes during the transition; fixes flow old → new only.

## Documents

Read these before making design decisions. They are authoritative over anything inferred
from the code.

- Design spec: `docs/superpowers/specs/2026-08-02-foundrykit-design.md`
- Current plan: `docs/superpowers/plans/2026-08-02-foundrykit-foundation.md`

Each GitHub issue links to the plan task holding its complete code and exact commands.

## Commands

```bash
task test:foundrylib   # Foundry Script suite (foundry.testlib), headless
task test:scripts      # Build-script and boundary regression checks
task lint              # prek run --all-files
task                   # default: test:foundrylib
```

`task test:foundrylib` is the check that matters. It installs packages with `anvil`,
rsyncs the addon into `test_project/`, and runs the engine headless:

```
foundry --headless project test --project test_project \
    --runner res://addons/foundrylib/testlib/cli/run.fs -- --path res://tests
```

Set `FOUNDRY_BIN` to point at a Foundry binary, or put `foundry` on `PATH`. The script
falls back to `/Users/christian/CafecitoGames/Foundry/bin/foundry.macos.editor.dev.arm64`.

Private dependencies (FoundryLib) need `GITHUB_TOKEN`, `GH_TOKEN`, or an authenticated
`gh`; the script resolves them in that order.

### Availability during the foundation work

`Taskfile.yml`, `scripts/test-foundrylib` and `.pre-commit-config.yaml` are created by
issue #2. `task test:scripts` depends on scripts created by issue #11 and will fail until
that lands — use `task test:foundrylib` as the gate before then.

## Prerequisites

| Tool | Purpose | Install |
|------|---------|---------|
| `foundry` | Engine, runs the test suite | Build from the Foundry repo, or download a release |
| `anvil` | Package manager for `packages.toml` | `go install github.com/cafecito-games/foundry-tools/cmd/anvil@latest` |
| `task` | Task runner | `brew install go-task` |
| `prek` | Pre-commit runner | `brew install prek` |
| `rg` | Used by the boundary and contract scripts | `brew install ripgrep` |
| `xcodegen` | Generates the Apple project (later plans) | `brew install xcodegen` |

## Architecture

One addon, modular subsystems, one autoload.

```
addons/FoundryKit/
  FoundryKit.fs        @autoload facade — the only autoload
  core/                shared; knows nothing about any subsystem
  auth/  purchase/  mobile/
  */internal/          backends; never imported by consumers
  bin/{core,auth,purchase,mobile}/
```

Namespaces mirror folders: `games.cafecito.foundrykit`, `.core`, `.auth`,
`.auth.internal`, and so on.

Native binaries are split per subsystem — `FoundryKitCore.framework` as a shared
dependency, plus one xcframework and one `.foundryextension` per subsystem, and four
AARs. A consumer may delete any subsystem's `bin/` directory.

### Invariants

These are enforced by `scripts/test-import-boundaries` and `scripts/test-foundry-script-strict`.
Breaking one is a defect, not a style preference.

1. **`core/` never references a subsystem.** No `auth`, `purchase` or `mobile` import,
   type, or string.
2. **No subsystem imports another subsystem.**
3. **Only files under an `internal/` directory import an `internal` namespace.**
4. **`FoundryKitCore` holds no shared mutable state.** It is loaded once and shared by
   three dynamically loaded frameworks, so logging config and registries are per-instance
   values passed at construction, never statics.
5. **No `_` wildcard branch over an error or outcome union.** Exhaustiveness checking is
   the point of the union redesign: adding a variant must break every site that needs
   updating.
6. **A missing native binary and an unsupported platform resolve identically** — to a
   Null backend reporting `is_available() == false`, never an error.

## Foundry Script conventions

- **Tabs** for indentation, matching AuthenticationKit and PurchaseKit.
- `##` doc comments on every public type and method. `@deprecated`, `@experimental` and
  `@tutorial` are doc-comment directives, not annotations.
- Public API is `async` returning a result tagged union — not paired success/failure
  signals. Signals are reserved for genuinely unsolicited events.
- Tagged unions **cannot be generic** (`enum_name` takes no type parameters), so there is
  no shared `Result[T, E]`. Core speaks one concrete `NativeOutcome`; each subsystem maps
  it into its own typed result union. Classes, traits and functions *are* generic — shared
  behaviour travels through generic traits.
- Every case in a non-tagged-union enum needs an explicit `= expression`. Tagged unions
  reject `=` on every case.
- Prefer full words in identifiers. `definition`, not `def`; `position`, not `pos`.

## Testing

Tests live in `test_project/tests/` as `*.test.fs` and use FoundryLib's `foundry.testlib`
(`uses Test`, `Expect.that(...).to_equal(...)`). Test doubles and helpers are
`*.notest.fs` under `test_project/tests/support/` so the runner skips them.

Vest and GDScript are legacy — MobileKit still uses them upstream, but nothing in
FoundryKit does.

Write the failing test first, watch it fail for the right reason, then implement.

## Pinned dependencies

| Dependency | Version |
|------------|---------|
| FoundryLib | `6df2b4d7ff43c013a4c9e9033c01cdadbdeda19a` |
| Foundry-Swift-Binary | `0.1.0-alpha.2` |

Both are pinned exactly. The Foundry-Swift-Binary bump from `alpha.1` (what
AuthenticationKit and PurchaseKit use) syncs the Foundry alpha.14 extension API and must
be validated against the extension-loading path.

## Pull requests

- One issue per PR. Reference it with `Closes #N`.
- Paste the `task test:foundrylib` summary into the PR body.
- Do not add a compatibility shim for the old signal-based API. The redesign exists to
  remove that pattern; migration is documented instead.
