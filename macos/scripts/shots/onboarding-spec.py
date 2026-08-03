#!/usr/bin/env python3
"""生成 README 新手引导图的 annotate.py spec（中/英/日/韩四套）。

    onboarding-spec.py <raw目录> <语言 zh|en|ja|ko>

<raw目录> 是 shoot-readme.sh 产出的 out/ 目录（含 raw-timeline.png / raw-collapsed.png）。
输出 JSON 打到 stdout，交给 annotate.py 渲染：

    onboarding-spec.py <raw目录> zh > /tmp/spec.json
    annotate.py /tmp/spec.json <安装前的输出目录>

坐标（头部控件距右缘 pt、垂直中心 14pt）由 macos/scripts/shots/window-tool.swift 在
640×580 面板上实测校准（2026-07-30），随头部布局改动需要重新量。量法见
`windows/DEBUG-PLAYBOOK.md` §3b「两个卡死点」——横扫点击找命中区间，别靠肉眼猜中心。
"""
import json
import os
import sys

# 头部控件：距面板右缘 pt（面板宽 640pt 时实测），y 一律 14（垂直中心）。
# 顺序即 header 里从左到右排布的顺序：project → kind → dict → collapse → pin → settings。
HEADER_Y = 14
PROJECT_X, KIND_X = 414, 483
DICT_X, COLLAPSE_X, PIN_X, SETTINGS_X = 559 - 29, 559, 559 + 29, 559 + 59
# ⚠ 上面 DICT/PIN/SETTINGS 用 COLLAPSE_X 做基准而不是各写死一个数：四个图标命中框
# 21pt、组内间距 8pt，中心距固定 29pt——一旦头部改了间距，这里只用改 COLLAPSE_X 一处，
# 其余仨跟着挪，不会悄悄错位。COLLAPSE_X=81 是实测值（横扫命中区间 72–88pt 的中点）。

