# 演示数据灌注(README 截图专用) —— 数据集规范见 docs/DEMO-DATASET.md
# 用法: python demo-seed.py [db路径] [--lang zh|en|ja|ko]
#       默认 db = %LOCALAPPDATA%\AgentTimeline\timeline.db，默认 lang = zh
# 注意: 针对 Windows 端 schema(nodes 整型自增 id / ts 毫秒);mac 端 schema 不同
# (TEXT id / ts 秒 / 无 summary_pending),需按 macos/Sources/.../Store.swift 适配,
# 数据内容以 DEMO-DATASET.md 为准保持双端一致。
import sqlite3, json, datetime, os, sys
# 演示内容与结构的唯一事实源是 scripts/demo_dataset.py——两端 import 同一份，
# 只各自做 schema 适配与时间单位换算。别把数据抄回本文件（此前两端各存一份时漂过一处）。
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "scripts"))
import demo_dataset as DATA


argv = [a for a in sys.argv[1:]]
LANG = 'zh'
if '--lang' in argv:
    i = argv.index('--lang')
    LANG = argv[i + 1]
    del argv[i:i + 2]
if LANG not in ('zh', 'en', 'ja', 'ko'):
    raise SystemExit('--lang 只接受 zh / en / ja / ko')
DB = argv[0] if argv else os.path.expandvars(r'%LOCALAPPDATA%\AgentTimeline\timeline.db')

# 时间基准按 DEMO-DATASET.md：D = 拍摄当天、D-1 = 前一天，时区取本机本地时区
# ——时间线必须出现「今天 / 昨天」两个分组。写死绝对日期的话，过了那天再拍，
# 分组就变成「07-27 · Mon」，与 mac 端产出（相对当天）对不上。mac 侧
# macos/scripts/demo-seed.py 一直是相对实现，这里补齐。
TODAY = datetime.date.today()

def ms(day_offset, hh, mm):
    d = TODAY + datetime.timedelta(days=day_offset)
    return int(datetime.datetime(d.year, d.month, d.day, hh, mm).astimezone().timestamp() * 1000)

def kp(*points):
    return json.dumps(list(points), ensure_ascii=False)

# ── 结构与文案分离
#
# 结构（agent / 项目 / session / 时间 / kind / 代号的名字与状态）**两语共用**：
# zh 与 en 两套图必须逐位可比——同样 12 条、同样时间戳、同样代号生命周期，
# 只有文字换语言。结构一旦分叉，README 两行的版面就对不齐了。
#
# ⚠ `kind` 与代号 `status` 落库的是**中文 rawValue**（`需求`/`完成`/…），
#   那是存储契约、**不翻译**：界面靠 UiText 把它们映射成当前语言的显示标签
#   （见 windows/AgentTimeline/UI/UiText.cs）。这里翻了反而会让过滤与状态机失效。

# (agent, project, session, ts, kind, [(代号, 状态), ...])
content = DATA.CONTENT[LANG]
defs = DATA.DEFS[LANG]

c = sqlite3.connect(DB)
ids = {}
for i, (agent, proj, sess, when, kind, codes) in enumerate(DATA.NODES):
    ts = ms(*when)
    text, title, points, result = content[i]
    codenames = json.dumps(
        [{"name": n, "definition": defs[n] if st == "定义" else None, "status": st} for n, st in codes],
        ensure_ascii=False)
    cur = c.execute(
        "INSERT INTO nodes (agent, project, session_id, ts, text, source_file, source_offset,"
        " command_hash, title, key_points, codenames, result_line, summary_source, summary_pending, kind)"
        " VALUES (?,?,?,?,?,?,?,?,?,?,?,?,'Rule',0,?)",
        (agent, proj, sess, ts, text, f"demo/{proj}.jsonl", i * 100, f"demo-{i}",
         title, kp(*points), codenames, result, kind))
    ids[i] = cur.lastrowid

# 代号定义表在共享模块里（DATA.CODES），本文件只做 schema 适配。
# ⚠ 曾经这里留着一份同名的本地 CODES 副本——抽共享模块时漏删的，循环读的一直是
#   DATA.CODES。它是死代码，但**看起来像权威定义**：谁去改那份局部表都会以为改了行为，
#   实际什么都不会变。这类"看着生效其实没接线"的残留比缺代码更难查，已删。
#   （mac 侧 review 时指出，2026-07-30）
for name, def_idx, first_when, occ, status, status_idx, upd_when, ctx_from in DATA.CODES:
    first_ts = ms(*first_when)
    upd_ts = ms(*upd_when) if upd_when is not None else None
    definition = defs[name]
    if ctx_from is None:
        last_ctx = ""
    elif ctx_from == 'research':
        last_ctx = DATA.RESEARCH_CTX[LANG]
    else:
        last_ctx = content[ctx_from][0]
    c.execute(
        "INSERT INTO codenames (name, first_seen, defining_node_id, definition, context_excerpt,"
        " occurrences, status, status_node, updated, last_context) VALUES (?,?,?,?,?,?,?,?,?,?)",
        (name, first_ts, ids[def_idx], definition, definition, occ, status,
         ids[status_idx if status_idx is not None else def_idx],
         upd_ts if upd_ts is not None else first_ts, last_ctx))

c.commit()
print("demo nodes:", len(DATA.NODES), "codenames:", len(DATA.CODES), "lang:", LANG)
