import GoogleSignIn
import XCTest

@testable import FoundryKitAuth

final class SignInOutcomeTests: XCTestCase {

    func testEmptyClientIDIsRejected() {
        XCTAssertNil(validatedClientID(nil))
        XCTAssertNil(validatedClientID(""))
    }

    func testNonEmptyClientIDIsAccepted() {
        XCTAssertEqual(
            validatedClientID("abc.apps.googleusercontent.com"),
            "abc.apps.googleusercontent.com")
    }

    func testReversedClientIDReversesDotSeparatedComponents() {
        XCTAssertEqual(
            reversedClientID("123-abc.apps.googleusercontent.com"),
            "com.googleusercontent.apps.123-abc")
    }

    func testCallbackURLSchemeIsRecognisedRegardlessOfCase() {
        XCTAssertTrue(
            hasCallbackURLScheme(
                iosClientID: "123-abc.apps.googleusercontent.com",
                urlSchemes: ["myGame", "COM.GOOGLEUSERCONTENT.APPS.123-ABC"]))
    }

    func testMissingCallbackURLSchemeIsRejected() {
        XCTAssertFalse(
            hasCallbackURLScheme(
                iosClientID: "123-abc.apps.googleusercontent.com",
                urlSchemes: []))
        XCTAssertFalse(
            hasCallbackURLScheme(
                iosClientID: "123-abc.apps.googleusercontent.com",
                urlSchemes: ["com.googleusercontent.apps.999-zzz"]))
    }

    func testEmptyNonceLeavesTheSDKToGenerateOne() {
        XCTAssertNil(normalizedNonce(""))
    }

    func testSuppliedNonceIsPassedThrough() {
        XCTAssertEqual(normalizedNonce("abc123"), "abc123")
    }

    func testOutcomeCarriesTokenAndProfile() {
        let outcome = SignInOutcome.success(
            idToken: "id", email: "a@b.c", displayName: "Ada")
        guard case let .success(idToken, email, displayName) = outcome else {
            return XCTFail("expected success")
        }
        XCTAssertEqual(idToken, "id")
        XCTAssertEqual(email, "a@b.c")
        XCTAssertEqual(displayName, "Ada")
    }

    func testFailureMessagesAreFixedAndNonIdentifying() {
        // Provider diagnostics must never reach the emitted message — they can
        // carry account identifiers. Only fixed strings are emitted.
        guard case let .failure(code, message) = SignInOutcome.cancelled else {
            return XCTFail("expected failure")
        }
        XCTAssertEqual(code, errorCancelled)
        XCTAssertEqual(message, "The player cancelled the sign-in flow.")
    }

    func testNoStoredCredentialMapsToNoCredential() {
        // `restorePreviousSignIn` reports this on a fresh install or after sign-out.
        // It is the ordinary "nobody is signed in" answer, not a failure.
        let error = NSError(
            domain: kGIDSignInErrorDomain,
            code: GIDSignInError.Code.hasNoAuthInKeychain.rawValue)
        guard case let .failure(code, message) = extractOutcome(user: nil, error: error) else {
            return XCTFail("expected failure")
        }
        XCTAssertEqual(code, errorNoCredential)
        XCTAssertEqual(message, "Sign-in returned no credential.")
    }

    func testProviderDiagnosticsNeverReachTheMessage() {
        let error = NSError(
            domain: kGIDSignInErrorDomain,
            code: GIDSignInError.Code.unknown.rawValue,
            userInfo: [NSLocalizedDescriptionKey: "ada@example.com is not permitted"])
        guard case let .failure(code, message) = extractOutcome(user: nil, error: error) else {
            return XCTFail("expected failure")
        }
        XCTAssertEqual(code, errorGeneric)
        XCTAssertEqual(message, "Sign-in failed.")
    }

    func testBusyOutcomeIsFixedAndNonIdentifying() {
        guard case let .failure(code, message) = SignInOutcome.busy else {
            return XCTFail("expected failure")
        }
        XCTAssertEqual(code, errorGeneric)
        XCTAssertEqual(message, "Another sign-in is already in progress.")
    }
}
