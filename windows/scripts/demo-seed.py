# 演示数据灌注(README 截图专用) —— 数据集规范见 docs/DEMO-DATASET.md
# 用法: python demo-seed.py [db路径]   (默认 %LOCALAPPDATA%\AgentTimeline\timeline.db)
# 注意: 针对 Windows 端 schema(nodes 整型自增 id / ts 毫秒);mac 端 schema 不同
# (TEXT id / ts 秒 / 无 summary_pending),需按 macos/Sources/.../Store.swift 适配,
# 数据内容以 DEMO-DATASET.md 为准保持双端一致。
import sqlite3, json, datetime, os, sys

DB = sys.argv[1] if len(sys.argv) > 1 else os.path.expandvars(r'%LOCALAPPDATA%\AgentTimeline\timeline.db')
TZ = datetime.timezone(datetime.timedelta(hours=8))

def ms(mon, day, hh, mm):
    return int(datetime.datetime(2026, mon, day, hh, mm, tzinfo=TZ).timestamp() * 1000)

def cn(*items):
    return json.dumps([{"name": n, "definition": d, "status": s} for n, d, s in items], ensure_ascii=False)

def kp(*points):
    return json.dumps(list(points), ensure_ascii=False)

# (agent, project, session, ts, text, title, key_points, codenames, result, kind)
nodes = [
    ("claude", "web-console", "wc-1", ms(7,26,20,10),
     "帮我规划登录模块改造，把需求整理编号",
     "规划登录模块改造需求", kp("梳理登录/支付/消息三条线", "输出可执行需求清单"),
     cn(("N1","登录页视觉改版","定义"), ("N2","支付流程重构","定义"), ("N3","消息中心优化","定义")),
     "需求编号如下：N1 登录页视觉改版、N2 支付流程重构、N3 消息中心优化", "需求"),
    ("claude", "web-console", "wc-1", ms(7,26,21,3),
     "按优先级拆任务：T1: 先做 N1 的页面骨架，T2: 打通 N2 的退款接口",
     "按优先级拆解任务", kp("T1 页面骨架先行", "T2 退款接口打通"),
     cn(("T1","先做 N1 的页面骨架","定义"), ("T2","打通 N2 的退款接口","定义")),
     "任务已登记，开始执行 T1。", "任务"),
    ("codex", "data-pipeline", "dp-1", ms(7,26,18,40),
     "调研一下 REQ-AUTH-3 需要的 OAuth 供应商，输出对比表",
     "OAuth 供应商选型调研", kp("价格/合规/接入成本三维对比", "给出推荐结论"),
     cn(("REQ-AUTH-3","第三方账号绑定","提及"),),
     "已完成 5 家供应商对比，推荐 Auth0 与自建方案二选一。", "调研"),
    ("zcode", "mobile-app", "ma-1", ms(7,26,16,5),
     "排查启动闪退，收集崩溃栈并定位根因",
     "排查冷启动闪退", kp("采集三台设备崩溃栈", "定位冷启动路径"),
     "[]",
     "NPE 在冷启动路径，已加空保护并回归通过。", "修复"),
    ("kimi", "docs-site", "ds-1", ms(7,26,15,30),
     "讲解什么是 SSG 与 SSR 的取舍，给出我们文档站的建议",
     "SSG vs SSR 取舍讲解", kp("构建时渲染与请求时渲染对比", "文档站选型建议"),
     "[]",
     "已输出对比笔记：文档站建议 SSG + 局部水合。", "学习"),
    ("claude", "web-console", "wc-1", ms(7,27,10,24),
     "T1 完成，接下去执行T2",
     "", "[]",
     cn(("T1",None,"完成"), ("T2",None,"进行中")),
     "T2 已开始：退款接口联调中。", "任务"),
    ("codex", "data-pipeline", "dp-1", ms(7,27,9,15),
     "把清洗任务拆成增量模式，凌晨跑全量、白天跑增量",
     "清洗任务改增量模式", kp("按分区键做增量水位", "全量窗口挪到凌晨"),
     "[]",
     "增量管道已上线，单轮耗时从 42min 降到 6min。", "任务"),
    ("zcode", "mobile-app", "ma-1", ms(7,27,10,48),
     "评估离线缓存方案，SQLite 与文件分片二选一",
     "离线缓存方案决策", kp("写放大与断电安全对比", "迁移成本评估"),
     "[]",
     "拍板 SQLite + WAL，对比清单已归档。", "决策"),
    ("kimi", "docs-site", "ds-1", ms(7,27,9,50),
     "把 FAQ 迁移到新目录结构，保留旧链接跳转",
     "FAQ 目录迁移", kp("28 篇批量迁移", "旧链接 301 跳转"),
     "[]",
     "迁移完成，跳转规则已配置。", "其他"),
    ("codex", "data-pipeline", "dp-1", ms(7,27,11,20),
     "修复昨晚全量作业 OOM 的问题",
     "修复全量作业 OOM", kp("排查分区倾斜", "按 key 重分片"),
     "[]",
     "根因是分区倾斜，已按 key 重分片并复跑通过。", "修复"),
    ("claude", "web-console", "wc-1", ms(7,27,11,52),
     "N2完成，N3变更：改为只做红点提醒",
     "", "[]",
     cn(("N2",None,"完成"), ("N3",None,"变更")),
     "状态已同步，词典已更新。", "任务"),
]

c = sqlite3.connect(DB)
ids = {}
for i, (agent, proj, sess, ts, text, title, kps, cns, result, kind) in enumerate(nodes):
    cur = c.execute(
        "INSERT INTO nodes (agent, project, session_id, ts, text, source_file, source_offset,"
        " command_hash, title, key_points, codenames, result_line, summary_source, summary_pending, kind)"
        " VALUES (?,?,?,?,?,?,?,?,?,?,?,?,'Rule',0,?)",
        (agent, proj, sess, ts, text, f"demo/{proj}.jsonl", i * 100, f"demo-{i}", title, kps, cns, result, kind))
    ids[i] = cur.lastrowid

def code(name, definition, defining_idx, first_ts, occ, status, status_idx, upd_ts, last_ctx):
    status_node = ids[status_idx] if status_idx is not None else ids[defining_idx]
    c.execute(
        "INSERT INTO codenames (name, first_seen, defining_node_id, definition, context_excerpt,"
        " occurrences, status, status_node, updated, last_context) VALUES (?,?,?,?,?,?,?,?,?,?)",
        (name, first_ts, ids[defining_idx], definition, definition or "",
         occ, status, status_node, upd_ts if upd_ts is not None else first_ts, last_ctx or ""))

code("N1", "登录页视觉改版", 0, ms(7,26,20,10), 2, "定义", None, None, "")
code("N2", "支付流程重构", 0, ms(7,26,20,10), 3, "完成", 10, ms(7,27,11,52), "N2完成，N3变更：改为只做红点提醒")
code("N3", "消息中心优化", 0, ms(7,26,20,10), 3, "变更", 10, ms(7,27,11,52), "N2完成，N3变更：改为只做红点提醒")
code("T1", "先做 N1 的页面骨架", 1, ms(7,26,21,3), 3, "完成", 5, ms(7,27,10,24), "T1 完成，接下去执行T2")
code("T2", "打通 N2 的退款接口", 1, ms(7,26,21,3), 3, "进行中", 5, ms(7,27,10,24), "T1 完成，接下去执行T2")
code("REQ-AUTH-3", "第三方账号绑定", 2, ms(7,26,18,40), 2, "进行中", 2, ms(7,26,18,40), "调研 OAuth 供应商对比")

c.commit()
print("demo nodes:", len(nodes), "codenames: 6")
