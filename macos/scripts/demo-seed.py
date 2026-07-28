# 演示数据灌注（README 截图专用）—— 数据集规范见 docs/DEMO-DATASET.md
# 用法: python3 demo-seed.py <db路径>   （对全新空库执行；先由 app 启动一次建表，或本脚本自建）
# 针对 mac 端 schema（nodes TEXT 主键 / ts unix 秒 REAL / engine+summarized，无 summary_pending；
# codenames 的 definition_node/status_node 为 TEXT）。内容与 windows/scripts/demo-seed.py 一致，
# 差异仅 schema 适配：codenames JSON 的 definition 在 mac 端不可为 null（Swift CodenameDef 非可空），
# 状态更新型条目写空串。
import sqlite3, json, datetime, sys

DB = sys.argv[1]
TODAY = datetime.date.today()

def ts(day_offset, hh, mm):
    d = TODAY + datetime.timedelta(days=day_offset)
    return datetime.datetime(d.year, d.month, d.day, hh, mm).astimezone().timestamp()

def cn(*items):
    return json.dumps(
        [{"name": n, "definition": d or "", "status": s} for n, d, s in items],
        ensure_ascii=False)

def kp(*points):
    return json.dumps(list(points), ensure_ascii=False)

# (agent, project, session, ts, text, title, key_points, codenames, result, kind)
nodes = [
    ("claude", "web-console", "wc-1", ts(-1,20,10),
     "帮我规划登录模块改造，把需求整理编号",
     "规划登录模块改造需求", kp("梳理登录/支付/消息三条线", "输出可执行需求清单"),
     cn(("N1","登录页视觉改版","定义"), ("N2","支付流程重构","定义"), ("N3","消息中心优化","定义")),
     "需求编号如下：N1 登录页视觉改版、N2 支付流程重构、N3 消息中心优化", "需求"),
    ("claude", "web-console", "wc-1", ts(-1,21,3),
     "按优先级拆任务：T1: 先做 N1 的页面骨架，T2: 打通 N2 的退款接口",
     "按优先级拆解任务", kp("T1 页面骨架先行", "T2 退款接口打通"),
     cn(("T1","先做 N1 的页面骨架","定义"), ("T2","打通 N2 的退款接口","定义")),
     "任务已登记，开始执行 T1。", "任务"),
    ("codex", "data-pipeline", "dp-1", ts(-1,18,40),
     "调研一下 REQ-AUTH-3 需要的 OAuth 供应商，输出对比表",
     "OAuth 供应商选型调研", kp("价格/合规/接入成本三维对比", "给出推荐结论"),
     cn(("REQ-AUTH-3","第三方账号绑定","提及")),
     "已完成 5 家供应商对比，推荐 Auth0 与自建方案二选一。", "调研"),
    ("zcode", "mobile-app", "ma-1", ts(-1,16,5),
     "排查启动闪退，收集崩溃栈并定位根因",
     "排查冷启动闪退", kp("采集三台设备崩溃栈", "定位冷启动路径"),
     "[]",
     "NPE 在冷启动路径，已加空保护并回归通过。", "修复"),
    ("kimi", "docs-site", "ds-1", ts(-1,15,30),
     "讲解什么是 SSG 与 SSR 的取舍，给出我们文档站的建议",
     "SSG vs SSR 取舍讲解", kp("构建时渲染与请求时渲染对比", "文档站选型建议"),
     "[]",
     "已输出对比笔记：文档站建议 SSG + 局部水合。", "学习"),
    ("claude", "web-console", "wc-1", ts(0,10,24),
     "T1 完成，接下去执行T2",
     "", "[]",
     cn(("T1",None,"完成"), ("T2",None,"进行中")),
     "T2 已开始：退款接口联调中。", "任务"),
    ("codex", "data-pipeline", "dp-1", ts(0,9,15),
     "把清洗任务拆成增量模式，凌晨跑全量、白天跑增量",
     "清洗任务改增量模式", kp("按分区键做增量水位", "全量窗口挪到凌晨"),
     "[]",
     "增量管道已上线，单轮耗时从 42min 降到 6min。", "任务"),
    ("zcode", "mobile-app", "ma-1", ts(0,10,48),
     "评估离线缓存方案，SQLite 与文件分片二选一",
     "离线缓存方案决策", kp("写放大与断电安全对比", "迁移成本评估"),
     "[]",
     "拍板 SQLite + WAL，对比清单已归档。", "决策"),
    ("kimi", "docs-site", "ds-1", ts(0,9,50),
     "把 FAQ 迁移到新目录结构，保留旧链接跳转",
     "FAQ 目录迁移", kp("28 篇批量迁移", "旧链接 301 跳转"),
     "[]",
     "迁移完成，跳转规则已配置。", "其他"),
    ("codex", "data-pipeline", "dp-1", ts(0,11,20),
     "修复昨晚全量作业 OOM 的问题",
     "修复全量作业 OOM", kp("排查分区倾斜", "按 key 重分片"),
     "[]",
     "根因是分区倾斜，已按 key 重分片并复跑通过。", "修复"),
    ("grok", "mobile-app", "ma-1", ts(0,11,5),
     "把崩溃聚合看板接进 CI，每天早八点推一次日报",
     "崩溃看板接入 CI", kp("按版本+机型聚合", "日报八点定时推送"),
     "[]",
     "看板已接入，首份日报明早八点发出。", "任务"),
    ("claude", "web-console", "wc-1", ts(0,11,52),
     "N2完成，N3变更：改为只做红点提醒",
     "", "[]",
     cn(("N2",None,"完成"), ("N3",None,"变更")),
     "状态已同步，词典已更新。", "任务"),
]

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
for i, (agent, proj, sess, t, text, title, kps, cns, result, kind) in enumerate(nodes):
    nid = f"demo-{i:03d}"
    ids[i] = nid
    c.execute(
        "INSERT INTO nodes (id, agent, project, cwd, session_id, ts, text, source_file,"
        " title, key_points, codenames, result_line, engine, summarized, summary_attempts, kind)"
        " VALUES (?,?,?,NULL,?,?,?,?,?,?,?,?,'rule',1,3,?)",
        (nid, agent, proj, sess, t, text, f"demo/{proj}.jsonl", title, kps, cns, result, kind))

