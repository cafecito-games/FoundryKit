import FoundrySwift

/// Registers the auth subsystem's native classes.
///
/// One entry symbol per subsystem binary — this is what
/// `FoundryKitAuth.foundryextension` names in its `entry_symbol`.
#initFoundryExtension(
    cdecl: "foundry_kit_auth_entry_point",
    types: [
        iOSGoogleSignIn.self
    ]
)
