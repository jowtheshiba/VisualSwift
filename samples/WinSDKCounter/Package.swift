// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "WinSDKCounter",
    products: [.executable(name: "WinSDKCounter", targets: ["WinSDKCounter"])],
    targets: [.executableTarget(name: "WinSDKCounter")]
)
