import Foundation
import Security

/// Status codes mirrored by the script side's Keychain status mapping.
///
/// `keychainStatusSuccess` and `keychainStatusAbsent` are both ordinary answers:
/// "nothing stored yet" is the normal state of a first launch and must never surface
/// as a failure. Every other code is a failure carrying a detail string.
let keychainStatusSuccess = 0
let keychainStatusAbsent = 1
let keychainStatusMissingEntitlement = 2
let keychainStatusInteractionNotAllowed = 3
let keychainStatusFailed = 4

/// The resolved meaning of an `OSStatus` returned by `Security.framework`.
enum KeychainOutcome: Equatable, Sendable {
    /// The operation completed.
    case success
    /// There is no item under this service and account. Not a failure.
    case absent
    /// The operation failed; `detail` is safe to log and names the cause.
    case failure(code: Int, detail: String)

    /// The status code the script side reads.
    var statusCode: Int {
        switch self {
        case .success: return keychainStatusSuccess
        case .absent: return keychainStatusAbsent
        case .failure(let code, _): return code
        }
    }

    /// The detail for a failure, or the empty string for the two ordinary answers.
    var detail: String {
        switch self {
        case .success, .absent: return ""
        case .failure(_, let detail): return detail
        }
    }
}

/// Maps an `OSStatus` onto the outcome vocabulary.
///
/// Every branch is named deliberately, and the final branch reports the raw numeric
/// status rather than collapsing unrecognised values into an anonymous failure — an
/// `OSStatus` nobody anticipated is exactly the case where the number is the only
/// thing that lets a human diagnose it.
///
/// No detail string ever carries the stored value or the account key.
func keychainOutcome(for status: OSStatus) -> KeychainOutcome {
    switch status {
    case errSecSuccess:
        return .success

    // Nothing has been stored yet, or it was erased. The normal first-launch state.
    case errSecItemNotFound:
        return .absent

    // -34018. What an unsigned process produces — including the CI test runner, which
    // has neither a signing identity nor a keychain-access-groups entitlement. Named
    // explicitly so a human reading the log recognises a provisioning problem rather
    // than chasing a defect in the caller.
    case errSecMissingEntitlement:
        return .failure(
            code: keychainStatusMissingEntitlement,
            detail:
                "The Keychain refused the request for want of an entitlement "
                + "(errSecMissingEntitlement, -34018). The process is unsigned or "
                + "carries no keychain-access-groups entitlement. This is a signing "
                + "or provisioning problem, not a fault in the request.")

    // The item exists but the device has not been unlocked since boot, or the item's
    // accessibility class forbids access in the current state.
    case errSecInteractionNotAllowed:
        return .failure(
            code: keychainStatusInteractionNotAllowed,
            detail:
                "The Keychain is not accessible in the current device state "
                + "(errSecInteractionNotAllowed, -25308).")

    case errSecUserCanceled:
        return .failure(
            code: keychainStatusFailed,
            detail: "The Keychain request was cancelled (errSecUserCanceled, -128).")

    // Reported when a second add collides with an existing item. `store` handles this
    // by updating, so reaching the mapping means the update path failed too.
    case errSecDuplicateItem:
        return .failure(
            code: keychainStatusFailed,
            detail:
                "A Keychain item already exists for this service and account and "
                + "could not be updated (errSecDuplicateItem, -25299).")

    case errSecAuthFailed:
        return .failure(
            code: keychainStatusFailed,
            detail: "The Keychain rejected the request's authorization (errSecAuthFailed, -25293).")

    case errSecParam:
        return .failure(
            code: keychainStatusFailed,
            detail: "The Keychain rejected the request's parameters (errSecParam, -50).")

    case errSecAllocate:
        return .failure(
            code: keychainStatusFailed,
            detail: "The Keychain could not allocate memory for the request (errSecAllocate, -108).")

    case errSecNotAvailable:
        return .failure(
            code: keychainStatusFailed,
            detail: "No Keychain is available to this process (errSecNotAvailable, -25291).")

    case errSecDecode:
        return .failure(
            code: keychainStatusFailed,
            detail: "The Keychain could not decode the stored item (errSecDecode, -26275).")

    default:
        return .failure(
            code: keychainStatusFailed,
            detail: "The Keychain reported OSStatus \(status).")
    }
}

/// The service string every FoundryKit auth item is filed under.
///
/// Derived from the host application's bundle identifier so two applications sharing a
/// device — or a keychain access group — cannot collide on one item. The suffix keeps
/// FoundryKit's own items distinguishable from anything else the application stores.
///
/// A process with no bundle identifier is anomalous rather than impossible (a bare
/// command-line tool, for one), so the executable name stands in before the last-resort
/// constant. All three forms are stable across launches, which is what matters: an
/// unstable service string would silently orphan a stored session on every restart.
func keychainService(bundleIdentifier: String?, executableName: String?) -> String {
    if let bundleIdentifier, !bundleIdentifier.isEmpty {
        return "\(bundleIdentifier).foundrykit.auth"
    }
    if let executableName, !executableName.isEmpty {
        return "\(executableName).foundrykit.auth"
    }
    return "games.cafecito.foundrykit.auth"
}

/// The attributes identifying one item: the primary key of a generic password.
///
/// `kSecClassGenericPassword` items are keyed on service and account together, which is
/// what makes a second `SecItemAdd` with the same pair report `errSecDuplicateItem`.
func keychainIdentity(service: String, account: String) -> [String: Any] {
    [
        kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: service,
        kSecAttrAccount as String: account,
    ]
}

/// The query for reading one item back, asking for the stored bytes themselves.
func keychainLoadQuery(service: String, account: String) -> [String: Any] {
    var query = keychainIdentity(service: service, account: account)
    query[kSecReturnData as String] = true
    query[kSecMatchLimit as String] = kSecMatchLimitOne
    return query
}

/// The attributes for adding an item.
///
/// Accessibility is `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`, and both halves
/// are deliberate. `ThisDeviceOnly` keeps the session out of iCloud Keychain sync and
/// out of encrypted backups, so it cannot be lifted onto another device. `AfterFirstUnlock`
/// lets a background launch following a reboot read the session without the player having
/// unlocked the device again — `WhenUnlocked` would fail those launches, and a syncable
/// class would leak the session off the device.
func keychainAddAttributes(service: String, account: String, data: Data) -> [String: Any] {
    var attributes = keychainIdentity(service: service, account: account)
    attributes[kSecValueData as String] = data
    attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
    return attributes
}

/// The attributes for overwriting an existing item's value.
///
/// Only the value and the accessibility class are updated; the service and account are
/// the primary key and travel in the query instead. Re-asserting accessibility means an
/// item written by an older build under a different class is corrected on the next store
/// rather than keeping the weaker one forever.
func keychainUpdateAttributes(data: Data) -> [String: Any] {
    [
        kSecValueData as String: data,
        kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
    ]
}
