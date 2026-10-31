// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "DebugSmoke",
    products: [.executable(name: "DebugSmoke", targets: ["DebugSmoke"])],
    targets: [.executableTarget(name: "DebugSmoke", path: ".", sources: ["main.swift"])]
)
