// GENERATED from design/strings.json by scripts/build-app.sh — do not edit.
enum StringsData {
    static let json = #"""
{
  "$schema": "agent-timeline strings v1",
  "meta": {
    "platform": "键名可带 @win / @mac 后缀做平台覆盖：加载器先查 `键名@<平台>`，没有则回退 `键名`。仅在概念本身分叉时才拆（如 hideToTray——macOS 是菜单栏应用，没有托盘），不要为措辞差异拆键，那正是本表要消灭的漂移。",
    "note": "双端共享文案唯一事实源。mac: 编译进 bundle 由 Strings.swift 解析；win: Assets 下字节一致副本由 AppStrings 解析。占位符一律用 {0}/{1} 序号式（两端 Format 语义相同），不要用各自语言的插值语法。",
    "languages": "zh-Hans 为原文，其余三种由此翻译；新增键必须四种齐全，CI 硬校验。",
    "drift": "建表时发现两端在单中文下已漂移 8 处以上（纯规则'不调用 LLM'vs'不调用模型'、退出/显隐/加载更多/项目过滤等）。此表已统一取词，两端今后只认这里。",
    "storage": "kind.* 与 status.* 只是**显示标签**。落库值仍是中文枚举（nodes.kind / codenames.status），切语言不改历史数据，也不需要迁移。"
  },
  "languages": ["zh-Hans", "en", "ja", "ko"],
  "strings": {
    "app.settingsTitle": {
      "zh-Hans": "Agent Timeline 设置 · v{0}",
      "en": "Agent Timeline Settings · v{0}",
      "ja": "Agent Timeline 設定 · v{0}",
      "ko": "Agent Timeline 설정 · v{0}"
    },

    "tray.showHide": {
      "zh-Hans": "显示 / 隐藏",
      "en": "Show / Hide",
      "ja": "表示 / 非表示",
      "ko": "표시 / 숨기기"
    },
    "tray.alwaysOnTop": {
      "zh-Hans": "总在最前",
      "en": "Always on Top",
      "ja": "常に最前面に表示",
      "ko": "항상 위에 표시"
    },
    "tray.settings": {
      "zh-Hans": "设置…",
      "en": "Settings…",
      "ja": "設定…",
      "ko": "설정…"
    },
    "tray.exit": {
      "zh-Hans": "退出",
      "en": "Quit",
      "ja": "終了",
      "ko": "종료"
    },

    "header.projectFilter": {
      "zh-Hans": "项目过滤",
      "en": "Filter by project",
      "ja": "プロジェクトで絞り込み",
      "ko": "프로젝트 필터"
    },
    "header.kindFilter": {
      "zh-Hans": "类型过滤",
      "en": "Filter by type",
      "ja": "種別で絞り込み",
      "ko": "유형 필터"
    },
    "header.dictionary": {
      "zh-Hans": "代号词典",
      "en": "Codename dictionary",
      "ja": "コードネーム辞書",
      "ko": "코드명 사전"
    },
    "header.settings": {
      "zh-Hans": "设置",
      "en": "Settings",
      "ja": "設定",
      "ko": "설정"
    },
    "header.hideToTray": {
      "zh-Hans": "收进托盘",
      "en": "Hide to tray",
      "ja": "タスクトレイに収納",
      "ko": "트레이로 숨기기"
    },
    "header.hideToTray@mac": {
      "zh-Hans": "收进菜单栏",
      "en": "Hide to menu bar",
      "ja": "メニューバーに収納",
      "ko": "메뉴 막대로 숨기기"
    },

    "header.allProjects": {
      "zh-Hans": "全部",
      "en": "All",
      "ja": "すべて",
      "ko": "전체"
    },
    "header.allKinds": {
      "zh-Hans": "类型",
      "en": "Type",
      "ja": "種別",
      "ko": "유형"
    },
    "filter.allProjectsItem": {
      "zh-Hans": "全部项目",
      "en": "All projects",
      "ja": "すべてのプロジェクト",
      "ko": "모든 프로젝트"
    },
    "filter.allKindsItem": {
      "zh-Hans": "全部类型",
      "en": "All types",
      "ja": "すべての種別",
      "ko": "모든 유형"
    },

    "entry.expandCollapse": {
      "zh-Hans": "展开 / 收起",
      "en": "Expand / Collapse",
      "ja": "展開 / 折りたたみ",
      "ko": "펼치기 / 접기"
    },
    "entry.copyCommand": {
      "zh-Hans": "复制原话",
      "en": "Copy command",
      "ja": "原文をコピー",
      "ko": "원문 복사"
    },
    "entry.copySummary": {
      "zh-Hans": "复制摘要",
      "en": "Copy summary",
      "ja": "要約をコピー",
      "ko": "요약 복사"
    },
    "entry.filterThisProject": {
      "zh-Hans": "只看此项目",
      "en": "Only this project",
      "ja": "このプロジェクトのみ",
      "ko": "이 프로젝트만 보기"
    },
    "entry.jumpToCodename": {
      "zh-Hans": "跳转到 {0} 定义节点",
      "en": "Jump to where {0} was defined",
      "ja": "{0} を定義したノードへ移動",
      "ko": "{0} 정의 노드로 이동"
    },

    "timeline.todayWithCount": {
      "zh-Hans": "今天 · {0} 条",
      "en": "Today · {0}",
      "ja": "今日 · {0} 件",
      "ko": "오늘 · {0}개"
    },
    "timeline.yesterday": {
      "zh-Hans": "昨天",
      "en": "Yesterday",
      "ja": "昨日",
      "ko": "어제"
    },
    "timeline.loadMore": {
      "zh-Hans": "加载更多",
      "en": "Load more",
      "ja": "さらに読み込む",
      "ko": "더 보기"
    },
    "timeline.empty": {
      "zh-Hans": "暂无记录 — 在 Claude Code / Codex / Grok Build / Kimi Code / ZCode 里发一条命令即可点亮",
      "en": "Nothing yet — send a command in Claude Code / Codex / Grok Build / Kimi Code / ZCode to light this up",
      "ja": "まだ記録がありません — Claude Code / Codex / Grok Build / Kimi Code / ZCode でコマンドを送ると表示されます",
      "ko": "아직 기록이 없습니다 — Claude Code / Codex / Grok Build / Kimi Code / ZCode에서 명령을 보내면 표시됩니다"
    },
    "timeline.unpin": {
      "zh-Hans": "取消置顶",
      "en": "Unpin",
      "ja": "最前面表示を解除",
      "ko": "항상 위 고정 해제"
    },

    "dict.title": {
      "zh-Hans": "代号词典（{0}）",
      "en": "Codenames ({0})",
      "ja": "コードネーム辞書（{0}）",
      "ko": "코드명 사전({0})"
    },
    "dict.empty": {
      "zh-Hans": "尚无登记的代号 — 会话中出现 \"N1: xxx\" 式定义或 REQ-3 式长代号后会自动登记",
      "en": "No codenames yet — they are registered automatically once a session contains an \"N1: xxx\" definition or a long code like REQ-3",
      "ja": "登録されたコードネームはまだありません — セッションに「N1: xxx」形式の定義や REQ-3 のような長いコードが現れると自動登録されます",
      "ko": "아직 등록된 코드명이 없습니다 — 세션에 \"N1: xxx\" 형식의 정의나 REQ-3 같은 긴 코드가 나타나면 자동 등록됩니다"
    },
    "dict.noDefinition": {
      "zh-Hans": "（暂无定义）",
      "en": "(no definition yet)",
      "ja": "（定義なし）",
      "ko": "(정의 없음)"
    },
    "dict.notRegistered": {
      "zh-Hans": "尚未登记",
      "en": "Not registered",
      "ja": "未登録",
      "ko": "미등록"
    },
    "dict.pendingDefinition": {
      "zh-Hans": "暂无定义（等待摘要提炼或定义式重述）",
      "en": "No definition yet (waiting for a summary or a restated definition)",
      "ja": "定義はまだありません（要約の抽出または定義の言い直しを待機中）",
      "ko": "아직 정의가 없습니다 (요약 추출 또는 정의 재언급 대기 중)"
    },
    "dict.lastMention": {
      "zh-Hans": "最近提及：…{0}…",
      "en": "Last mentioned: …{0}…",
      "ja": "最近の言及：…{0}…",
      "ko": "최근 언급: …{0}…"
    },
    "dict.firstSeen": {
      "zh-Hans": "首次 {0} · 共 {1} 次",
      "en": "First seen {0} · {1} mentions",
      "ja": "初検出 {0} · 計 {1} 回",
      "ko": "최초 {0} · 총 {1}회"
    },
    "dict.updated": {
      "zh-Hans": " · 更新 {0}",
      "en": " · updated {0}",
      "ja": " · 更新 {0}",
      "ko": " · 업데이트 {0}"
    },
    "dict.jumpToDefinition": {
      "zh-Hans": "跳转到定义节点",
      "en": "Jump to definition",
      "ja": "定義ノードへ移動",
      "ko": "정의 노드로 이동"
    },

    "settings.section.engine": {
      "zh-Hans": "摘要引擎",
      "en": "Summary engine",
      "ja": "要約エンジン",
      "ko": "요약 엔진"
    },
    "settings.engine.cli": {
      "zh-Hans": "本机 CLI（推荐，零配置：claude -p / codex exec）",
      "en": "Local CLI (recommended, zero config: claude -p / codex exec)",
      "ja": "ローカル CLI（推奨・設定不要：claude -p / codex exec）",
      "ko": "로컬 CLI(권장 · claude -p / codex exec)"
    },
    "settings.engine.provider": {
      "zh-Hans": "自定义 Provider（OpenAI 兼容接口）",
      "en": "Custom provider (OpenAI-compatible API)",
      "ja": "カスタムプロバイダー（OpenAI 互換 API）",
      "ko": "사용자 지정 공급자(OpenAI 호환)"
    },
    "settings.engine.rule": {
      "zh-Hans": "纯规则（不调用模型）",
      "en": "Rules only (no model calls)",
      "ja": "ルールのみ（モデル呼び出しなし）",
      "ko": "규칙만 사용(모델 호출 없음)"
    },
    "settings.cliChoice": {
      "zh-Hans": "CLI 选择（auto 时优先 claude，其次 codex）",
      "en": "CLI choice (auto prefers claude, then codex)",
      "ja": "CLI の選択（auto は claude を優先し、次に codex）",
      "ko": "CLI 선택(auto: claude 우선, 그다음 codex)"
    },
    "settings.model": {
      "zh-Hans": "模型",
      "en": "Model",
      "ja": "モデル",
      "ko": "모델"
    },
    "settings.section.appearance": {
      "zh-Hans": "外观与窗口",
      "en": "Appearance & window",
      "ja": "外観とウィンドウ",
      "ko": "모양 및 창"
    },
    "settings.hoverOpacity": {
      "zh-Hans": "悬停不透明度",
      "en": "Hover opacity",
      "ja": "ホバー時の不透明度",
      "ko": "호버 불투명도"
    },
    "settings.idleOpacity": {
      "zh-Hans": "失焦不透明度",
      "en": "Idle opacity",
      "ja": "非フォーカス時の不透明度",
      "ko": "비활성 불투명도"
    },
    "settings.alwaysOnTop": {
      "zh-Hans": "窗口置顶",
      "en": "Keep window on top",
      "ja": "ウィンドウを最前面に表示",
      "ko": "창을 항상 위에 표시"
    },
    "settings.on": {
      "zh-Hans": "开",
      "en": "On",
      "ja": "オン",
      "ko": "켬"
    },
    "settings.off": {
      "zh-Hans": "关",
      "en": "Off",
      "ja": "オフ",
      "ko": "끔"
    },
    "settings.section.data": {
      "zh-Hans": "数据与 Agent",
      "en": "Data & agents",
      "ja": "データと Agent",
      "ko": "데이터 및 Agent"
    },
    "settings.sessionSources": {
      "zh-Hans": "Session 来源",
      "en": "Session sources",
      "ja": "セッションのソース",
      "ko": "세션 소스"
    },
    "settings.backfillDays": {
      "zh-Hans": "启动回填最近 {0} 天的 session",
      "en": "Backfill sessions from the last {0} days on startup",
      "ja": "起動時に直近 {0} 日分を取り込む",
      "ko": "최근 {0}일 세션 불러오기"
    },
    "settings.note": {
      "zh-Hans": "Agent 目录开关需重启应用后生效；其余设置保存即生效。",
      "en": "Agent source toggles take effect after a restart; everything else applies on save.",
      "ja": "Agent ソースの切り替えは再起動後に有効になります。その他の設定は保存すると即座に反映されます。",
      "ko": "Agent 소스 변경은 앱을 다시 시작해야 적용됩니다. 나머지 설정은 저장하면 바로 적용됩니다."
    },
    "settings.save": {
      "zh-Hans": "保存",
      "en": "Save",
      "ja": "保存",
      "ko": "저장"
    },
    "settings.cancel": {
      "zh-Hans": "取消",
      "en": "Cancel",
      "ja": "キャンセル",
      "ko": "취소"
    },
    "settings.apply": {
      "zh-Hans": "应用",
      "en": "Apply",
      "ja": "適用",
      "ko": "적용"
    },

    "settings.language": {
      "zh-Hans": "语言",
      "en": "Language",
      "ja": "言語",
      "ko": "언어"
    },
    "settings.language.system": {
      "zh-Hans": "跟随系统",
      "en": "Match system",
      "ja": "システムに合わせる",
      "ko": "시스템 설정 따르기"
    },
    "settings.language.note": {
      "zh-Hans": "切换后界面与新生成的摘要立即改用该语言；已入库的历史摘要保持原样，不重新生成。",
      "en": "Switching applies immediately to the interface and to newly generated summaries. Summaries already stored keep their original language and are not regenerated.",
      "ja": "切り替えるとインターフェースと新しく生成される要約にすぐ反映されます。保存済みの要約は元の言語のまま残り、再生成されません。",
      "ko": "전환하면 인터페이스와 새로 생성되는 요약에 즉시 적용됩니다. 이미 저장된 요약은 원래 언어를 유지하며 다시 생성되지 않습니다."
    },

    "kind.requirement": {
      "zh-Hans": "需求",
      "en": "Requirement",
      "ja": "要件",
      "ko": "요구사항"
    },
    "kind.task": {
      "zh-Hans": "任务",
      "en": "Task",
      "ja": "タスク",
      "ko": "작업"
    },
    "kind.research": {
      "zh-Hans": "调研",
      "en": "Research",
      "ja": "調査",
      "ko": "조사"
    },
    "kind.learning": {
      "zh-Hans": "学习",
      "en": "Learning",
      "ja": "学習",
      "ko": "학습"
    },
    "kind.decision": {
      "zh-Hans": "决策",
      "en": "Decision",
      "ja": "決定",
      "ko": "결정"
    },
    "kind.fix": {
      "zh-Hans": "修复",
      "en": "Fix",
      "ja": "修正",
      "ko": "버그 수정"
    },
    "kind.other": {
      "zh-Hans": "其他",
      "en": "Other",
      "ja": "その他",
      "ko": "기타"
    },

    "status.defined": {
      "zh-Hans": "定义",
      "en": "Defined",
      "ja": "定義",
      "ko": "정의됨"
    },
    "status.inProgress": {
      "zh-Hans": "进行中",
      "en": "In progress",
      "ja": "進行中",
      "ko": "진행 중"
    },
    "status.done": {
      "zh-Hans": "完成",
      "en": "Done",
      "ja": "完了",
      "ko": "완료"
    },
    "status.changed": {
      "zh-Hans": "变更",
      "en": "Changed",
      "ja": "変更",
      "ko": "변경됨"
    },
    "status.mentioned": {
      "zh-Hans": "提及",
      "en": "Mentioned",
      "ja": "言及",
      "ko": "언급됨"
    },

    "rule.emptyCommand": {
      "zh-Hans": "(空命令)",
      "en": "(empty command)",
      "ja": "（空のコマンド）",
      "ko": "(빈 명령)"
    }
  }
}
"""#
}
