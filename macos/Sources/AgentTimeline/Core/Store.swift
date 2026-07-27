import Foundation
import SQLite3

/// Single writer over a WAL sqlite database. All access is funneled through a
/// private serial queue, so the type is safe to share across threads.
final class Store: @unchecked Sendable {
    static let changedNotification = Notification.Name("StoreChanged")

    private var db: OpaquePointer?
    private let queue = DispatchQueue(label: "agent-timeline.store")
    private static let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

    init(path: String) throws {
        try FileManager.default.createDirectory(
            atPath: (path as NSString).deletingLastPathComponent,
            withIntermediateDirectories: true)
        guard sqlite3_open(path, &db) == SQLITE_OK else {
            throw StoreError.openFailed(path)
        }
        exec("PRAGMA journal_mode=WAL")
        exec("PRAGMA synchronous=NORMAL")
        exec("PRAGMA busy_timeout=3000")
        exec("""
        CREATE TABLE IF NOT EXISTS nodes (
            id TEXT PRIMARY KEY,
            agent TEXT NOT NULL,
            project TEXT NOT NULL,
            cwd TEXT,
            session_id TEXT NOT NULL,
            ts REAL NOT NULL,
            text TEXT NOT NULL,
            source_file TEXT NOT NULL,
            title TEXT,
            key_points TEXT,
            codenames TEXT,
            result_line TEXT,
            engine TEXT,
            summarized INTEGER NOT NULL DEFAULT 0,
            summary_attempts INTEGER NOT NULL DEFAULT 0
        )
        """)
        exec("CREATE INDEX IF NOT EXISTS idx_nodes_ts ON nodes(ts DESC)")
        exec("CREATE INDEX IF NOT EXISTS idx_nodes_session ON nodes(agent, session_id, ts DESC)")
        exec("""
        CREATE TABLE IF NOT EXISTS codenames (
            name TEXT PRIMARY KEY,
            definition TEXT NOT NULL DEFAULT '',
            definition_node TEXT NOT NULL,
            first_seen REAL NOT NULL,
            occurrences INTEGER NOT NULL DEFAULT 1
        )
        """)
        exec("""
        CREATE TABLE IF NOT EXISTS file_offsets (
            path TEXT PRIMARY KEY,
            offset INTEGER NOT NULL,
            inode INTEGER NOT NULL
        )
        """)

        // Lifecycle migration (2026-07-26): status machine on codenames, phase
        // kind on nodes. Replay-needed bookkeeping lives in UserDefaults
        // (AppDelegate) so a crash mid-replay re-arms on next launch.
        exec("ALTER TABLE codenames ADD COLUMN status TEXT NOT NULL DEFAULT ''")
        exec("ALTER TABLE codenames ADD COLUMN status_node TEXT NOT NULL DEFAULT ''")
        exec("ALTER TABLE codenames ADD COLUMN updated REAL NOT NULL DEFAULT 0")
        exec("ALTER TABLE codenames ADD COLUMN last_context TEXT NOT NULL DEFAULT ''")
        exec("ALTER TABLE nodes ADD COLUMN kind TEXT")
    }

    deinit { sqlite3_close(db) }

    enum StoreError: Error {
        case openFailed(String)
    }

    // MARK: - Nodes

