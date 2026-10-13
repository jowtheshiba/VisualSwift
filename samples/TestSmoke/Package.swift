// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "TestSmoke",
    products: [.library(name: "TestSmoke", targets: ["TestSmoke"])],
    targets: [.target(name: "TestSmoke"), .testTarget(name: "TestSmokeTests", dependencies: ["TestSmoke"])]
)
