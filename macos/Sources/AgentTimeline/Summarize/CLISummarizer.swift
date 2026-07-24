import Foundation

/// Summarizes by invoking an installed agent CLI headlessly (`claude -p`,
/// falling back to `codex exec`). Runs inside a dedicated scratch working
/// directory so the CLI's own session files never enter the watched roots.
struct CLISummarizer: Sendable {
    enum CLIKind: String { case claude, codex }

    struct ResolvedCLI: Sendable {
        let kind: CLIKind
        let path: String
    }

    /// Search well-known install locations first, then ask a login shell.
    static func resolve() -> ResolvedCLI? {
        for (kind, name) in [(CLIKind.claude, "claude"), (CLIKind.codex, "codex")] {
            let candidates = [
                "~/.claude/local/claude",
                "/opt/homebrew/bin/\(name)",
                "/usr/local/bin/\(name)",
                "~/.local/bin/\(name)",
                "~/bin/\(name)",
            ].map { ($0 as NSString).expandingTildeInPath }
            if let hit = candidates.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) {
                return ResolvedCLI(kind: kind, path: hit)
            }
            if let shellHit = shellWhich(name) {
                return ResolvedCLI(kind: kind, path: shellHit)
            }
        }
        return nil
    }

    private static func shellWhich(_ name: String) -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-lc", "command -v \(name)"]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        guard (try? process.run()) != nil else { return nil }
        process.waitUntilExit()
        guard process.terminationStatus == 0,
              let data = try? pipe.fileHandleForReading.readToEnd(),
              let path = String(data: data, encoding: .utf8)?
                  .trimmingCharacters(in: .whitespacesAndNewlines),
              !path.isEmpty else { return nil }
        return path
    }

    let cli: ResolvedCLI
    let model: String

    func summarize(_ cmd: UserCommand) throws -> Summary {
        let prompt = SummaryPrompt.build(for: cmd)
        let scratch = AppSettings.summarizerScratchDir
        try FileManager.default.createDirectory(atPath: scratch, withIntermediateDirectories: true)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: cli.path)
        switch cli.kind {
        case .claude:
            var args = ["-p", prompt, "--output-format", "json"]
            if !model.isEmpty { args += ["--model", model] }
            process.arguments = args
        case .codex:
            process.arguments = ["exec", "--json", prompt]
        }
        process.currentDirectoryURL = URL(fileURLWithPath: scratch)
        var env = ProcessInfo.processInfo.environment
        env["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" + (env["PATH"] ?? "")
        process.environment = env

        let stdout = Pipe()
        process.standardOutput = stdout
        // Keep the CLI's stderr for diagnosis — a silent hang is undebuggable.
        let errLogPath = AppSettings.supportDir + "/cli-stderr.log"
        FileManager.default.createFile(atPath: errLogPath, contents: nil)
        process.standardError = FileHandle(forWritingAtPath: errLogPath) ?? FileHandle.nullDevice
        try process.run()

        // Hard timeout so a hung CLI never wedges the queue; SIGKILL if SIGTERM is ignored.
        let deadline = DispatchWorkItem {
            if process.isRunning {
                process.terminate()
                DispatchQueue.global().asyncAfter(deadline: .now() + 5) {
                    if process.isRunning {
                        kill(process.processIdentifier, SIGKILL)
                    }
                }
            }
        }
        DispatchQueue.global().asyncAfter(deadline: .now() + 90, execute: deadline)
        // Read before waitUntilExit: large outputs would deadlock a full pipe otherwise.
        let data = stdout.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        deadline.cancel()

        guard process.terminationStatus == 0,
              let output = String(data: data, encoding: .utf8) else {
            throw SummarizeError.cliFailed(Int(process.terminationStatus))
        }
        guard let summary = SummaryPrompt.parse(unwrap(output), engine: .cli) else {
            throw SummarizeError.badOutput
        }
        return summary
    }

    /// `claude -p --output-format json` wraps the answer in a result envelope;
    /// `codex exec --json` emits JSONL events. Pull the model text out of either.
    private func unwrap(_ output: String) -> String {
        switch cli.kind {
        case .claude:
            if let data = output.data(using: .utf8),
               let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
               let result = obj["result"] as? String {
                return result
            }
            return output
        case .codex:
            var lastMessage = ""
            for line in output.split(separator: "\n") {
                guard let data = line.data(using: .utf8),
                      let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
                else { continue }
                // rollout-style event: {"msg": {"type": "agent_message", "message": "..."}}
                if let msg = obj["msg"] as? [String: Any],
                   msg["type"] as? String == "agent_message",
                   let text = msg["message"] as? String {
                    lastMessage = text
                }
                if let item = obj["item"] as? [String: Any],
                   item["type"] as? String == "agent_message",
                   let text = item["text"] as? String {
                    lastMessage = text
                }
            }
            return lastMessage.isEmpty ? output : lastMessage
        }
    }
}

enum SummarizeError: Error {
    case cliFailed(Int)
    case badOutput
    case notConfigured
    case httpError(Int)
}
