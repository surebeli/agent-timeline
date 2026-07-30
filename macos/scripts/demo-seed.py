# 演示数据灌注（README 截图专用）—— 数据集规范见 docs/DEMO-DATASET.md
# 用法: python3 demo-seed.py <db路径> [--lang zh|en]   （对全新空库执行）
#
# 结构（STRUCT / CODES）与文案（CONTENT / DEFS）分离，且**结构与 windows/scripts/demo-seed.py
# 逐字段相同**：两端 README 的中英四行要对齐，版面与折行就必须由同一套结构决定。
# 英文文案是从 win 那份程序化搬过来的，不是手抄——两端英文图内容必须逐字一致。
#
# ⚠️ kind 与代号 status 保持中文 rawValue：那是**存储契约**（nodes.kind / codenames.status），
# 翻了会让 kind 过滤与代号状态机一起失效。显示层由 UiText 负责换语言。
#
# 针对 mac 端 schema（nodes TEXT 主键 / ts unix 秒 REAL / engine+summarized，无 summary_pending；
# codenames 的 definition_node/status_node 为 TEXT，且 definition 不可为 null）。
import sqlite3, json, datetime, os, sys
# 演示内容与结构的唯一事实源是 scripts/demo_dataset.py——两端 import 同一份，
# 只各自做 schema 适配与时间单位换算。别把数据抄回本文件（此前两端各存一份时漂过一处）。
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts"))
import demo_dataset as DATA


argv = sys.argv[1:]
LANG = 'zh'
if '--lang' in argv:
    i = argv.index('--lang')
    LANG = argv[i + 1]
    del argv[i:i + 2]
if LANG not in ('zh', 'en'):
    raise SystemExit('--lang 只接受 zh 或 en')
if not argv:
    raise SystemExit('用法: demo-seed.py <db路径> [--lang zh|en]')
DB = argv[0]
TODAY = datetime.date.today()


def ts(day_offset, hh, mm):
    d = TODAY + datetime.timedelta(days=day_offset)
    return datetime.datetime(d.year, d.month, d.day, hh, mm).astimezone().timestamp()


def kp(*points):
    return json.dumps(list(points), ensure_ascii=False)


content = DATA.CONTENT[LANG]
defs = DATA.DEFS[LANG]

c = sqlite3.connect(DB)
c.executescript("""
CREATE TABLE IF NOT EXISTS nodes (
    id TEXT PRIMARY KEY, agent TEXT NOT NULL, project TEXT NOT NULL, cwd TEXT,
    session_id TEXT NOT NULL, ts REAL NOT NULL, text TEXT NOT NULL, source_file TEXT NOT NULL,
    title TEXT, key_points TEXT, codenames TEXT, result_line TEXT, engine TEXT,
    summarized INTEGER NOT NULL DEFAULT 0, summary_attempts INTEGER NOT NULL DEFAULT 0, kind TEXT);
CREATE TABLE IF NOT EXISTS codenames (
    name TEXT PRIMARY KEY, definition TEXT NOT NULL DEFAULT '', definition_node TEXT NOT NULL,
    first_seen REAL NOT NULL, occurrences INTEGER NOT NULL DEFAULT 1,
    status TEXT NOT NULL DEFAULT '', status_node TEXT NOT NULL DEFAULT '',
    updated REAL NOT NULL DEFAULT 0, last_context TEXT NOT NULL DEFAULT '');
CREATE TABLE IF NOT EXISTS file_offsets (path TEXT PRIMARY KEY, offset INTEGER NOT NULL, inode INTEGER NOT NULL);
""")

ids = {}
for i, (agent, proj, sess, when, kind, codes) in enumerate(DATA.NODES):
    t = ts(*when)
    text, title, points, result = content[i]
    nid = "demo-%03d" % i
    ids[i] = nid
    # mac 的 CodenameDef.definition 非可空，状态更新型条目写空串（win 侧写 null）
    codenames = json.dumps(
        [{"name": n, "definition": (defs[n] if st == "定义" else ""), "status": st} for n, st in codes],
        ensure_ascii=False)
    c.execute(
        "INSERT INTO nodes (id, agent, project, cwd, session_id, ts, text, source_file,"
        " title, key_points, codenames, result_line, engine, summarized, summary_attempts, kind)"
        " VALUES (?,?,?,NULL,?,?,?,?,?,?,?,?,'rule',1,3,?)",
        (nid, agent, proj, sess, t, text, "demo/%s.jsonl" % proj,
         title, kp(*points), codenames, result, kind))

for name, def_idx, first_when, occ, status, status_idx, upd_when, ctx_from in DATA.CODES:
    first_ts = ts(*first_when)
    upd_ts = ts(*upd_when) if upd_when is not None else None
    if ctx_from is None:
        last_ctx = ""
    elif ctx_from == "research":
        last_ctx = DATA.RESEARCH_CTX[LANG]     # 独立短摘录，不是命令原文
    else:
        last_ctx = content[ctx_from][0]
    c.execute(
        "INSERT INTO codenames (name, definition, definition_node, first_seen, occurrences,"
        " status, status_node, updated, last_context) VALUES (?,?,?,?,?,?,?,?,?)",
        (name, defs.get(name, ""), ids[def_idx], first_ts, occ, status,
         ids[status_idx if status_idx is not None else def_idx],
         upd_ts if upd_ts is not None else first_ts, last_ctx))

c.commit()
print("demo nodes: %d  codenames: %d  lang: %s" % (len(DATA.NODES), len(DATA.CODES), LANG))
