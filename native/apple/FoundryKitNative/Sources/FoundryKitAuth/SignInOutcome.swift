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
    static let busy = SignInOutcome.failure(
        code: errorGeneric, message: "Another sign-in is already in progress.")
}

/// Converts a GoogleSignIn completion payload into an outcome.
///
/// A free function with no actor isolation so it can be called synchronously from
/// completion handlers that arrive on any thread.
///
/// The provider's own diagnostic text is deliberately discarded: it can carry account
/// identifiers, and only the fixed messages above are ever emitted.
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
    guard nsError.domain == kGIDSignInErrorDomain else {
        return .generic
    }
    switch nsError.code {
    case GIDSignInError.Code.canceled.rawValue:
        return .cancelled
    // `restorePreviousSignIn` reports this when the player has never signed in or has
    // signed out since. That is the ordinary "nobody is signed in" answer, not a fault.
    case GIDSignInError.Code.hasNoAuthInKeychain.rawValue:
        return .noCredential
    default:
        return .generic
    }
}

/// Returns `raw` when it is a non-empty client ID, otherwise nil.
func validatedClientID(_ raw: String?) -> String? {
    guard let raw, !raw.isEmpty else { return nil }
    return raw
}

/// Maps a caller-supplied nonce onto the value GoogleSignIn expects.
///
/// An empty nonce means the caller did not ask for replay binding of its own, and must
/// become `nil` so the SDK generates a fresh per-request nonce. Passing the empty string
/// through would install a constant, zero-entropy nonce on every request instead.
func normalizedNonce(_ raw: String) -> String? {
    raw.isEmpty ? nil : raw
}

/// Computes the reversed-client-ID URL scheme GoogleSignIn expects in the host
/// app's Info.plist.
func reversedClientID(_ iosClientID: String) -> String {
    iosClientID.split(separator: ".").reversed().joined(separator: ".")
}

/// Returns whether `urlSchemes` declares the reversed client ID that GoogleSignIn needs
/// in order to receive the OAuth callback.
///
/// Without it the SDK raises an Objective-C exception the moment interactive sign-in
/// starts, which no Swift `catch` can intercept. Checking up front turns a host
/// misconfiguration into `isAvailable() == false`, which is what the addon promises for
/// anything it cannot do.
///
/// URL schemes are case-insensitive, so the comparison is too.
func hasCallbackURLScheme(iosClientID: String, urlSchemes: [String]) -> Bool {
    let expected = reversedClientID(iosClientID).lowercased()
    return urlSchemes.contains { $0.lowercased() == expected }
}
