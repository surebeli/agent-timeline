// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "AgentTimeline",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "AgentTimeline",
            path: "Sources/AgentTimeline"
        ),
        .testTarget(
            name: "AgentTimelineTests",
            dependencies: ["AgentTimeline"],
            path: "Tests/AgentTimelineTests"
        ),
    ]
)
