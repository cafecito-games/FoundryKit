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

    /// The token of the sign-in currently awaiting a reply, if any.
    ///
    /// `GIDSignIn` is a singleton that keeps exactly one pending operation, so starting a
    /// second one replaces the first one's completion handler: the earlier request would
    /// never be answered, and the reply it was waiting for would be delivered under the
    /// later request's token. Refusing the overlapping request keeps every emission
    /// attributable to the request that caused it.
    private var inFlightRequestToken: String?

    /// Set when `signOut` runs while a sign-in is still in flight.
    ///
    /// GoogleSignIn offers no way to cancel a started flow, so the credential it stores
    /// when that flow finishes would silently undo the sign-out. The flag makes the late
    /// reply discard that credential and report the sign-in as cancelled instead.
    private var signedOutWhileInFlight = false

    /// Configures GoogleSignIn. The iOS/macOS client ID comes from the host app's
    /// `Info.plist` (`GIDClientID`); `webClientId` becomes the `serverClientID` so the
    /// returned ID token is minted for the game's backend.
    ///
    /// An empty `webClientId`, a missing `GIDClientID`, or a host that does not declare
    /// the reversed-client-ID URL scheme leaves the extension unconfigured, so
    /// `isAvailable()` reports false and no sign-in is attempted.
    @Callable
    func initialize(webClientId: String) {
        guard let iosClientID = validatedClientID(Self.infoPlistClientID()),
            !webClientId.isEmpty,
            hasCallbackURLScheme(iosClientID: iosClientID, urlSchemes: Self.infoPlistURLSchemes())
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
        guard inFlightRequestToken == nil else {
            emit(.busy, requestToken: requestToken)
            return
        }
        inFlightRequestToken = requestToken
        GIDSignIn.sharedInstance.signIn(
            withPresenting: anchor, hint: nil, additionalScopes: nil,
            nonce: normalizedNonce(nonce)
        ) { [weak self] result, error in
            let outcome = extractOutcome(user: result?.user, error: error)
            MainActor.assumeIsolated {
                self?.settle(outcome, requestToken: requestToken)
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
        guard inFlightRequestToken == nil else {
            emit(.busy, requestToken: requestToken)
            return
        }
        inFlightRequestToken = requestToken
        GIDSignIn.sharedInstance.restorePreviousSignIn { [weak self] user, error in
            let outcome = extractOutcome(user: user, error: error)
            MainActor.assumeIsolated {
                self?.settle(outcome, requestToken: requestToken)
            }
        }
    }

    @Callable
    func signOut(requestToken: String) {
        if inFlightRequestToken != nil {
            signedOutWhileInFlight = true
        }
        GIDSignIn.sharedInstance.signOut()
        signOutComplete.emit(requestToken)
    }

    /// Releases the in-flight slot and emits the outcome that answers `requestToken`.
    ///
    /// A sign-out issued while the request was in flight wins: the credential the flow
    /// just stored is discarded and the request reports cancellation, so the player is
    /// left signed out as they asked.
    private func settle(_ outcome: SignInOutcome, requestToken: String) {
        var resolved = outcome
        if inFlightRequestToken == requestToken {
            inFlightRequestToken = nil
            if signedOutWhileInFlight {
                signedOutWhileInFlight = false
                GIDSignIn.sharedInstance.signOut()
                resolved = .cancelled
            }
        }
        emit(resolved, requestToken: requestToken)
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

    private static func infoPlistURLSchemes() -> [String] {
        let urlTypes = Bundle.main.object(forInfoDictionaryKey: "CFBundleURLTypes")
        guard let urlTypes = urlTypes as? [[String: Any]] else {
            return []
        }
        return urlTypes.flatMap { $0["CFBundleURLSchemes"] as? [String] ?? [] }
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
