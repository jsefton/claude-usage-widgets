// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "ClaudeUsageBar",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "ClaudeUsageBar", targets: ["ClaudeUsageBar"]),
        .library(name: "UsageCore", targets: ["UsageCore"]),
    ],
    targets: [
        // Pure Foundation logic: log parsing, pricing, daily window. Testable anywhere.
        .target(name: "UsageCore"),
        // The macOS menu bar app (SwiftUI / AppKit).
        .executableTarget(name: "ClaudeUsageBar", dependencies: ["UsageCore"]),
        .testTarget(name: "UsageCoreTests", dependencies: ["UsageCore"]),
    ]
)
