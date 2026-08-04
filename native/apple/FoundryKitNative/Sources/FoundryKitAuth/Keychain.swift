import FoundrySwift
import Foundation
import OSLog
import Security

/// A `Security.framework` generic-password store, exposed to `AppleSecureStore.fs`.
///
/// **Every method here is synchronous.** `SecItemAdd`, `SecItemCopyMatching`,
/// `SecItemUpdate` and `SecItemDelete` return before they yield, so unlike
/// `iOSGoogleSignIn` this surface needs no signal, no correlation token and no
/// request-token bookkeeping: a `@Callable` returns the answer directly. That is worth
/// keeping — awaiting an already-completed coroutine hangs the engine's test runner
/// rather than failing it, and a synchronous native cannot reach that state.
///
/// Items are filed under a service derived from the host application's bundle
/// identifier, so two applications cannot collide on one item, and are written with
/// `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` so the session neither syncs to
/// iCloud Keychain nor rides along in an encrypted backup, while a post-reboot
/// background launch can still read it.
///
/// Stored values are session tokens. No value, and no account key, is ever logged.
@Foundry
class iOSKeychain: RefCounted {

    private static let logger = Logger(
        subsystem: "games.cafecito.foundrykit", category: "auth.keychain")

    /// The service every item is filed under. Fixed for the lifetime of the process.
    private static let service = keychainService(
        bundleIdentifier: Bundle.main.bundleIdentifier,
        executableName: Bundle.main.executableURL?.deletingPathExtension().lastPathComponent)

    /// Written and read only on the main thread — Foundry drives every `@Callable`
    /// there, and nothing in this class hops off it.
    private var debugLogging = false

    /// The detail for the most recent non-success status, or the empty string.
    ///
    /// Safe to read immediately after any call because every call is synchronous: the
    /// status a caller just received and this string describe the same operation.
    private var lastDetail = ""

    /// The bytes read by the most recent successful `load`, awaiting collection.
    private var loadedValue: PackedByteArray?

    /// The Keychain is present on every Apple platform this extension builds for.
    ///
    /// Whether a *particular* process may use it depends on its entitlements, which
    /// cannot be established without attempting an operation; that shows up as a
    /// `keychainStatusMissingEntitlement` status from `store`, `load` or `erase`.
    @Callable
    func isAvailable() -> Bool { true }

    @Callable
    func setDebugLogging(_ enabled: Bool) { debugLogging = enabled }

    /// The service string items are filed under. Exposed for diagnostics.
    @Callable
    func serviceName() -> String { Self.service }

    /// The detail describing the most recent non-success status.
    @Callable
    func lastErrorDetail() -> String { lastDetail }

    /// Writes `value` under `account`, replacing anything already there.
    ///
    /// A generic-password item is keyed on service and account together, so a second
    /// `SecItemAdd` for the same pair reports `errSecDuplicateItem` rather than
    /// overwriting. That case updates the existing item instead, which keeps exactly one
    /// record per account — a delete-then-add would leave the account with no session at
    /// all if the process died between the two calls.
    @Callable
    func store(account: String, value: PackedByteArray) -> Int {
        let data = Self.data(from: value)
        let addStatus = SecItemAdd(
            keychainAddAttributes(service: Self.service, account: account, data: data)
                as CFDictionary,
            nil)
        let status =
            addStatus == errSecDuplicateItem
            ? SecItemUpdate(
                keychainIdentity(service: Self.service, account: account) as CFDictionary,
                keychainUpdateAttributes(data: data) as CFDictionary)
            : addStatus
        logDebug("store (bytes=\(data.count), updated=\(addStatus == errSecDuplicateItem))")
        return settle(keychainOutcome(for: status))
    }

    /// Reads the item stored under `account`.
    ///
    /// Returns a status only; the bytes are collected separately with
    /// `takeLoadedValue()`, so a value is never left sitting in a return slot the caller
    /// might ignore. A status of `keychainStatusAbsent` means nothing has been stored
    /// yet, which is the ordinary state of a first launch and not a failure.
    @Callable
    func load(account: String) -> Int {
        loadedValue = nil
        var item: CFTypeRef?
        let status = SecItemCopyMatching(
            keychainLoadQuery(service: Self.service, account: account) as CFDictionary, &item)
        let outcome = keychainOutcome(for: status)
        guard case .success = outcome else {
            return settle(outcome)
        }
        guard let data = item as? Data else {
            return settle(
                .failure(
                    code: keychainStatusFailed,
                    detail: "The Keychain returned an item that carried no data."))
        }
        loadedValue = Self.packed(from: data)
        logDebug("load (bytes=\(data.count))")
        return settle(.success)
    }

    /// Hands over the bytes read by the last successful `load` and forgets them.
    ///
    /// Clearing on collection keeps the session out of this object for any longer than
    /// the caller needs it, and makes a second call return empty rather than replay a
    /// value from an earlier request.
    @Callable
    func takeLoadedValue() -> PackedByteArray {
        defer { loadedValue = nil }
        return loadedValue ?? PackedByteArray()
    }

    /// Removes the item stored under `account`.
    ///
    /// Deleting something that was never there reports `keychainStatusAbsent`. The
    /// caller's intent is satisfied either way; the distinction is preserved rather than
    /// flattened so a caller that cares can tell.
    @Callable
    func erase(account: String) -> Int {
        let status = SecItemDelete(
            keychainIdentity(service: Self.service, account: account) as CFDictionary)
        logDebug("erase")
        return settle(keychainOutcome(for: status))
    }

    /// Records the outcome's detail and returns its status code.
    private func settle(_ outcome: KeychainOutcome) -> Int {
        lastDetail = outcome.detail
        if case .failure(let code, let detail) = outcome {
            Self.logger.error("keychain failure (code=\(code)): \(detail, privacy: .public)")
        }
        return outcome.statusCode
    }

    private func logDebug(_ message: String) {
        if debugLogging {
            Self.logger.notice("\(message, privacy: .public)")
        }
    }

    private static func data(from packed: PackedByteArray) -> Data {
        let count = packed.size()
        var bytes = [UInt8]()
        bytes.reserveCapacity(Int(count))
        for index in 0..<count {
            bytes.append(UInt8(truncatingIfNeeded: packed.get(index: index)))
        }
        return Data(bytes)
    }

    private static func packed(from data: Data) -> PackedByteArray {
        let packed = PackedByteArray()
        _ = packed.resize(newSize: Int64(data.count))
        for (index, byte) in data.enumerated() {
            packed.set(index: Int64(index), value: Int64(byte))
        }
        return packed
    }
}