COPY = {
    "zh": {
        "img1": {
            "title": "认识你的挂件",
            "sub": "六个入口都在标题栏上，鼠标悬停任意图标可见完整说明",
            "footer": "Agent Timeline · 首次使用速览",
            "labels": [
                ("项目过滤", "只看某一个项目的时间线"),
                ("类型过滤", "按 需求/任务/调研/学习/决策/修复 归类查看"),
                ("代号词典", "回看所有代号的定义与生命周期状态"),
                ("折叠面板", "收起面板只留标题栏，不占屏幕（下一张图细讲）"),
                ("窗口置顶", "失焦后依旧显示在最上层"),
                ("设置", "摘要引擎、透明度、语言、agent 开关"),
            ],
        },
        "img2": {
            "title": "不用时，收起来",
            "sub": "点一下折叠按钮，面板收成一条标题栏；再点一次展开回原来的高度，位置分毫不动",
            "footer": "折叠状态跨重启保持 · 展开高度记忆上一次的样子",
            "collapse": ("点这里折叠", "面板收成只剩标题栏，41pt 高，不挡屏幕"),
            "expand": ("再点一次展开", "回到折叠前的高度，顶边位置不变"),
            "arrow": "点击",
        },
    },
    "en": {
        "img1": {
            "title": "Meet your widget",
            "sub": "Six entry points live on the title bar — hover any icon for the full tooltip",
            "footer": "Agent Timeline · first-run overview",
            "labels": [
                ("Project filter", "Show only one project's timeline"),
                ("Kind filter", "Filter by Requirement / Task / Research / Learning / Decision / Fix"),
                ("Codename dictionary", "Review every codename's definition and lifecycle state"),
                ("Collapse panel", "Shrink to just the title bar, out of your way (next image)"),
                ("Always on top", "Stays visible even when the panel loses focus"),
                ("Settings", "Summary engine, opacity, language, agent toggles"),
            ],
        },
        "img2": {
            "title": "Collapse it when you don't need it",
            "sub": ("Click the collapse button and the panel shrinks to a single title bar; "
                    "click again to expand back to its previous height, in the exact same spot"),
            "footer": "Collapsed state survives restarts · expanded height is remembered",
            "collapse": ("Click here to collapse", "Shrinks to just the title bar (41pt), out of the way"),
            "expand": ("Click again to expand", "Back to the pre-collapse height, top edge unchanged"),
            "arrow": "Click",
        },
    },
    "ja": {
        "img1": {
            "title": "ウィジェットの使い方",
            "sub": "6つの入口はすべてタイトルバーに。アイコンにホバーすると説明が出ます",
            "footer": "Agent Timeline · はじめての方へ",
            "labels": [
                ("プロジェクト絞り込み", "特定のプロジェクトのタイムラインだけを表示"),
                ("種別フィルター", "要件 / タスク / 調査 / 学習 / 決定 / 修正 で絞り込み"),
                ("コードネーム辞書", "すべてのコード名の定義とライフサイクル状態を確認"),
                ("パネルを折りたたむ", "タイトルバーだけに縮小して画面を占有しない（次の画像で詳説）"),
                ("最前面に固定", "フォーカスを失っても最前面に表示され続ける"),
                ("設定", "要約エンジン・透明度・言語・agent のオン/オフ"),
            ],
        },
        "img2": {
            "title": "使わない時は折りたたむ",
            "sub": ("折りたたみボタンを押すとパネルはタイトルバー1本に。"
                    "もう一度押せば元の高さに戻り、位置は動きません"),
            "footer": "折りたたみ状態は再起動後も保持 · 展開時の高さも記憶",
            "collapse": ("ここをクリックで折りたたみ", "タイトルバーだけに（41pt）、画面の邪魔にならない"),
            "expand": ("もう一度クリックで展開", "折りたたむ前の高さに戻る。上端の位置は変わらない"),
            "arrow": "クリック",
        },
    },
    "ko": {
        "img1": {
            "title": "위젯 둘러보기",
            "sub": "여섯 개 진입점이 모두 제목 표시줄에 있습니다. 아이콘에 마우스를 올리면 설명이 나옵니다",
            "footer": "Agent Timeline · 첫 사용 가이드",
            "labels": [
                ("프로젝트 필터", "특정 프로젝트의 타임라인만 보기"),
                ("유형 필터", "요구사항 / 작업 / 조사 / 학습 / 결정 / 수정 으로 분류해 보기"),
                ("코드명 사전", "모든 코드명의 정의와 생명주기 상태 확인"),
                ("패널 접기", "제목 표시줄만 남기고 접어 화면을 차지하지 않음（다음 이미지에서 자세히）"),
                ("항상 위에 표시", "포커스를 잃어도 계속 맨 앞에 표시"),
                ("설정", "요약 엔진, 불투명도, 언어, agent 켜기/끄기"),
            ],
        },
        "img2": {
            "title": "안 볼 때는 접어두기",
            "sub": ("접기 버튼을 누르면 패널이 제목 표시줄 하나로 접힙니다. "
                    "다시 누르면 원래 높이로 펼쳐지고 위치는 그대로입니다"),
            "footer": "접힌 상태는 재시작 후에도 유지 · 펼쳤을 때의 높이도 기억",
            "collapse": ("여기를 클릭해 접기", "제목 표시줄만 남김（41pt）, 화면을 가리지 않음"),
            "expand": ("다시 클릭해 펼치기", "접기 전 높이로 복귀. 위쪽 가장자리는 그대로"),
            "arrow": "클릭",
        },
    },
}

# 语言 → 成品文件名后缀。中文是默认产物、不带后缀（历史文件名不变）。
# 与 shoot-readme.sh 的 SUFFIX 分支同口径，改这里要同步那边。
SUFFIX = {"zh": "", "en": "-en", "ja": "-ja", "ko": "-ko"}

# 紧凑四件套的 zigzag 徽标偏移：命中框 21pt 宽、间距 29pt，相邻徽标同高会碰，
# 高低交替错开。项目/类型两个圆更大更疏，单排即可。
OFFSET_WIDE = [-15, -58]
OFFSET_HIGH = [-15, -80]
OFFSET_LOW = [-15, -42]


