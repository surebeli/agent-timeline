# 演示数据灌注(README 截图专用) —— 数据集规范见 docs/DEMO-DATASET.md
# 用法: python demo-seed.py [db路径] [--lang zh|en]
#       默认 db = %LOCALAPPDATA%\AgentTimeline\timeline.db，默认 lang = zh
# 注意: 针对 Windows 端 schema(nodes 整型自增 id / ts 毫秒);mac 端 schema 不同
# (TEXT id / ts 秒 / 无 summary_pending),需按 macos/Sources/.../Store.swift 适配,
# 数据内容以 DEMO-DATASET.md 为准保持双端一致。
import sqlite3, json, datetime, os, sys

argv = [a for a in sys.argv[1:]]
LANG = 'zh'
if '--lang' in argv:
    i = argv.index('--lang')
    LANG = argv[i + 1]
    del argv[i:i + 2]
if LANG not in ('zh', 'en'):
    raise SystemExit('--lang 只接受 zh 或 en')
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
STRUCT = [
    ("claude", "web-console",   "wc-1", ms(-1, 20, 10), "需求", [("N1", "定义"), ("N2", "定义"), ("N3", "定义")]),
    ("claude", "web-console",   "wc-1", ms(-1, 21, 3),  "任务", [("T1", "定义"), ("T2", "定义")]),
    ("codex",  "data-pipeline", "dp-1", ms(-1, 18, 40), "调研", [("REQ-AUTH-3", "提及")]),
    ("zcode",  "mobile-app",    "ma-1", ms(-1, 16, 5),  "修复", []),
    ("kimi",   "docs-site",     "ds-1", ms(-1, 15, 30), "学习", []),
    ("claude", "web-console",   "wc-1", ms(0, 10, 24),  "任务", [("T1", "完成"), ("T2", "进行中")]),
    ("codex",  "data-pipeline", "dp-1", ms(0, 9, 15),   "任务", []),
    ("zcode",  "mobile-app",    "ma-1", ms(0, 10, 48),  "决策", []),
    ("kimi",   "docs-site",     "ds-1", ms(0, 9, 50),   "其他", []),
    ("codex",  "data-pipeline", "dp-1", ms(0, 11, 20),  "修复", []),
    ("grok",   "mobile-app",    "ma-1", ms(0, 11, 5),   "任务", []),
    ("claude", "web-console",   "wc-1", ms(0, 11, 52),  "任务", [("N2", "完成"), ("N3", "变更")]),
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

content = CONTENT[LANG]
defs = DEFS[LANG]

c = sqlite3.connect(DB)
ids = {}
for i, (agent, proj, sess, ts, kind, codes) in enumerate(STRUCT):
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

# (代号, 定义节点, 首见时间, 次数, 状态, 状态节点, 更新时间, lastContext 取自哪条命令)
CODES = [
    ("N1",         0, ms(-1, 20, 10), 2, "定义",   None, None,           None),
    ("N2",         0, ms(-1, 20, 10), 3, "完成",   11,   ms(0, 11, 52),  11),
    ("N3",         0, ms(-1, 20, 10), 3, "变更",   11,   ms(0, 11, 52),  11),
    ("T1",         1, ms(-1, 21, 3),  3, "完成",   5,    ms(0, 10, 24),  5),
    ("T2",         1, ms(-1, 21, 3),  3, "进行中", 5,    ms(0, 10, 24),  5),
    ("REQ-AUTH-3", 2, ms(-1, 18, 40), 2, "进行中", 2,    ms(-1, 18, 40), 'research'),
]

for name, def_idx, first_ts, occ, status, status_idx, upd_ts, ctx_from in CODES:
    definition = defs[name]
    if ctx_from is None:
        last_ctx = ""
    elif ctx_from == 'research':
        last_ctx = RESEARCH_CTX[LANG]
    else:
        last_ctx = content[ctx_from][0]
    c.execute(
        "INSERT INTO codenames (name, first_seen, defining_node_id, definition, context_excerpt,"
        " occurrences, status, status_node, updated, last_context) VALUES (?,?,?,?,?,?,?,?,?,?)",
        (name, first_ts, ids[def_idx], definition, definition, occ, status,
         ids[status_idx if status_idx is not None else def_idx],
         upd_ts if upd_ts is not None else first_ts, last_ctx))

c.commit()
print("demo nodes:", len(STRUCT), "codenames:", len(CODES), "lang:", LANG)
