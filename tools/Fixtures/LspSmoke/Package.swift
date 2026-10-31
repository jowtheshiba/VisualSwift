// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "LspSmoke",
    products: [.executable(name: "LspSmoke", targets: ["LspSmoke"])],
    targets: [.executableTarget(name: "LspSmoke")]
)
