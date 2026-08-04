import Foundation
import Security
import XCTest

@testable import FoundryKitAuth

/// Covers the `OSStatus` mapping and the query dictionaries `iOSKeychain` builds.
///
/// **No test here performs a live Keychain round-trip, and none should be added.**
/// `SecItemAdd` from an unsigned XCTest bundle with no host application returns
/// `errSecMissingEntitlement` (-34018) on macOS, and the repository's `Apple native` CI
/// job runs exactly that way. Making a round-trip pass would mean adding entitlements, a
/// test host application and a signing identity — a far larger change than this native
/// wrapper, and unnecessary: the Foundry Script side is covered against a fake store, and
/// first verification against a real Keychain on real hardware belongs to the device
/// testing epic.
///
/// What is left is what these tests can actually prove: that every status a caller might
/// see is mapped to the right outcome, and that the dictionaries handed to
/// `Security.framework` carry the class, the primary key and the accessibility the design
/// requires.
final class KeychainStatusTests: XCTestCase {

    // MARK: - OSStatus mapping

    func testSuccessMapsToSuccess() {
        XCTAssertEqual(keychainOutcome(for: errSecSuccess), .success)
        XCTAssertEqual(keychainOutcome(for: errSecSuccess).statusCode, keychainStatusSuccess)
    }

    func testNothingStoredYetIsAbsentRatherThanAnError() {
        // A first launch has never written anything. Reporting that as a failure would
        // make every fresh install look like a broken Keychain.
        let outcome = keychainOutcome(for: errSecItemNotFound)
        XCTAssertEqual(outcome, .absent)
        XCTAssertEqual(outcome.statusCode, keychainStatusAbsent)
        XCTAssertEqual(outcome.detail, "")
    }

    func testMissingEntitlementNamesTheEntitlementProblem() {
        // -34018 is what an unsigned process produces, which includes this very test
        // runner. A human reading the log needs to recognise a provisioning problem
        // rather than go looking for a defect in the request.
        let outcome = keychainOutcome(for: errSecMissingEntitlement)
        XCTAssertEqual(outcome.statusCode, keychainStatusMissingEntitlement)
        XCTAssertTrue(outcome.detail.contains("entitlement"), outcome.detail)
        XCTAssertTrue(outcome.detail.contains("-34018"), outcome.detail)
    }

    func testMissingEntitlementUsesTheDocumentedNumericValue() {
        XCTAssertEqual(errSecMissingEntitlement, -34018)
    }

    func testInteractionNotAllowedIsItsOwnFailure() {
        // Distinct from a generic failure: the item exists and the request was
        // well-formed, the device simply is not in a state that permits access.
        let outcome = keychainOutcome(for: errSecInteractionNotAllowed)
        XCTAssertEqual(outcome.statusCode, keychainStatusInteractionNotAllowed)
        XCTAssertFalse(outcome.detail.isEmpty)
    }

    func testRecognisedFailuresCarryANonEmptyDetail() {
        for status in [
            errSecUserCanceled, errSecDuplicateItem, errSecAuthFailed, errSecParam,
            errSecAllocate, errSecNotAvailable, errSecDecode,
        ] {
            let outcome = keychainOutcome(for: status)
            XCTAssertEqual(outcome.statusCode, keychainStatusFailed, "status \(status)")
            XCTAssertFalse(outcome.detail.isEmpty, "status \(status)")
        }
    }

    func testAnUnrecognisedStatusIsReportedWithItsNumericValue() {
        // The mapping must not swallow a status nobody anticipated. The number is the
        // only thing that makes such a case diagnosable at all.
        let outcome = keychainOutcome(for: OSStatus(-99999))
        XCTAssertEqual(outcome.statusCode, keychainStatusFailed)
        XCTAssertTrue(outcome.detail.contains("-99999"), outcome.detail)
    }

    func testSuccessAndAbsentAreDistinguishableFromEveryFailure() {
        XCTAssertNotEqual(keychainStatusSuccess, keychainStatusAbsent)
        let failureCodes = Set([
            keychainStatusMissingEntitlement, keychainStatusInteractionNotAllowed,
            keychainStatusFailed,
        ])
        XCTAssertFalse(failureCodes.contains(keychainStatusSuccess))
        XCTAssertFalse(failureCodes.contains(keychainStatusAbsent))
        XCTAssertEqual(failureCodes.count, 3)
    }

