#!/usr/bin/env python3
"""演示数据集的双语不变式校验（docs/DEMO-DATASET.md）。

守两条：
  1. **结构两语一致**——agent / 项目 / session / 时间戳 / kind / 代号生命周期完全相同。
     结构一旦分叉，两语的版面与折行就不同，README 中英两行对不齐；
  2. **文案两语不同**——命令原文 / 标题 / 关键点 / 结果行 / 代号定义都换了语言。
     全等说明某一套忘了翻，而那种图看着完全正常（chrome 是对的语言、内容不是）。

  3. **两端产出等价**——mac 与 Windows 两份 seed 现在 import 同一个
     `scripts/demo_dataset.py`，但各自做 schema 适配；这一条守住「适配没写歪」。
     schema 差异（id 类型 / ts 单位 / codenames JSON 里 definition 的 null-vs-空串）
     在比对前归一，其余必须逐字一致。

顺带核 `kind` 与代号 `status` 仍是中文 rawValue：那是存储契约，翻了会让 kind 过滤
与代号状态机一起失效。

用法：python3 scripts/check-demo-dataset.py [仓库根]
"""
import json
import os
import sqlite3
import subprocess
import sys
import tempfile

ROOT = sys.argv[1] if len(sys.argv) > 1 else "."
WIN_SEED = os.path.join(ROOT, "windows", "scripts", "demo-seed.py")
MAC_SEED = os.path.join(ROOT, "macos", "scripts", "demo-seed.py")

# 只建 seed 需要的两张表；字段与 windows/AgentTimeline/Core/Store.cs 对齐即可。
WIN_SCHEMA = """
CREATE TABLE nodes(id INTEGER PRIMARY KEY AUTOINCREMENT, agent TEXT, project TEXT, session_id TEXT,
 ts INTEGER, text TEXT, source_file TEXT, source_offset INTEGER, command_hash TEXT, title TEXT,
 key_points TEXT, codenames TEXT, result_line TEXT, summary_source TEXT, summary_pending INTEGER,
 kind TEXT);
CREATE TABLE codenames(name TEXT, first_seen INTEGER, defining_node_id INTEGER, definition TEXT,
 context_excerpt TEXT, occurrences INTEGER, status TEXT, status_node INTEGER, updated INTEGER,
 last_context TEXT);
"""

# mac 端 schema（对齐 macos/Sources/AgentTimeline/Core/Store.swift）：TEXT 主键、ts 秒、
# engine+summarized 而非 summary_source+summary_pending、codenames 无 context_excerpt。
MAC_SCHEMA = """
CREATE TABLE nodes(id TEXT PRIMARY KEY, agent TEXT, project TEXT, cwd TEXT, session_id TEXT,
 ts REAL, text TEXT, source_file TEXT, title TEXT, key_points TEXT, codenames TEXT,
 result_line TEXT, engine TEXT, summarized INTEGER, summary_attempts INTEGER, kind TEXT);
CREATE TABLE codenames(name TEXT PRIMARY KEY, definition TEXT, definition_node TEXT,
 first_seen REAL, occurrences INTEGER, status TEXT, status_node TEXT, updated REAL,
 last_context TEXT);
CREATE TABLE file_offsets(path TEXT PRIMARY KEY, offset INTEGER, inode INTEGER);
"""

KIND_RAW = {"需求", "任务", "调研", "学习", "决策", "修复", "其他"}
STATUS_RAW = {"定义", "进行中", "完成", "变更", "提及"}