def build(raw_dir, lang):
    c = COPY[lang]
    timeline = os.path.join(raw_dir, "raw-timeline.png")
    collapsed = os.path.join(raw_dir, "raw-collapsed.png")
    suffix = SUFFIX[lang]

    (proj_l, proj_d), (kind_l, kind_d), (dict_l, dict_d), \
        (coll_l, coll_d), (pin_l, pin_d), (set_l, set_d) = c["img1"]["labels"]

    img1 = {
        "name": f"onboarding-1-overview{suffix}",
        "canvas": [1560, 1020],
        "title": c["img1"]["title"], "sub": c["img1"]["sub"],
        "titleTop": 56, "titleSize": 54, "subTop": 132, "subSize": 27,
        "footer": c["img1"]["footer"],
        "legendAt": [1010, 300], "legendWidth": 500, "legendSize": 25, "legendGap": 26,
        "shots": [{
            "path": timeline, "at": [70, 300], "size": [640, 580], "scale": 1,
            "spots": [
                {"at": [PROJECT_X, HEADER_Y], "r": 25, "label": proj_l, "desc": proj_d,
                 "badgeOffset": OFFSET_WIDE},
                {"at": [KIND_X, HEADER_Y], "r": 20, "label": kind_l, "desc": kind_d,
                 "badgeOffset": OFFSET_WIDE},
                {"at": [DICT_X, HEADER_Y], "r": 16, "label": dict_l, "desc": dict_d,
                 "badgeOffset": OFFSET_HIGH},
                {"at": [COLLAPSE_X, HEADER_Y], "r": 16, "label": coll_l, "desc": coll_d,
                 "badgeOffset": OFFSET_LOW},
                {"at": [PIN_X, HEADER_Y], "r": 16, "label": pin_l, "desc": pin_d,
                 "badgeOffset": OFFSET_HIGH},
                {"at": [SETTINGS_X, HEADER_Y], "r": 16, "label": set_l, "desc": set_d,
                 "badgeOffset": OFFSET_LOW},
            ],
        }],
    }

    coll_label, coll_desc = c["img2"]["collapse"]
    exp_label, exp_desc = c["img2"]["expand"]
    img2 = {
        "name": f"onboarding-2-collapse{suffix}",
        "canvas": [1560, 920],
        "title": c["img2"]["title"], "sub": c["img2"]["sub"],
        "titleTop": 56, "titleSize": 54, "subTop": 132, "subSize": 27,
        "footer": c["img2"]["footer"],
        "legendAt": [70, 660], "legendWidth": 1420, "legendSize": 25, "legendGap": 16,
        "shots": [
            {"path": timeline, "at": [70, 260], "size": [640, 580], "scale": 0.6,
             "spots": [{"at": [COLLAPSE_X, HEADER_Y], "r": 16, "label": coll_label,
                        "desc": coll_desc, "badgeOffset": OFFSET_LOW}]},
            # 折叠态与展开态放在**同一个顶边高度**——呼应真实行为「折叠时顶边不动」，
            # 不是随手把两张图并排；这也是为什么它俩的 "at" y 坐标相同。
            {"path": collapsed, "at": [600, 260], "size": [640, 41], "scale": 0.6,
             "spots": [{"at": [COLLAPSE_X, HEADER_Y], "r": 16, "label": exp_label,
                        "desc": exp_desc, "badgeOffset": OFFSET_LOW}]},
        ],
        "arrows": [{"at": [462, 253], "label": c["img2"]["arrow"]}],
    }
    return {"images": [img1, img2]}


def main():
    if len(sys.argv) != 3 or sys.argv[2] not in SUFFIX:
        raise SystemExit("用法: onboarding-spec.py <raw目录> <zh|en|ja|ko>")
    print(json.dumps(build(sys.argv[1], sys.argv[2]), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