def code(name, definition, def_idx, first_ts, occ, status, status_idx, upd_ts, last_ctx):
    c.execute(
        "INSERT INTO codenames (name, definition, definition_node, first_seen, occurrences,"
        " status, status_node, updated, last_context) VALUES (?,?,?,?,?,?,?,?,?)",
        (name, definition, ids[def_idx], first_ts, occ, status,
         ids[status_idx if status_idx is not None else def_idx],
         upd_ts if upd_ts is not None else first_ts, last_ctx))

code("N1", "登录页视觉改版", 0, ts(-1,20,10), 2, "定义", None, None, "")
code("N2", "支付流程重构", 0, ts(-1,20,10), 3, "完成", 10, ts(0,11,52), "N2完成，N3变更：改为只做红点提醒")
code("N3", "消息中心优化", 0, ts(-1,20,10), 3, "变更", 10, ts(0,11,52), "N2完成，N3变更：改为只做红点提醒")
code("T1", "先做 N1 的页面骨架", 1, ts(-1,21,3), 3, "完成", 5, ts(0,10,24), "T1 完成，接下去执行T2")
code("T2", "打通 N2 的退款接口", 1, ts(-1,21,3), 3, "进行中", 5, ts(0,10,24), "T1 完成，接下去执行T2")
code("REQ-AUTH-3", "第三方账号绑定", 2, ts(-1,18,40), 2, "进行中", 2, ts(-1,18,40), "调研 OAuth 供应商对比")

c.commit()
print("demo nodes:", len(nodes), "codenames: 6")
