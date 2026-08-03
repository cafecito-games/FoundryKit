# FoundryKitNative

Apple-platform native code for FoundryKit, as a SwiftPM package.

- `FoundryKitCore` — shared dynamic library, a dependency of every subsystem extension.
  It is loaded once and shared by all of them, so it holds **no mutable static state**.
- `FoundryKitAuth` — the auth subsystem extension.

## Build and test

From the repository root:

```bash
task apple:auth   # xcframework + shared core framework into addons/FoundryKit/bin/
task apple:test   # the Swift suite, through xcodebuild
```

Both go through `xcodegen` and `xcodebuild`. **Plain `swift test` and `swift build` cannot
build this package**, for two reasons that both live upstream in Foundry-Swift-Binary
`0.1.0-alpha.2` and are tracked as FoundryKit#81:

- `FoundrySwiftMacros.artifactbundle` declares `"type": "executable"` in its `info.json`,
  so SwiftPM never registers it as a macro plugin. Every `@Foundry`, `@Signal`,
  `@Callable` and `#initFoundryExtension` fails to expand without an explicit
  `-load-plugin-executable` flag.
- `FoundrySwift.xcframework` ships no binary `.swiftmodule`, only a `.swiftinterface`
  whose first import is the private `FoundryExtensionC` module. Rebuilding from the
  interface drops the module map the original compilation had, so `FoundryExtensionC`
  needs to be on the Swift import search path explicitly.

Both flags live in `project.yml`'s `OTHER_SWIFT_FLAGS`, derived from build settings so no
triple, configuration or derived-data location is baked in. That makes the generated Xcode
project the only entry point that can build the package.

There is a third, unrelated packaging quirk: Foundry-Swift-Binary declares path-based
`.binaryTarget`s under a `Binaries/` directory that is gitignored in its own repository.
The tag carries only `binaries.json`, naming the release assets and their SHA-256
checksums, so resolving the tag reports `does not contain a binary artifact`.
`scripts/prepare-foundryswift-binary` downloads those assets from the **public**
`cafecito-games/Foundry-Swift` releases and verifies them against the manifest checksums —
no token and no authenticated `gh` are required. Both entry points run it automatically,
against the checkout xcodebuild creates under its derived-data path.

## Scripts

| Script | Purpose |
|---|---|
| `scripts/apple-build-support` | Sourced helpers: tool checks, project generation, package preparation, one `xcodebuild build` invocation |
| `scripts/prepare-foundryswift-binary` | Unpacks the Foundry-Swift-Binary artifacts into a checkout. Takes the checkout directory as an optional argument |
| `scripts/xcodebuild-test` | What `task apple:test` runs |

## Pinned dependency

Foundry-Swift-Binary `0.1.0-alpha.2`, exact. See `Package.resolved`.
