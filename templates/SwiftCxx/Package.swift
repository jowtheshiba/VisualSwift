// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "$safeprojectname$",
    products: [.executable(name: "$safeprojectname$", targets: ["App"])],
    targets: [
        .target(name: "CppMath", publicHeadersPath: "include"),
        .executableTarget(
            name: "App",
            dependencies: ["CppMath"],
            swiftSettings: [.interoperabilityMode(.Cxx)]
        )
    ],
    cxxLanguageStandard: .cxx17
)