    /// Returns true if the node was newly inserted.
    @discardableResult
    func insertNodeIfNew(_ cmd: UserCommand) -> Bool {
        queue.sync {
            let sql = """
            INSERT OR IGNORE INTO nodes (id, agent, project, cwd, session_id, ts, text, source_file)
            VALUES (?,?,?,?,?,?,?,?)
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return false }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, cmd.id)
            bind(stmt, 2, cmd.agent.rawValue)
            bind(stmt, 3, cmd.project)
            bind(stmt, 4, cmd.cwd)
            bind(stmt, 5, cmd.sessionId)
            sqlite3_bind_double(stmt, 6, cmd.timestamp.timeIntervalSince1970)
            bind(stmt, 7, cmd.text)
            bind(stmt, 8, cmd.sourceFile)
            sqlite3_step(stmt)
            return sqlite3_changes(db) > 0
        }
    }

    func setSummary(nodeId: String, summary: Summary) {
        queue.sync {
            let sql = """
            UPDATE nodes SET title=?, key_points=?, codenames=?,
                result_line=COALESCE(?, result_line), engine=?, summarized=?,
                kind=COALESCE(?, kind) WHERE id=?
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, summary.title)
            bind(stmt, 2, encodeJSON(summary.keyPoints))
            bind(stmt, 3, encodeJSON(summary.codenames))
            bind(stmt, 4, summary.resultLine)
            bind(stmt, 5, summary.engine)
            sqlite3_bind_int(stmt, 6, summary.isLLM ? 2 : 1)
            bind(stmt, 7, summary.kind)
            bind(stmt, 8, nodeId)
            sqlite3_step(stmt)
        }
    }

    func bumpSummaryAttempts(nodeId: String) -> Int {
        queue.sync {
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(
                db, "UPDATE nodes SET summary_attempts=summary_attempts+1 WHERE id=? RETURNING summary_attempts",
                -1, &stmt, nil) == SQLITE_OK else { return 0 }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, nodeId)
            return sqlite3_step(stmt) == SQLITE_ROW ? Int(sqlite3_column_int(stmt, 0)) : 0
        }
    }

    /// Fill the result line of the newest node in a session. The latest agent
    /// output always wins — LLM summaries never author result lines (the prompt
    /// pins resultLine to null), so there is nothing to protect.
    func setResultLine(agent: AgentKind, sessionId: String, before: Date, line: String) {
        // §3.4-1 兜底：规整链路上任一环节产出空白都不得抹掉已显示的结果行
        // （Kimi 多 ContentPart × 无条件 UPDATE 会放大成可见回归）。
        guard !line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        queue.sync {
            let sql = """
            UPDATE nodes SET result_line=? WHERE id = (
                SELECT id FROM nodes WHERE agent=? AND session_id=? AND ts<=? ORDER BY ts DESC LIMIT 1
            )
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, line)
            bind(stmt, 2, agent.rawValue)
            bind(stmt, 3, sessionId)
            sqlite3_bind_double(stmt, 4, before.timeIntervalSince1970)
            sqlite3_step(stmt)
        }
    }

    /// Give unsummarized nodes a fresh chance, e.g. after the engine settings change.
    func resetSummaryAttempts() {
        queue.sync {
            exec("UPDATE nodes SET summary_attempts=0 WHERE summarized < 2")
        }
    }

    func fetchNodes(limit: Int = 500) -> [TimelineNode] {
        queue.sync {
            let sql = """
            SELECT id, agent, project, cwd, session_id, ts, text, source_file,
                   title, key_points, codenames, result_line, engine, summarized, kind
            FROM nodes ORDER BY ts DESC LIMIT ?
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int(stmt, 1, Int32(limit))
            var out: [TimelineNode] = []
            while sqlite3_step(stmt) == SQLITE_ROW {
                guard let node = readNode(stmt) else { continue }
                out.append(node)
            }
            return out
        }
    }

    func pendingSummaries(limit: Int = 50) -> [UserCommand] {
        queue.sync {
            let sql = """
            SELECT id, agent, project, cwd, session_id, ts, text, source_file,
                   NULL, NULL, NULL, NULL, NULL, 0, NULL
            FROM nodes WHERE summarized < 2 AND summary_attempts < 3
            ORDER BY ts DESC LIMIT ?
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int(stmt, 1, Int32(limit))
            var out: [UserCommand] = []
            while sqlite3_step(stmt) == SQLITE_ROW {
                guard let node = readNode(stmt) else { continue }
                out.append(node.command)
            }
            return out
        }
    }

    // MARK: - Codenames

    /// Soft sighting (dash-style hit or LLM extraction): bump occurrences and
    /// only fill an EMPTY definition — derivative sources never overwrite one.
    func recordCodename(name: String, definition: String, nodeId: String, seenAt: Date) {
        queue.sync {
            let sql = """
            INSERT INTO codenames (name, definition, definition_node, first_seen, occurrences)
            VALUES (?,?,?,?,1)
            ON CONFLICT(name) DO UPDATE SET
                occurrences = occurrences + 1,
                definition = CASE WHEN codenames.definition='' AND excluded.definition!='' THEN excluded.definition ELSE codenames.definition END
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, name)
            bind(stmt, 2, definition)
            bind(stmt, 3, nodeId)
            sqlite3_bind_double(stmt, 4, seenAt.timeIntervalSince1970)
            sqlite3_step(stmt)
        }
    }

    /// Explicit "N1: xxx" definition sighting: the latest restatement wins.
    /// A restatement that changes an existing definition flips status to 变更.
    func defineCodename(name: String, definition: String, nodeId: String, at: Date) {
        queue.sync {
            let sql = """
            INSERT INTO codenames (name, definition, definition_node, first_seen, occurrences, status, status_node, updated)
            VALUES (?,?,?,?,1,?,?,?)
            ON CONFLICT(name) DO UPDATE SET
                occurrences = occurrences + 1,
                status = CASE WHEN codenames.definition != '' AND codenames.definition != excluded.definition
                              THEN '变更' ELSE codenames.status END,
                definition = excluded.definition,
                status_node = excluded.status_node,
                updated = excluded.updated
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, name)
            bind(stmt, 2, definition)
            bind(stmt, 3, nodeId)
            sqlite3_bind_double(stmt, 4, at.timeIntervalSince1970)
            bind(stmt, 5, CodenameStatus.defined.rawValue)
            bind(stmt, 6, nodeId)
            sqlite3_bind_double(stmt, 7, at.timeIntervalSince1970)
            sqlite3_step(stmt)
        }
    }

    /// A later mention of a known codename: bump occurrences (unless the sighting
    /// that discovered it this round already counted), remember the context
    /// excerpt, and advance the status machine when a signal was seen.
    func touchCodename(name: String, status: CodenameStatus?, context: String,
                       nodeId: String, at: Date, bumpOccurrence: Bool = true) {
        queue.sync {
            let sql = """
            UPDATE codenames SET
                occurrences = occurrences + ?6,
                last_context = CASE WHEN ?1 != '' THEN ?1 ELSE last_context END,
                status = CASE WHEN ?2 != '' THEN ?2 ELSE status END,
                status_node = CASE WHEN ?2 != '' THEN ?3 ELSE status_node END,
                updated = ?4
            WHERE name = ?5
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, context)
            bind(stmt, 2, status?.rawValue ?? "")
            bind(stmt, 3, nodeId)
            sqlite3_bind_double(stmt, 4, at.timeIntervalSince1970)
            bind(stmt, 5, name)
            sqlite3_bind_int(stmt, 6, bumpOccurrence ? 1 : 0)
            sqlite3_step(stmt)
        }
    }

    func clearCodenames() {
        queue.sync { exec("DELETE FROM codenames") }
    }

    func fetchCodenames() -> [String: CodenameEntry] {
        queue.sync {
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(
                db, """
                SELECT name, definition, definition_node, first_seen, occurrences,
                       status, status_node, updated, last_context FROM codenames
                """,
                -1, &stmt, nil) == SQLITE_OK else { return [:] }
            defer { sqlite3_finalize(stmt) }
            var out: [String: CodenameEntry] = [:]
            while sqlite3_step(stmt) == SQLITE_ROW {
                var entry = CodenameEntry(
                    name: column(stmt, 0) ?? "",
                    definition: column(stmt, 1) ?? "",
                    definitionNodeId: column(stmt, 2) ?? "",
                    firstSeen: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 3)),
                    occurrences: Int(sqlite3_column_int(stmt, 4)))
                entry.status = column(stmt, 5) ?? ""
                entry.statusNodeId = column(stmt, 6) ?? ""
                let updated = sqlite3_column_double(stmt, 7)
                entry.updated = updated > 0 ? Date(timeIntervalSince1970: updated) : nil
                entry.lastContext = column(stmt, 8) ?? ""
                out[entry.name] = entry
            }
            return out
        }
    }

    /// The command node an assistant reply belongs to.
    func latestNodeId(agent: AgentKind, sessionId: String, before: Date) -> String? {
        queue.sync {
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(
                db, "SELECT id FROM nodes WHERE agent=? AND session_id=? AND ts<=? ORDER BY ts DESC LIMIT 1",
                -1, &stmt, nil) == SQLITE_OK else { return nil }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, agent.rawValue)
            bind(stmt, 2, sessionId)
            sqlite3_bind_double(stmt, 3, before.timeIntervalSince1970)
            guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
            return column(stmt, 0)
        }
    }

    /// Oldest-first full scan for the one-time codename lifecycle replay.
    func fetchAllNodesAscending() -> [TimelineNode] {
        queue.sync {
            let sql = """
            SELECT id, agent, project, cwd, session_id, ts, text, source_file,
                   title, key_points, codenames, result_line, engine, summarized, kind
            FROM nodes ORDER BY ts ASC
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK else { return [] }
            defer { sqlite3_finalize(stmt) }
            var out: [TimelineNode] = []
            while sqlite3_step(stmt) == SQLITE_ROW {
                guard let node = readNode(stmt) else { continue }
                out.append(node)
            }
            return out
        }
    }

    // MARK: - File offsets

    func fileOffset(path: String) -> (offset: Int64, inode: UInt64)? {
        queue.sync {
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(
                db, "SELECT offset, inode FROM file_offsets WHERE path=?", -1, &stmt, nil) == SQLITE_OK
            else { return nil }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, path)
            guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
            return (sqlite3_column_int64(stmt, 0), UInt64(bitPattern: sqlite3_column_int64(stmt, 1)))
        }
    }

    func setFileOffset(path: String, offset: Int64, inode: UInt64) {
        queue.sync {
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(
                db, "INSERT OR REPLACE INTO file_offsets (path, offset, inode) VALUES (?,?,?)",
                -1, &stmt, nil) == SQLITE_OK else { return }
            defer { sqlite3_finalize(stmt) }
            bind(stmt, 1, path)
            sqlite3_bind_int64(stmt, 2, offset)
            sqlite3_bind_int64(stmt, 3, Int64(bitPattern: inode))
            sqlite3_step(stmt)
        }
    }

    // MARK: - Helpers

    private func readNode(_ stmt: OpaquePointer?) -> TimelineNode? {
        guard let agentStr = column(stmt, 1),
              let agent = AgentKind(rawValue: agentStr) else { return nil }
        let cmd = UserCommand(
            agent: agent,
            project: column(stmt, 2) ?? "",
            cwd: column(stmt, 3),
            sessionId: column(stmt, 4) ?? "",
            timestamp: Date(timeIntervalSince1970: sqlite3_column_double(stmt, 5)),
            text: column(stmt, 6) ?? "",
            sourceFile: column(stmt, 7) ?? "")
        var summary: Summary?
        if let title = column(stmt, 8), sqlite3_column_int(stmt, 13) > 0 {
            summary = Summary(
                title: title,
                keyPoints: decodeJSON([String].self, column(stmt, 9)) ?? [],
                codenames: decodeJSON([CodenameDef].self, column(stmt, 10)) ?? [],
                resultLine: column(stmt, 11),
                engine: column(stmt, 12) ?? SummaryEngineKind.rule.rawValue,
                kind: column(stmt, 14))
        }
        return TimelineNode(command: cmd, summary: summary)
    }

    private func exec(_ sql: String) {
        sqlite3_exec(db, sql, nil, nil, nil)
    }

    private func bind(_ stmt: OpaquePointer?, _ index: Int32, _ value: String?) {
        if let value {
            sqlite3_bind_text(stmt, index, value, -1, Self.transient)
        } else {
            sqlite3_bind_null(stmt, index)
        }
    }

    private func column(_ stmt: OpaquePointer?, _ index: Int32) -> String? {
        guard let cString = sqlite3_column_text(stmt, index) else { return nil }
        return String(cString: cString)
    }

    private func encodeJSON<T: Encodable>(_ value: T) -> String {
        guard let data = try? JSONEncoder().encode(value) else { return "[]" }
        return String(data: data, encoding: .utf8) ?? "[]"
    }

    private func decodeJSON<T: Decodable>(_ type: T.Type, _ string: String?) -> T? {
        guard let string, let data = string.data(using: .utf8) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
}
