import XCTest
import Testing
@testable import TestSmoke

final class CalculatorTests: XCTestCase {
    func testAddition() { XCTAssertEqual(Calculator().add(2, 3), 5) }
    func testFailure() { XCTAssertEqual(Calculator().add(2, 3), 99, "Intentional adapter smoke failure") }
    func testSkipped() throws { throw XCTSkip("Intentional adapter smoke skip") }
}

@Test func addition() { #expect(Calculator().add(2, 3) == 5) }
@Test func failure() { #expect(Calculator().add(2, 3) == 99) }
@Test(.disabled("Intentional adapter smoke skip")) func skipped() {}
@Test(arguments: [1, 2, 3]) func parameterized(value: Int) { #expect(Calculator().add(value, 0) == value) }