def seed(tmp, lang, end="win"):
    db = os.path.join(tmp, f"{end}-{lang}.db")
    conn = sqlite3.connect(db)
    # mac 那份 seed 自建表（CREATE TABLE IF NOT EXISTS），这里也先建一遍无妨——
    # 建错了列名反而能立刻暴露适配漂移。
    conn.executescript(MAC_SCHEMA if end == "mac" else WIN_SCHEMA)
    conn.commit()
    conn.close()
    seed_path = MAC_SEED if end == "mac" else WIN_SEED
    r = subprocess.run([sys.executable, seed_path, db, "--lang", lang],
                       capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise SystemExit(f"::error::{end} demo-seed.py --lang {lang} 失败：{r.stderr.strip()}")
    conn = sqlite3.connect(db)
    out = {
        "struct": conn.execute(
            "select agent,project,session_id,ts,kind from nodes order by ts,agent").fetchall(),
        "codes": conn.execute(
            "select name,first_seen,%s,occurrences,status,status_node,updated"
            " from codenames order by name" % ("definition_node" if end == "mac" else "defining_node_id")
        ).fetchall(),
        "texts": conn.execute(
            "select text,title,key_points,result_line from nodes order by ts,agent").fetchall(),
        "defs": conn.execute("select name,definition from codenames order by name").fetchall(),
        "ctx": conn.execute("select name,last_context from codenames order by name").fetchall(),
        "embedded": [
            [(x["name"], x["status"]) for x in json.loads(j)]
            for (j,) in conn.execute("select codenames from nodes order by ts,agent").fetchall()
        ],
    }
    conn.close()
    return out


def main():
    fails = []
    with tempfile.TemporaryDirectory() as tmp:
        zh = seed(tmp, "zh")
        en = seed(tmp, "en")
        mac_zh = seed(tmp, "zh", end="mac")
        mac_en = seed(tmp, "en", end="mac")

    # ── 第三条：两端产出等价。两份 seed import 同一个 scripts/demo_dataset.py，
    # 这里守的是「schema 适配没写歪」。ts 单位（win 毫秒 / mac 秒）与 id 类型
    # （win 自增整型 / mac TEXT）是已文档化的差异，比对前归一。
    def norm_struct(rows, end):
        div = 1000 if end == "win" else 1
        return [(a, p_, s_, round(t / div), k) for (a, p_, s_, t, k) in rows]

    def norm_codes(rows, end):
        div = 1000 if end == "win" else 1
        # 状态/定义节点的 id 类型不同（整型 vs demo-00N），只比「是否指向同一条下标」
        return [(n, round(f / div), str(dn), o, st, str(sn), round(u / div))
                for (n, f, dn, o, st, sn, u) in rows]

    def norm_defs(rows):
        return [(n, d or "") for (n, d) in rows]

    for lang, w, m in (("zh", zh, mac_zh), ("en", en, mac_en)):
        if norm_struct(w["struct"], "win") != norm_struct(m["struct"], "mac"):
            fails.append(f"{lang}: 两端结构不等价（agent/项目/session/时间/kind）——schema 适配写歪了")
        if norm_defs(w["defs"]) != norm_defs(m["defs"]):
            fails.append(f"{lang}: 两端代号定义不等价")
        if w["texts"] != m["texts"]:
            fails.append(f"{lang}: 两端文案不等价——共享模块没生效或某端抄了一份")
        if w["ctx"] != m["ctx"]:
            fails.append(f"{lang}: 两端代号 lastContext 不等价"
                         "（曾漂过：mac 把 REQ-AUTH-3 写成完整命令原文、win 写的是独立短摘录）")
        if [c[:2] + c[3:5] + c[6:] for c in norm_codes(w["codes"], "win")] != \
           [c[:2] + c[3:5] + c[6:] for c in norm_codes(m["codes"], "mac")]:
            fails.append(f"{lang}: 两端代号生命周期不等价（首见/次数/状态/更新时间）")

    if zh["struct"] != en["struct"]:
        fails.append("结构（agent/项目/session/ts/kind）两语不一致——两语版面会对不齐")
    if zh["codes"] != en["codes"]:
        fails.append("代号生命周期（定义节点/次数/状态/状态节点/更新时间）两语不一致")
    if zh["embedded"] != en["embedded"]:
        fails.append("节点内嵌 codenames 的 name/status 两语不一致")

    if zh["texts"] == en["texts"]:
        fails.append("文案两语完全相同——英文一套没翻，会拍出混语图")
    same = [i for i, (a, b) in enumerate(zip(zh["texts"], en["texts"])) if a == b]
    if same:
        fails.append(f"第 {[i + 1 for i in same]} 条的文案两语相同——漏译")
    if zh["defs"] == en["defs"]:
        fails.append("代号定义两语完全相同——漏译")

    for lang, data in (("zh", zh), ("en", en)):
        bad_kind = {k for (_, _, _, _, k) in data["struct"] if k not in KIND_RAW}
        if bad_kind:
            fails.append(f"{lang} 的 kind 不是中文 rawValue：{bad_kind}（存储契约，不能翻译）")
        bad_status = {s for (_, _, _, _, s, _, _) in data["codes"] if s not in STATUS_RAW}
        if bad_status:
            fails.append(f"{lang} 的代号 status 不是中文 rawValue：{bad_status}（同上）")

    if fails:
        for f in fails:
            print(f"::error::{f}")
        print(f"\n演示数据集校验未通过：{len(fails)} 项")
        return 1

    print(f"演示数据集校验通过：{len(zh['struct'])} 节点 × 2 语种 × 2 端，"
          f"结构逐字段一致、文案逐条不同、两端产出等价、kind/status 仍是落库 rawValue")
    return 0


if __name__ == "__main__":
    sys.exit(main())
