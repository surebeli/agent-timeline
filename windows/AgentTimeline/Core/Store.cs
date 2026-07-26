using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentTimeline.Core;

/// <summary>
/// SQLite persistence — the single write point of the app (mirrors macos Store.swift).
/// WAL mode; one shared connection guarded by a lock (write volume is tiny).
///
/// Tables:
///   nodes         one row per user command (+ denormalized summary columns)
///   summaries     summary cache keyed by command hash (PRD F4: 按命令内容 hash 缓存)
///   codenames     the codename dictionary (PRD F3)
///   file_offsets  per-file incremental tail state (docs/SESSION-FORMATS.md 增量读取约定)
/// </summary>
public sealed class Store : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public Store(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        CreateSchema();

        // Lifecycle migration (2026-07-26, mirrors macos Store.swift): status machine on
        // codenames, phase kind on nodes. Whether the one-time history replay runs is NOT
        // derived from the schema — TimelineCoordinator keys it off the persisted
        // AppSettings.CodenameReplayVersion marker (written only after a completed replay,
        // so a crash mid-replay re-arms).
        AddColumnIfMissing("codenames", "status", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("codenames", "status_node", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("codenames", "updated", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("codenames", "last_context", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("nodes", "kind", "TEXT");
        AddColumnIfMissing("summaries", "kind", "TEXT");
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private void AddColumnIfMissing(string table, string column, string definition)
    {
        if (ColumnExists(table, column)) return;
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private void CreateSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS nodes (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                agent           TEXT NOT NULL,
                project         TEXT NOT NULL,
                session_id      TEXT NOT NULL,
                ts              INTEGER NOT NULL,          -- unix milliseconds (UTC)
                text            TEXT NOT NULL,
                source_file     TEXT NOT NULL,
                source_offset   INTEGER NOT NULL,
                command_hash    TEXT NOT NULL,
                title           TEXT NOT NULL DEFAULT '',
                key_points      TEXT NOT NULL DEFAULT '[]', -- JSON string[]
                codenames       TEXT NOT NULL DEFAULT '[]', -- JSON {name,definition}[]
                result_line     TEXT,
                summary_source  TEXT NOT NULL DEFAULT 'Rule',
                summary_pending INTEGER NOT NULL DEFAULT 0,
                UNIQUE(agent, session_id, ts, command_hash)
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_ts ON nodes(ts DESC);
            CREATE INDEX IF NOT EXISTS idx_nodes_project ON nodes(project);

            CREATE TABLE IF NOT EXISTS summaries (
                command_hash  TEXT PRIMARY KEY,
                title         TEXT NOT NULL,
                key_points    TEXT NOT NULL,
                codenames     TEXT NOT NULL,
                result_line   TEXT,
                source        TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS codenames (
                name             TEXT PRIMARY KEY,
                first_seen       INTEGER NOT NULL,
                defining_node_id INTEGER NOT NULL,
                definition       TEXT,
                context_excerpt  TEXT NOT NULL DEFAULT '',
                occurrences      INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS file_offsets (
                path        TEXT PRIMARY KEY,
                byte_offset INTEGER NOT NULL,
                file_id     TEXT NOT NULL
            );
            """);
    }

    // ---------------------------------------------------------------- nodes

    /// <summary>Inserts a node; returns its id, or -1 when it is a duplicate (already ingested).</summary>
    public long InsertNode(UserCommand cmd, Summary summary, string commandHash, bool summaryPending)
    {
        lock (_gate)
        {
            using var insert = _conn.CreateCommand();
            insert.CommandText = """
                INSERT OR IGNORE INTO nodes
                    (agent, project, session_id, ts, text, source_file, source_offset,
                     command_hash, title, key_points, codenames, result_line, summary_source, summary_pending, kind)
                VALUES ($agent, $project, $session, $ts, $text, $file, $offset,
                        $hash, $title, $kp, $cn, $rl, $src, $pending, $kind);
                """;
            insert.Parameters.AddWithValue("$agent", cmd.Agent.Key());
            insert.Parameters.AddWithValue("$project", cmd.Project);
            insert.Parameters.AddWithValue("$session", cmd.SessionId);
            insert.Parameters.AddWithValue("$ts", cmd.Timestamp.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$text", cmd.Text);
            insert.Parameters.AddWithValue("$file", cmd.SourceFile);
            insert.Parameters.AddWithValue("$offset", cmd.SourceOffset);
            insert.Parameters.AddWithValue("$hash", commandHash);
            insert.Parameters.AddWithValue("$title", summary.Title);
            insert.Parameters.AddWithValue("$kp", JsonSerializer.Serialize(summary.KeyPoints, JsonOpts));
            insert.Parameters.AddWithValue("$cn", JsonSerializer.Serialize(summary.Codenames, JsonOpts));
            insert.Parameters.AddWithValue("$rl", (object?)summary.ResultLine ?? DBNull.Value);
            insert.Parameters.AddWithValue("$src", summary.Source.ToString());
            insert.Parameters.AddWithValue("$pending", summaryPending ? 1 : 0);
            insert.Parameters.AddWithValue("$kind", (object?)summary.Kind ?? DBNull.Value);
            if (insert.ExecuteNonQuery() == 0) return -1;

            using var lastId = _conn.CreateCommand();
            lastId.CommandText = "SELECT last_insert_rowid();";
            return (long)(lastId.ExecuteScalar() ?? -1L);
        }
    }

    public void UpdateSummary(long nodeId, Summary summary, bool pending)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE nodes SET title=$title, key_points=$kp, codenames=$cn,
                       result_line=COALESCE($rl, result_line),
                       summary_source=$src, summary_pending=$pending,
                       kind=COALESCE($kind, kind)
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$title", summary.Title);
            cmd.Parameters.AddWithValue("$kp", JsonSerializer.Serialize(summary.KeyPoints, JsonOpts));
            cmd.Parameters.AddWithValue("$cn", JsonSerializer.Serialize(summary.Codenames, JsonOpts));
            cmd.Parameters.AddWithValue("$rl", (object?)summary.ResultLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", summary.Source.ToString());
            cmd.Parameters.AddWithValue("$pending", pending ? 1 : 0);
            cmd.Parameters.AddWithValue("$kind", (object?)summary.Kind ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Sets the result line on the most recent node of a session; returns that node id, or null.</summary>
    public long? SetResultLine(AgentKind agent, string sessionId, string resultLine)
    {
        lock (_gate)
        {
            using var find = _conn.CreateCommand();
            find.CommandText = """
                SELECT id FROM nodes WHERE agent=$agent AND session_id=$session
                ORDER BY ts DESC, id DESC LIMIT 1;
                """;
            find.Parameters.AddWithValue("$agent", agent.Key());
            find.Parameters.AddWithValue("$session", sessionId);
            var idObj = find.ExecuteScalar();
            if (idObj is null || idObj is DBNull) return null;
            var id = (long)idObj;

            using var update = _conn.CreateCommand();
            update.CommandText = "UPDATE nodes SET result_line=$rl WHERE id=$id;";
            update.Parameters.AddWithValue("$rl", resultLine);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
            return id;
        }
    }

    /// <summary>
    /// Pages newest-first. Cursor is the (ts, id) compound of the previous page's last row —
    /// 排序键与游标键必须一致：多 agent 回填按 root 串行入库会产生「ts 更旧但 id 更大」的行，
    /// 单纯 id 游标会永久跳过它们（M3 实机审计发现）。First page: both cursor params at
    /// long.MaxValue. <paramref name="project"/> / <paramref name="kind"/> filter when non-null.
    /// </summary>
    public List<TimelineNode> GetRecentNodes(
        int limit, long beforeTs = long.MaxValue, long beforeId = long.MaxValue,
        string? project = null, string? kind = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, agent, project, session_id, ts, text, source_file, source_offset,
                       command_hash, title, key_points, codenames, result_line, summary_source, summary_pending, kind
                FROM nodes
                WHERE (ts < $beforeTs OR (ts = $beforeTs AND id < $before)) {(project is null ? "" : "AND project = $project")} {(kind is null ? "" : "AND kind = $kind")}
                ORDER BY ts DESC, id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$beforeTs", beforeTs);
            cmd.Parameters.AddWithValue("$before", beforeId);
            cmd.Parameters.AddWithValue("$limit", limit);
            if (project is not null) cmd.Parameters.AddWithValue("$project", project);
            if (kind is not null) cmd.Parameters.AddWithValue("$kind", kind);

            var result = new List<TimelineNode>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadNode(reader));
            return result;
        }
    }

    /// <summary>Oldest-first full scan for the one-time codename lifecycle replay.</summary>
    public List<TimelineNode> GetAllNodesAscending()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, agent, project, session_id, ts, text, source_file, source_offset,
                       command_hash, title, key_points, codenames, result_line, summary_source, summary_pending, kind
                FROM nodes ORDER BY ts ASC, id ASC;
                """;
            var result = new List<TimelineNode>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadNode(reader));
            return result;
        }
    }

    /// <summary>The command node an assistant reply belongs to (latest node at or before the reply).</summary>
    public long? LatestNodeId(AgentKind agent, string sessionId, DateTimeOffset before)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id FROM nodes WHERE agent=$agent AND session_id=$session AND ts<=$ts
                ORDER BY ts DESC, id DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$agent", agent.Key());
            cmd.Parameters.AddWithValue("$session", sessionId);
            cmd.Parameters.AddWithValue("$ts", before.ToUnixTimeMilliseconds());
            var idObj = cmd.ExecuteScalar();
            return idObj is long id ? id : null;
        }
    }

    /// <summary>
    /// 每个项目按 agent 的节点数（项目下拉的来源标注用）；组内按数量降序，
    /// 首个即该项目的主导 agent。
    /// </summary>
    public List<(string Project, AgentKind Agent, int Count)> GetProjectAgentCounts()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT project, agent, COUNT(*) FROM nodes GROUP BY project, agent ORDER BY project, COUNT(*) DESC;";
            var result = new List<(string, AgentKind, int)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add((reader.GetString(0),
                    AgentKindExtensions.FromKey(reader.GetString(1)),
                    reader.GetInt32(2)));
            }
            return result;
        }
    }

    public List<string> GetProjects()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT project FROM nodes ORDER BY project;";
            var result = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    public TimelineNode? GetNode(long id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, agent, project, session_id, ts, text, source_file, source_offset,
                       command_hash, title, key_points, codenames, result_line, summary_source, summary_pending, kind
                FROM nodes WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadNode(reader) : null;
        }
    }

    private static TimelineNode ReadNode(SqliteDataReader r)
    {
        var command = new UserCommand(
            Agent: AgentKindExtensions.FromKey(r.GetString(1)),
            Project: r.GetString(2),
            SessionId: r.GetString(3),
            Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
            Text: r.GetString(5),
            SourceFile: r.GetString(6),
            SourceOffset: r.GetInt64(7));
        var summary = new Summary(
            Title: r.GetString(9),
            KeyPoints: DeserializeOrEmpty<List<string>>(r.GetString(10)),
            Codenames: DeserializeOrEmpty<List<CodenameDefinition>>(r.GetString(11)),
            ResultLine: r.IsDBNull(12) ? null : r.GetString(12),
            Source: Enum.TryParse<SummarySource>(r.GetString(13), out var src) ? src : SummarySource.Rule,
            Kind: r.IsDBNull(15) ? null : r.GetString(15));
        return new TimelineNode
        {
            Id = r.GetInt64(0),
            Command = command,
            Summary = summary,
            CommandHash = r.GetString(8),
            SummaryPending = r.GetInt64(14) != 0,
        };
    }

    private static T DeserializeOrEmpty<T>(string json) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts) ?? new T(); }
        catch { return new T(); }
    }

    // ------------------------------------------------------------ summaries

    public Summary? GetCachedSummary(string commandHash)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT title, key_points, codenames, result_line, source, kind FROM summaries WHERE command_hash=$h;";
            cmd.Parameters.AddWithValue("$h", commandHash);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new Summary(
                Title: reader.GetString(0),
                KeyPoints: DeserializeOrEmpty<List<string>>(reader.GetString(1)),
                Codenames: DeserializeOrEmpty<List<CodenameDefinition>>(reader.GetString(2)),
                ResultLine: reader.IsDBNull(3) ? null : reader.GetString(3),
                Source: Enum.TryParse<SummarySource>(reader.GetString(4), out var src) ? src : SummarySource.Rule,
                Kind: reader.IsDBNull(5) ? null : reader.GetString(5));
        }
    }

    public void CacheSummary(string commandHash, Summary summary)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO summaries (command_hash, title, key_points, codenames, result_line, source, kind)
                VALUES ($h, $title, $kp, $cn, $rl, $src, $kind)
                ON CONFLICT(command_hash) DO UPDATE SET
                    title=excluded.title, key_points=excluded.key_points, codenames=excluded.codenames,
                    result_line=excluded.result_line, source=excluded.source, kind=excluded.kind;
                """;
            cmd.Parameters.AddWithValue("$h", commandHash);
            cmd.Parameters.AddWithValue("$title", summary.Title);
            cmd.Parameters.AddWithValue("$kp", JsonSerializer.Serialize(summary.KeyPoints, JsonOpts));
            cmd.Parameters.AddWithValue("$cn", JsonSerializer.Serialize(summary.Codenames, JsonOpts));
            cmd.Parameters.AddWithValue("$rl", (object?)summary.ResultLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", summary.Source.ToString());
            cmd.Parameters.AddWithValue("$kind", (object?)summary.Kind ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------ codenames
    // Lifecycle semantics mirror macos Store.swift: RecordCodename = soft sighting,
    // DefineCodename = explicit "N1: xxx" (latest restatement wins, definition change
    // flips status to 变更), TouchCodename = later mention advancing the status machine.

    /// <summary>
    /// Soft sighting (dash-style hit or LLM extraction): bump occurrences and only fill
    /// an EMPTY definition — derivative sources never overwrite one.
    /// </summary>
    public void RecordCodename(string name, string definition, long nodeId, DateTimeOffset seenAt)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO codenames (name, definition, defining_node_id, first_seen, occurrences)
                VALUES ($name, $def, $node, $seen, 1)
                ON CONFLICT(name) DO UPDATE SET
                    occurrences = occurrences + 1,
                    definition = CASE WHEN (codenames.definition IS NULL OR codenames.definition = '')
                                       AND excluded.definition != ''
                                      THEN excluded.definition ELSE codenames.definition END;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$def", definition);
            cmd.Parameters.AddWithValue("$node", nodeId);
            cmd.Parameters.AddWithValue("$seen", seenAt.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Explicit "N1: xxx" definition sighting: the latest restatement wins. A restatement
    /// that changes an existing definition flips status to 变更; the first-seen time and
    /// defining node keep the FIRST record.
    /// </summary>
    public void DefineCodename(string name, string definition, long nodeId, DateTimeOffset at)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO codenames (name, definition, defining_node_id, first_seen, occurrences, status, status_node, updated)
                VALUES ($name, $def, $node, $ts, 1, $defined, $node, $ts)
                ON CONFLICT(name) DO UPDATE SET
                    occurrences = occurrences + 1,
                    status = CASE WHEN codenames.definition IS NOT NULL AND codenames.definition != ''
                                   AND codenames.definition != excluded.definition
                                  THEN $changed ELSE codenames.status END,
                    definition = excluded.definition,
                    status_node = excluded.status_node,
                    updated = excluded.updated;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$def", definition);
            cmd.Parameters.AddWithValue("$node", nodeId);
            cmd.Parameters.AddWithValue("$ts", at.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$defined", CodenameStatus.Defined.Label());
            cmd.Parameters.AddWithValue("$changed", CodenameStatus.Changed.Label());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A later mention of a known codename: bump occurrences (unless the sighting that
    /// discovered it this round already counted — <paramref name="bumpOccurrence"/> false),
    /// remember the context excerpt, and advance the status machine when a signal was seen.
    /// </summary>
    public void TouchCodename(string name, CodenameStatus? status, string context, long nodeId,
        DateTimeOffset at, bool bumpOccurrence = true)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE codenames SET
                    occurrences = occurrences + $bump,
                    last_context = CASE WHEN $ctx != '' THEN $ctx ELSE last_context END,
                    status = CASE WHEN $status != '' THEN $status ELSE status END,
                    status_node = CASE WHEN $status != '' THEN $node ELSE status_node END,
                    updated = $ts
                WHERE name = $name;
                """;
            cmd.Parameters.AddWithValue("$bump", bumpOccurrence ? 1 : 0);
            cmd.Parameters.AddWithValue("$ctx", context);
            cmd.Parameters.AddWithValue("$status", status?.Label() ?? "");
            cmd.Parameters.AddWithValue("$node", nodeId);
            cmd.Parameters.AddWithValue("$ts", at.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$name", name);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Wipes the dictionary (start of the one-time lifecycle replay).</summary>
    public void ClearCodenames()
    {
        lock (_gate)
        {
            Exec("DELETE FROM codenames;");
        }
    }

    public CodenameEntry? GetCodename(string name)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"{CodenameSelect} WHERE name=$name;";
            cmd.Parameters.AddWithValue("$name", name);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadCodename(reader) : null;
        }
    }

    public List<CodenameEntry> GetAllCodenames()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"{CodenameSelect};";
            var result = new List<CodenameEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadCodename(reader));
            return result;
        }
    }

    private const string CodenameSelect = """
        SELECT name, first_seen, defining_node_id, definition, context_excerpt, occurrences,
               status, status_node, updated, last_context
        FROM codenames
        """;

    private static CodenameEntry ReadCodename(SqliteDataReader reader)
    {
        var updatedMs = reader.IsDBNull(8) ? 0L : reader.GetInt64(8);
        return new CodenameEntry
        {
            Name = reader.GetString(0),
            FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            DefiningNodeId = reader.GetInt64(2),
            Definition = reader.IsDBNull(3) ? null : reader.GetString(3),
            ContextExcerpt = reader.GetString(4),
            Occurrences = (int)reader.GetInt64(5),
            Status = reader.IsDBNull(6) ? "" : reader.GetString(6),
            StatusNodeId = reader.IsDBNull(7) ? 0 : reader.GetInt64(7),
            Updated = updatedMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(updatedMs) : null,
            LastContext = reader.IsDBNull(9) ? "" : reader.GetString(9),
        };
    }

    // --------------------------------------------------------- file_offsets

    public (long Offset, string FileId)? GetFileOffset(string path)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT byte_offset, file_id FROM file_offsets WHERE path=$p;";
            cmd.Parameters.AddWithValue("$p", path);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetInt64(0), reader.GetString(1));
        }
    }

    public void SetFileOffset(string path, long offset, string fileId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_offsets (path, byte_offset, file_id) VALUES ($p, $o, $f)
                ON CONFLICT(path) DO UPDATE SET byte_offset=excluded.byte_offset, file_id=excluded.file_id;
                """;
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$o", offset);
            cmd.Parameters.AddWithValue("$f", fileId);
            cmd.ExecuteNonQuery();
        }
    }

    // ----------------------------------------------------------------------

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
