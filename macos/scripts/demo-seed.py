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
import sqlite3, json, datetime, sys

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


STRUCT = [
    ("claude", "web-console",   "wc-1", ts(-1, 20, 10), "需求", [("N1", "定义"), ("N2", "定义"), ("N3", "定义")]),
    ("claude", "web-console",   "wc-1", ts(-1, 21, 3),  "任务", [("T1", "定义"), ("T2", "定义")]),
    ("codex",  "data-pipeline", "dp-1", ts(-1, 18, 40), "调研", [("REQ-AUTH-3", "提及")]),
    ("zcode",  "mobile-app",    "ma-1", ts(-1, 16, 5),  "修复", []),
    ("kimi",   "docs-site",     "ds-1", ts(-1, 15, 30), "学习", []),
    ("claude", "web-console",   "wc-1", ts(0, 10, 24),  "任务", [("T1", "完成"), ("T2", "进行中")]),
    ("codex",  "data-pipeline", "dp-1", ts(0, 9, 15),   "任务", []),
    ("zcode",  "mobile-app",    "ma-1", ts(0, 10, 48),  "决策", []),
    ("kimi",   "docs-site",     "ds-1", ts(0, 9, 50),   "其他", []),
    ("codex",  "data-pipeline", "dp-1", ts(0, 11, 20),  "修复", []),
    ("grok",   "mobile-app",    "ma-1", ts(0, 11, 5),   "任务", []),
    ("claude", "web-console",   "wc-1", ts(0, 11, 52),  "任务", [("N2", "完成"), ("N3", "变更")]),
]

# 每条只列随语言变化的部分：(命令原文, 标题, 关键点, 结果行)
# 标题为空串 = 短命令，界面按规则隐去标题（#6 与 #12）。

CONTENT = {
    'zh': [
        ("帮我规划登录模块改造，把需求整理编号",
         "规划登录模块改造需求", ("梳理登录/支付/消息三条线", "输出可执行需求清单"),
         "需求编号如下：N1 登录页视觉改版、N2 支付流程重构、N3 消息中心优化"),
        ("按优先级拆任务：T1: 先做 N1 的页面骨架，T2: 打通 N2 的退款接口",
         "按优先级拆解任务", ("T1 页面骨架先行", "T2 退款接口打通"),
         "任务已登记，开始执行 T1。"),
        ("调研一下 REQ-AUTH-3 需要的 OAuth 供应商，输出对比表",
         "OAuth 供应商选型调研", ("价格/合规/接入成本三维对比", "给出推荐结论"),
         "已完成 5 家供应商对比，推荐 Auth0 与自建方案二选一。"),
        ("排查启动闪退，收集崩溃栈并定位根因",
         "排查冷启动闪退", ("采集三台设备崩溃栈", "定位冷启动路径"),
         "NPE 在冷启动路径，已加空保护并回归通过。"),
        ("讲解什么是 SSG 与 SSR 的取舍，给出我们文档站的建议",
         "SSG vs SSR 取舍讲解", ("构建时渲染与请求时渲染对比", "文档站选型建议"),
         "已输出对比笔记：文档站建议 SSG + 局部水合。"),
        ("T1 完成，接下去执行T2",
         "", (),
         "T2 已开始：退款接口联调中。"),
        ("把清洗任务拆成增量模式，凌晨跑全量、白天跑增量",
         "清洗任务改增量模式", ("按分区键做增量水位", "全量窗口挪到凌晨"),
         "增量管道已上线，单轮耗时从 42min 降到 6min。"),
        ("评估离线缓存方案，SQLite 与文件分片二选一",
         "离线缓存方案决策", ("写放大与断电安全对比", "迁移成本评估"),
         "拍板 SQLite + WAL，对比清单已归档。"),
        ("把 FAQ 迁移到新目录结构，保留旧链接跳转",
         "FAQ 目录迁移", ("28 篇批量迁移", "旧链接 301 跳转"),
         "迁移完成，跳转规则已配置。"),
        ("修复昨晚全量作业 OOM 的问题",
         "修复全量作业 OOM", ("排查分区倾斜", "按 key 重分片"),
         "根因是分区倾斜，已按 key 重分片并复跑通过。"),
        ("把崩溃聚合看板接进 CI，每天早八点推一次日报",
         "崩溃看板接入 CI", ("按版本+机型聚合", "日报八点定时推送"),
         "看板已接入，首份日报明早八点发出。"),
        ("N2完成，N3变更：改为只做红点提醒",
         "", (),
         "状态已同步，词典已更新。"),
    ],
    'en': [
        ("Help me plan the login module rework and number the requirements",
         "Plan the login module rework requirements",
         ("Map out the login / payment / messaging tracks", "Produce an actionable requirement list"),
         "Requirements numbered as follows: N1 login page redesign, N2 payment flow rework, N3 message centre cleanup"),
        ("Break the tasks down by priority: T1: build the N1 page skeleton first, T2: wire up the N2 refund API",
         "Break the tasks down by priority",
         ("T1 page skeleton first", "T2 refund API wired up"),
         "Tasks registered; starting on T1."),
        ("Research the OAuth providers REQ-AUTH-3 needs and give me a comparison table",
         "OAuth provider selection research",
         ("Compare price / compliance / integration cost", "Land on a recommendation"),
         "Compared five providers; recommend choosing between Auth0 and building in-house."),
        ("Investigate the launch crash, collect the stack traces and find the root cause",
         "Investigate the cold-start crash",
         ("Collected stacks from three devices", "Traced it to the cold-start path"),
         "An NPE on the cold-start path; null guard added and the regression passes."),
        ("Explain the SSG vs SSR trade-off and recommend one for our docs site",
         "SSG vs SSR trade-offs explained",
         ("Build-time versus request-time rendering", "A recommendation for the docs site"),
         "Comparison notes written up: SSG plus partial hydration for the docs site."),
        ("T1 done, moving on to T2",
         "", (),
         "T2 started: refund API integration under way."),
        ("Split the cleaning job into incremental mode: full run overnight, incremental by day",
         "Cleaning job switched to incremental",
         ("Incremental watermark by partition key", "Full-run window moved to the early hours"),
         "Incremental pipeline shipped; one pass went from 42min down to 6min."),
        ("Evaluate the offline cache options, SQLite or file sharding",
         "Offline cache decision",
         ("Write amplification versus power-loss safety", "Migration cost estimate"),
         "Decided on SQLite + WAL; the comparison is archived."),
        ("Migrate the FAQ to the new directory structure, keeping the old links redirecting",
         "FAQ directory migration",
         ("28 pages migrated in bulk", "301 redirects for the old links"),
         "Migration done; redirect rules are configured."),
        ("Fix last night's OOM in the full-run job",
         "Fix the full-run job OOM",
         ("Investigated partition skew", "Resharded by key"),
         "Root cause was partition skew; resharded by key and the rerun passes."),
        ("Wire the crash aggregation dashboard into CI and push a daily report at 8am",
         "Crash dashboard wired into CI",
         ("Aggregated by version and device", "Daily report scheduled for 8am"),
         "Dashboard is wired in; the first report goes out at 8am tomorrow."),
        ("N2 done, N3 changed: badge-only notifications from now on",
         "", (),
         "Status synced; the dictionary is up to date."),
    ],
}

