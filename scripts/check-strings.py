#!/usr/bin/env python3
"""文案表同源与完整性硬校验（CI 门禁）。

design/strings.json 是双端共享文案的唯一事实源。两端各有一份必须同源的副本：
  · windows/AgentTimeline/Assets/strings.json  —— **字节一致**（随构建复制到输出）
  · macos/.../StringsData.swift                —— 源文本原样嵌入（mac 侧编译进 bundle）

为什么要这道关：建表时实测发现，两端在**只有中文**的情况下就已经漂了 8 处以上
（纯规则"不调用 LLM" vs "不调用模型"、退出/显隐/加载更多/项目过滤…）。单语尚且如此，
四语各端各译必然更糟。漂移不会报错，只会让两端越走越远，所以只能靠机器守。

校验项：
  1. JSON 可解析，languages 字段齐备
  2. 每个键四语齐全且非空          —— 漏译只在跑到那个界面时才暴露
  3. 每个键的占位符集合四语一致    —— {0} 漏写会让参数凭空消失，且没有报错
  4. 平台覆盖键（@win/@mac）必须有对应基准键 —— 否则另一端拿不到回退值
  5. Windows 副本与源字节一致
  6. mac 嵌入副本包含源文本（该文件存在时才查，便于分阶段落地）

用法： python scripts/check-strings.py [仓库根]
"""
import io
import json
import os
import re
import sys

repo = sys.argv[1] if len(sys.argv) > 1 else "."
SRC = os.path.join(repo, "design", "strings.json")
WIN = os.path.join(repo, "windows", "AgentTimeline", "Assets", "strings.json")
MAC = os.path.join(repo, "macos", "Sources", "AgentTimeline", "UI", "StringsData.swift")
PLACEHOLDER = re.compile(r"\{(\d+)\}")

errors = []


def fail(msg):
    errors.append(msg)


raw = io.open(SRC, encoding="utf-8").read()
try:
    data = json.loads(raw)
except Exception as exc:  # noqa: BLE001
    print("::error::design/strings.json 解析失败: %s" % exc)
    sys.exit(1)

langs = data.get("languages") or []
strings = data.get("strings") or {}
if not langs:
    fail("languages 字段为空")
if not strings:
    fail("strings 字段为空")

# 2 + 3：完整性与占位符一致性
for key, entry in strings.items():
    slots = {}
    for lang in langs:
        value = entry.get(lang)
        if value is None:
            fail("%s 缺少语言 %s" % (key, lang))
            continue
        if not value.strip():
            fail("%s 的 %s 是空串" % (key, lang))
            continue
        slots[lang] = tuple(sorted(set(PLACEHOLDER.findall(value))))
    if len(set(slots.values())) > 1:
        fail("%s 占位符四语不一致: %s" % (key, slots))

# 4：平台覆盖键必须有基准键
for key in strings:
    if "@" in key:
        base, _, platform = key.partition("@")
        if platform not in ("win", "mac"):
            fail("%s 的平台后缀只允许 @win / @mac" % key)
        if base not in strings:
            fail("%s 没有对应的基准键 %s（另一端会拿不到回退值）" % (key, base))

# 5：Windows 副本字节一致
if not os.path.exists(WIN):
    fail("缺少 Windows 副本 %s" % WIN)
elif io.open(WIN, "rb").read() != io.open(SRC, "rb").read():
    fail("Windows 副本与 design/strings.json 不是字节一致——请重新复制")

# 6：mac 嵌入副本（尚未落地时跳过，落地后自动开始校验）
if os.path.exists(MAC):
    if raw.rstrip() not in io.open(MAC, encoding="utf-8").read():
        fail("mac 嵌入副本已过期——重新生成 StringsData.swift")
else:
    print("· mac 嵌入副本尚未落地，跳过该项（文件出现后本关自动开始校验）")

if errors:
    for e in errors:
        print("::error::%s" % e)
    print("\n文案表校验未通过：%d 项" % len(errors))
    sys.exit(1)

print("文案表校验通过：%d 键 × %d 语言，副本同源" % (len(strings), len(langs)))
