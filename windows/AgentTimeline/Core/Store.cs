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
                     command_hash, title, key_points, codenames, result_line, summary_source, summary_pending)
                VALUES ($agent, $project, $session, $ts, $text, $file, $offset,
                        $hash, $title, $kp, $cn, $rl, $src, $pending);
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
                       summary_source=$src, summary_pending=$pending
                WHERE id=$id;
                """;
            cmd.Parameters.AddWithValue("$title", summary.Title);
            cmd.Parameters.AddWithValue("$kp", JsonSerializer.Serialize(summary.KeyPoints, JsonOpts));
            cmd.Parameters.AddWithValue("$cn", JsonSerializer.Serialize(summary.Codenames, JsonOpts));
            cmd.Parameters.AddWithValue("$rl", (object?)summary.ResultLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", summary.Source.ToString());
            cmd.Parameters.AddWithValue("$pending", pending ? 1 : 0);
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
    /// Pages newest-first. <paramref name="beforeId"/> is an exclusive cursor for lazy loading
    /// (pass long.MaxValue for the first page); <paramref name="project"/> filters when non-null.
    /// </summary>
    public List<TimelineNode> GetRecentNodes(int limit, long beforeId = long.MaxValue, string? project = null)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, agent, project, session_id, ts, text, source_file, source_offset,
                       command_hash, title, key_points, codenames, result_line, summary_source, summary_pending
                FROM nodes
                WHERE id < $before {(project is null ? "" : "AND project = $project")}
                ORDER BY ts DESC, id DESC LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$before", beforeId);
            cmd.Parameters.AddWithValue("$limit", limit);
            if (project is not null) cmd.Parameters.AddWithValue("$project", project);

            var result = new List<TimelineNode>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadNode(reader));
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
                       command_hash, title, key_points, codenames, result_line, summary_source, summary_pending
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
            Source: Enum.TryParse<SummarySource>(r.GetString(13), out var src) ? src : SummarySource.Rule);
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
            cmd.CommandText = "SELECT title, key_points, codenames, result_line, source FROM summaries WHERE command_hash=$h;";
            cmd.Parameters.AddWithValue("$h", commandHash);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return new Summary(
                Title: reader.GetString(0),
                KeyPoints: DeserializeOrEmpty<List<string>>(reader.GetString(1)),
                Codenames: DeserializeOrEmpty<List<CodenameDefinition>>(reader.GetString(2)),
                ResultLine: reader.IsDBNull(3) ? null : reader.GetString(3),
                Source: Enum.TryParse<SummarySource>(reader.GetString(4), out var src) ? src : SummarySource.Rule);
        }
    }

    public void CacheSummary(string commandHash, Summary summary)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO summaries (command_hash, title, key_points, codenames, result_line, source)
                VALUES ($h, $title, $kp, $cn, $rl, $src)
                ON CONFLICT(command_hash) DO UPDATE SET
                    title=excluded.title, key_points=excluded.key_points, codenames=excluded.codenames,
                    result_line=excluded.result_line, source=excluded.source;
                """;
            cmd.Parameters.AddWithValue("$h", commandHash);
            cmd.Parameters.AddWithValue("$title", summary.Title);
            cmd.Parameters.AddWithValue("$kp", JsonSerializer.Serialize(summary.KeyPoints, JsonOpts));
            cmd.Parameters.AddWithValue("$cn", JsonSerializer.Serialize(summary.Codenames, JsonOpts));
            cmd.Parameters.AddWithValue("$rl", (object?)summary.ResultLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", summary.Source.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------ codenames

    public void UpsertCodename(CodenameEntry entry)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO codenames (name, first_seen, defining_node_id, definition, context_excerpt, occurrences)
                VALUES ($name, $seen, $node, $def, $ctx, $occ)
                ON CONFLICT(name) DO UPDATE SET
                    definition=excluded.definition, occurrences=excluded.occurrences;
                """;
            cmd.Parameters.AddWithValue("$name", entry.Name);
            cmd.Parameters.AddWithValue("$seen", entry.FirstSeen.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$node", entry.DefiningNodeId);
            cmd.Parameters.AddWithValue("$def", (object?)entry.Definition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ctx", entry.ContextExcerpt);
            cmd.Parameters.AddWithValue("$occ", entry.Occurrences);
            cmd.ExecuteNonQuery();
        }
    }

    public List<CodenameEntry> GetAllCodenames()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT name, first_seen, defining_node_id, definition, context_excerpt, occurrences FROM codenames;";
            var result = new List<CodenameEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new CodenameEntry
                {
                    Name = reader.GetString(0),
                    FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    DefiningNodeId = reader.GetInt64(2),
                    Definition = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ContextExcerpt = reader.GetString(4),
                    Occurrences = (int)reader.GetInt64(5),
                });
            }
            return result;
        }
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