    // MARK: - The service string

    func testServiceIsDerivedFromTheBundleIdentifier() {
        // Two applications on one device must not collide on a single item.
        XCTAssertEqual(
            keychainService(bundleIdentifier: "com.example.game", executableName: "game"),
            "com.example.game.foundrykit.auth")
        XCTAssertNotEqual(
            keychainService(bundleIdentifier: "com.example.game", executableName: nil),
            keychainService(bundleIdentifier: "com.example.other", executableName: nil))
    }

    func testServiceFallsBackWithoutABundleIdentifier() {
        XCTAssertEqual(
            keychainService(bundleIdentifier: nil, executableName: "tool"),
            "tool.foundrykit.auth")
        XCTAssertEqual(
            keychainService(bundleIdentifier: "", executableName: ""),
            "games.cafecito.foundrykit.auth")
    }

    // MARK: - Query construction

    func testIdentityIsAGenericPasswordKeyedOnServiceAndAccount() {
        let identity = keychainIdentity(service: "svc", account: "session")
        XCTAssertEqual(identity[kSecClass as String] as? String, kSecClassGenericPassword as String)
        XCTAssertEqual(identity[kSecAttrService as String] as? String, "svc")
        XCTAssertEqual(identity[kSecAttrAccount as String] as? String, "session")
        // The identity is the primary key alone — carrying a value here would make the
        // update query overwrite the key it is supposed to match on.
        XCTAssertNil(identity[kSecValueData as String])
    }

    func testLoadQueryAsksForTheDataAndASingleMatch() {
        let query = keychainLoadQuery(service: "svc", account: "session")
        XCTAssertEqual(query[kSecReturnData as String] as? Bool, true)
        XCTAssertEqual(
            query[kSecMatchLimit as String] as? String, kSecMatchLimitOne as String)
        XCTAssertEqual(query[kSecClass as String] as? String, kSecClassGenericPassword as String)
        XCTAssertEqual(query[kSecAttrAccount as String] as? String, "session")
    }

    func testAddAttributesCarryTheValueAndTheIdentity() {
        let data = Data([1, 2, 3])
        let attributes = keychainAddAttributes(service: "svc", account: "session", data: data)
        XCTAssertEqual(attributes[kSecValueData as String] as? Data, data)
        XCTAssertEqual(attributes[kSecAttrService as String] as? String, "svc")
        XCTAssertEqual(attributes[kSecAttrAccount as String] as? String, "session")
    }

    func testStoredItemsAreDeviceOnlyAndReadableAfterFirstUnlock() {
        // Both halves are load-bearing. `ThisDeviceOnly` keeps the session out of iCloud
        // Keychain sync and out of encrypted backups; `AfterFirstUnlock` lets a
        // background launch after a reboot read it without a second unlock. A syncable
        // class would leak the session off the device, and `WhenUnlocked` would fail
        // those launches.
        let expected = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly as String
        let added = keychainAddAttributes(service: "svc", account: "session", data: Data())
        XCTAssertEqual(added[kSecAttrAccessible as String] as? String, expected)

        // Re-asserted on update so an item written by an older build under a weaker
        // class is corrected rather than kept forever.
        let updated = keychainUpdateAttributes(data: Data())
        XCTAssertEqual(updated[kSecAttrAccessible as String] as? String, expected)
    }

    func testAccessibilityIsNotASyncableOrWhenUnlockedClass() {
        let accessible =
            keychainAddAttributes(service: "svc", account: "session", data: Data())[
                kSecAttrAccessible as String] as? String
        XCTAssertNotEqual(accessible, kSecAttrAccessibleWhenUnlocked as String)
        XCTAssertNotEqual(accessible, kSecAttrAccessibleAfterFirstUnlock as String)
        XCTAssertNotEqual(accessible, kSecAttrAccessibleWhenUnlockedThisDeviceOnly as String)
    }

    func testUpdateAttributesCarryNoPrimaryKey() {
        // `SecItemUpdate` takes the primary key in its query, not in the changes. An
        // account or service here would rewrite the key and orphan the item.
        let updated = keychainUpdateAttributes(data: Data([9]))
        XCTAssertEqual(updated[kSecValueData as String] as? Data, Data([9]))
        XCTAssertNil(updated[kSecAttrService as String])
        XCTAssertNil(updated[kSecAttrAccount as String])
        XCTAssertNil(updated[kSecClass as String])
    }
}
