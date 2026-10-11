// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "$safeprojectname$",
    products: [
        .executable(name: "$safeprojectname$", targets: ["App"])
    ],
    targets: [
        .executableTarget(name: "App")
    ]
)