# 代号定义（随语言变化）。状态与归属节点在 STRUCT / CODES 里，不在这。

DEFS = {
    'zh': {
        "N1": "登录页视觉改版", "N2": "支付流程重构", "N3": "消息中心优化",
        "T1": "先做 N1 的页面骨架", "T2": "打通 N2 的退款接口",
        "REQ-AUTH-3": "第三方账号绑定",
    },
    'en': {
        "N1": "login page redesign", "N2": "payment flow rework", "N3": "message centre cleanup",
        "T1": "build the N1 page skeleton first", "T2": "wire up the N2 refund API",
        "REQ-AUTH-3": "third-party account linking",
    },
}

# REQ-AUTH-3 的 lastContext 不是某条命令原文，单列；其余代号的 lastContext
# 直接**派生自对应命令原文**（下面 CODES 里的 ctx_from），免得两处各写一份、翻译时漂掉。
RESEARCH_CTX = {
    'zh': "调研 OAuth 供应商对比",
    'en': "OAuth provider comparison research",
}

CODES = [
    ("N1",         0, ts(-1, 20, 10), 2, "定义",   None, None,           None),
    ("N2",         0, ts(-1, 20, 10), 3, "完成",   11,   ts(0, 11, 52),  11),
    ("N3",         0, ts(-1, 20, 10), 3, "变更",   11,   ts(0, 11, 52),  11),
    ("T1",         1, ts(-1, 21, 3),  3, "完成",   5,    ts(0, 10, 24),  5),
    ("T2",         1, ts(-1, 21, 3),  3, "进行中", 5,    ts(0, 10, 24),  5),
    ("REQ-AUTH-3", 2, ts(-1, 18, 40), 2, "进行中", 2,    ts(-1, 18, 40), 'research'),
]


content = CONTENT[LANG]
defs = DEFS[LANG]

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
for i, (agent, proj, sess, t, kind, codes) in enumerate(STRUCT):
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

for name, def_idx, first_ts, occ, status, status_idx, upd_ts, ctx_from in CODES:
    if ctx_from is None:
        last_ctx = ""
    elif isinstance(ctx_from, int):
        last_ctx = content[ctx_from][0]
    else:
        last_ctx = content[2][0]      # 'research' → 调研那条命令
    c.execute(
        "INSERT INTO codenames (name, definition, definition_node, first_seen, occurrences,"
        " status, status_node, updated, last_context) VALUES (?,?,?,?,?,?,?,?,?)",
        (name, defs.get(name, ""), ids[def_idx], first_ts, occ, status,
         ids[status_idx if status_idx is not None else def_idx],
         upd_ts if upd_ts is not None else first_ts, last_ctx))

c.commit()
print("demo nodes: %d  codenames: %d  lang: %s" % (len(STRUCT), len(CODES), LANG))
