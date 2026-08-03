# FoundryKitNative

Apple-platform native code for FoundryKit, as a SwiftPM package.

- `FoundryKitCore` — shared dynamic library, a dependency of every subsystem extension.
  It is loaded once and shared by all of them, so it holds **no mutable static state**.
- `FoundryKitAuth` — the auth subsystem extension.

## Build and test

```bash
./scripts/swift-test
```

Plain `swift test` fails on a clean checkout. Foundry-Swift-Binary declares path-based
`.binaryTarget`s under a `Binaries/` directory that is gitignored in its own repository:
the tag carries only `binaries.json`, naming the release assets and their SHA-256
checksums. SwiftPM resolves the tag by itself and then reports `does not contain a binary
artifact`.

`scripts/prepare-foundryswift-binary` downloads those assets from the **public**
`cafecito-games/Foundry-Swift` releases and verifies them against the manifest checksums.
No token and no authenticated `gh` are required. `scripts/swift-test` runs it and then
`swift test`; run the prepare script directly if you want to use `swift build` or Xcode.

## Pinned dependency

Foundry-Swift-Binary `0.1.0-alpha.2`, exact. See `Package.resolved`.
