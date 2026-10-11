// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "$safeprojectname$",
    products: [
        .library(name: "$safeprojectname$", targets: ["Library"])
    ],
    targets: [
        .target(name: "Library"),
        .testTarget(name: "LibraryTests", dependencies: ["Library"])
    ]
)
