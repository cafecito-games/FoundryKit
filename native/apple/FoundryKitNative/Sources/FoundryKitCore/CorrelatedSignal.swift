import Foundation

/// Per-call correlation tokens for the native request/response protocol.
///
/// A native operation echoes the token it was given in both its success and its
/// failure emission, so the script side can tell whether an emission belongs to the
/// request it is currently awaiting. Without this, a late reply from a timed-out
/// request can be misattributed to the next request on the same target — see the
/// design spec's "Protocol requirement: per-call correlation token".
///
/// This type holds no state. `FoundryKitCore` is loaded once and shared by every
/// extension framework, so a stored property here would be shared across subsystems.
public enum CorrelationToken {

    /// Returns a fresh token. UUIDs are used because the value only needs to be
    /// unique within a process, not unguessable.
    public static func make() -> String {
        UUID().uuidString
    }

    /// Returns whether an emission carrying `actual` answers a request expecting
    /// `expected`.
    ///
    /// An empty `expected` means the caller opted out of correlation and accepts any
    /// emission — the behaviour that predates this protocol.
    public static func matches(_ expected: String, _ actual: String) -> Bool {
        if expected.isEmpty {
            return true
        }
        return expected == actual
    }
}
