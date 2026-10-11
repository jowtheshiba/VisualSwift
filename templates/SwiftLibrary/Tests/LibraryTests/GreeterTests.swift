import XCTest
@testable import Library

final class GreeterTests: XCTestCase {
    func testGreeting() {
        XCTAssertEqual(Greeter().greeting(name: "Swift"), "Hello, Swift!")
    }
}
