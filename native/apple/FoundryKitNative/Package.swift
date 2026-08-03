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
        .package(url: "https://github.com/google/GoogleSignIn-iOS", exact: "9.1.0"),
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
                .product(name: "GoogleSignIn", package: "GoogleSignIn-iOS"),
            ],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "FoundryKitCoreTests",
            dependencies: ["FoundryKitCore"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .testTarget(
            name: "FoundryKitAuthTests",
            dependencies: ["FoundryKitAuth"],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
