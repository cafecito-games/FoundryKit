import XCTest
@testable import FoundryKitCore

final class CorrelatedSignalTests: XCTestCase {

    func testTokenIsGeneratedNonEmpty() {
        XCTAssertFalse(CorrelationToken.make().isEmpty)
    }

    func testTokensAreUnique() {
        let first = CorrelationToken.make()
        let second = CorrelationToken.make()
        XCTAssertNotEqual(first, second)
    }

    func testTokenMatchesItself() {
        let token = CorrelationToken.make()
        XCTAssertTrue(CorrelationToken.matches(token, token))
    }

    func testDifferentTokensDoNotMatch() {
        XCTAssertFalse(CorrelationToken.matches(CorrelationToken.make(), CorrelationToken.make()))
    }

    func testEmptyExpectationMatchesAnything() {
        // An empty expected token means "no correlation required" — the
        // pre-correlation behaviour every existing caller relies on.
        XCTAssertTrue(CorrelationToken.matches("", "anything"))
        XCTAssertTrue(CorrelationToken.matches("", ""))
    }
}
