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
/// GoogleSignIn invokes completion handlers on the main thread, so each handler asserts
/// that isolation rather than hopping, and emits its signal synchronously. The outcome is
/// extracted from the SDK types before the assertion, so nothing but `Sendable` values
/// crosses into the emitting body.
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
            emit(
                .failure(code: errorUnavailable, message: "Sign-in is unavailable."),
                requestToken: requestToken)
            return
        }
        GIDSignIn.sharedInstance.signIn(
            withPresenting: anchor, hint: nil, additionalScopes: nil, nonce: nonce
        ) { [weak self] result, error in
            let outcome = extractOutcome(user: result?.user, error: error)
            MainActor.assumeIsolated {
                self?.emit(outcome, requestToken: requestToken)
            }
        }
    }

    @Callable
    func signInSilent(requestToken: String) {
        guard isConfigured else {
            emit(
                .failure(code: errorUnavailable, message: "Sign-in is unavailable."),
                requestToken: requestToken)
            return
        }
        GIDSignIn.sharedInstance.restorePreviousSignIn { [weak self] user, error in
            let outcome = extractOutcome(user: user, error: error)
            MainActor.assumeIsolated {
                self?.emit(outcome, requestToken: requestToken)
            }
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
