#!/usr/bin/env python3
"""演示数据集的双语不变式校验（docs/DEMO-DATASET.md）。

守两条：
  1. **结构两语一致**——agent / 项目 / session / 时间戳 / kind / 代号生命周期完全相同。
     结构一旦分叉，两语的版面与折行就不同，README 中英两行对不齐；
  2. **文案两语不同**——命令原文 / 标题 / 关键点 / 结果行 / 代号定义都换了语言。
     全等说明某一套忘了翻，而那种图看着完全正常（chrome 是对的语言、内容不是）。

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
SEED = os.path.join(ROOT, "windows", "scripts", "demo-seed.py")

# 只建 seed 需要的两张表；字段与 windows/AgentTimeline/Core/Store.cs 对齐即可。
SCHEMA = """
CREATE TABLE nodes(id INTEGER PRIMARY KEY AUTOINCREMENT, agent TEXT, project TEXT, session_id TEXT,
 ts INTEGER, text TEXT, source_file TEXT, source_offset INTEGER, command_hash TEXT, title TEXT,
 key_points TEXT, codenames TEXT, result_line TEXT, summary_source TEXT, summary_pending INTEGER,
 kind TEXT);
CREATE TABLE codenames(name TEXT, first_seen INTEGER, defining_node_id INTEGER, definition TEXT,
 context_excerpt TEXT, occurrences INTEGER, status TEXT, status_node INTEGER, updated INTEGER,
 last_context TEXT);
"""

KIND_RAW = {"需求", "任务", "调研", "学习", "决策", "修复", "其他"}
STATUS_RAW = {"定义", "进行中", "完成", "变更", "提及"}


def seed(tmp, lang):
    db = os.path.join(tmp, lang + ".db")
    conn = sqlite3.connect(db)
    conn.executescript(SCHEMA)
    conn.commit()
    conn.close()
    r = subprocess.run([sys.executable, SEED, db, "--lang", lang],
                       capture_output=True, text=True, encoding="utf-8")
    if r.returncode != 0:
        raise SystemExit(f"::error::demo-seed.py --lang {lang} 失败：{r.stderr.strip()}")
    conn = sqlite3.connect(db)
    out = {
        "struct": conn.execute(
            "select agent,project,session_id,ts,kind from nodes order by id").fetchall(),
        "codes": conn.execute(
            "select name,first_seen,defining_node_id,occurrences,status,status_node,updated"
            " from codenames order by name").fetchall(),
        "texts": conn.execute(
            "select text,title,key_points,result_line from nodes order by id").fetchall(),
        "defs": conn.execute("select name,definition from codenames order by name").fetchall(),
        "embedded": [
            [(x["name"], x["status"]) for x in json.loads(j)]
            for (j,) in conn.execute("select codenames from nodes order by id").fetchall()
        ],
    }
    conn.close()
    return out


def main():
    fails = []
    with tempfile.TemporaryDirectory() as tmp:
        zh = seed(tmp, "zh")
        en = seed(tmp, "en")

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

    print(f"演示数据集校验通过：{len(zh['struct'])} 节点 × 2 语种，"
          f"结构逐字段一致、文案逐条不同、kind/status 仍是落库 rawValue")
    return 0


if __name__ == "__main__":
    sys.exit(main())
