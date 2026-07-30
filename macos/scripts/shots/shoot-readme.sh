#!/usr/bin/env bash
# README「macOS 实机一览」三张图的拍摄编排（参数规范见 windows/DEBUG-PLAYBOOK.md §3b）。
#
#   macos/scripts/shots/shoot-readme.sh [--install] [--hero] [--app <路径>]
#
#   --install   拍完直接覆盖 docs/assets/ 下的成品（默认只写 out 目录，便于先目验）
#   --recover   救援：用数据目录下的固定备份 .shoot-backup 覆盖真实位置并清中断标记
#   --settings  额外拍设置窗口（screenshot-macos-settings$SUFFIX.png）
#   --language <ZhHans|En>  演示语言（默认 ZhHans）。En 时产出装到 *-en.png，
#               供 README.en.md 用；与 win shoot-readme.ps1 -Language 同语义
#
# 缩放：**按拍摄机系统默认**，脚本不改显示缩放（用户 2026-07-30 决定：各端用自己系统
# 的默认设置，不再互相强制）。mac 上 Retina 2x → 1718×1352；Windows 100% → 859×676，
# dip 几何与比例两端相同。
#   --hero      额外拍 README 首图 screenshot-dark.png（面板 430×698，不做合成）
#   --app       指定 .app（默认 macos/dist/AgentTimeline.app）
#
# 铁律（照做，别省）：
#   1. 隐私红线——公开截图只用 docs/DEMO-DATASET.md 的演示数据，真实时间线绝不出镜；
#   2. 数据安全——真实 db 与设置先备份，拍完立即还原，并用 md5 + 分 agent 计数双重核验；
#      trap 兜底（中途失败/Ctrl-C 也会还原），但 trap **挡不住 SIGKILL**，故另有两道：
#      · 中断标记 .shoot-in-progress：动真实文件之前立起来，还原三项全对上才清。
#        下一轮开跑先查它——上一轮没收尾就拒绝取基线，否则会把**演示库当成真实基线**
#        备份、拍完忠实还原回去：三条 ✅ 全打勾而真实数据被水泥封死。
#        「校验通过、数据没了」是最坏的一类失败，因为它不报错（Windows 侧 2026-07-30
#        真丢过一次）；
#      · $swapped 标志：restore() 只在**真的进入交换阶段**后才动文件。trap 会在备份步骤
#        之前的任何失败上也触发，那时备份目录是空的，而还原是「先删真实文件再从备份拷回」
#        —— 删得掉、拷不回。Windows 侧修 bug 1 时正是被这条又删了一次库。
#   3. 隔离干扰——演示配置 = 纯规则摘要 + 全部 agent 监听关闭 + 回填 0 天；
#   4. 演示数据在场时间越短越好。
#
# 为什么抓窗口而不是抓屏幕区域：screencapture -l 取窗口自身缓冲区，与 z 序、遮挡无关。
# 屏幕区域截图会把盖在上面的第三方全屏浮层一起摄进来（实测 UURemoteServer 等），
# 旧版截图上的彩色光斑就是这么来的，不是应用的半透明缺陷。
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
APP="$REPO/macos/dist/AgentTimeline.app"
INSTALL=0
HERO=0
RECOVER=0
LANGUAGE=ZhHans
while [ $# -gt 0 ]; do
  case "$1" in
    --install) INSTALL=1 ;;
    --recover) RECOVER=1 ;;
    --language) LANGUAGE="$2"; shift ;;
    --hero)    HERO=1 ;;
    --settings) SETTINGS=1 ;;
    --app)     APP="$2"; shift ;;
    *) echo "未知参数: $1" >&2; exit 2 ;;
  esac
  shift
done

DOMAIN=com.litianyi.agent-timeline
SUPPORT="$HOME/Library/Application Support/AgentTimeline"
DB="$SUPPORT/store.sqlite"
# 中断标记与固定备份都放在**数据目录**里：救援时不必去猜是哪个 mktemp 目录，
# 也不怕系统把 /tmp 清掉。
MARKER="$SUPPORT/.shoot-in-progress"
FIXED_BACKUP="$SUPPORT/.shoot-backup"
WORK="$(mktemp -d /tmp/agent-timeline-shots.XXXXXX)"
BACKUP="$WORK/backup"; OUT="$WORK/out"; BIN="$WORK/bin"
mkdir -p "$BACKUP" "$OUT" "$BIN"

# ── 几何常量（与 macOS 三张严格一致，改这里必须同步 DEBUG-PLAYBOOK.md §3b）
PANEL_W=640; PANEL_H=580          # pt；640 宽命令原文不被弹层截断，580 高收尾落在卡片边界
PANEL_X=500; PANEL_Y_COCOA=365    # 屏幕上的落点（Cocoa 左下原点）
HERO_W=430;  HERO_H=698
PAD=96                            # 合成画布四边留白（px，即 48pt @2x）
DICT_DX=101                       # 词典按钮距面板右缘（pt，命中框放大后的实测值）
COLLAPSE_DX=80                    # 折叠/展开按钮距面板右缘（pt，横扫实测命中区间 72-88）
SETTINGS_DX=22                    # 设置按钮距面板右缘（pt，实测同 onboarding-spec.py SETTINGS_X）
PROJ_DX=170                       # 「全部」下拉距面板右缘（pt）
TOOLBAR_DY=15                     # 工具栏按钮距面板顶（pt）

restored=0
swapped=0        # 只有真的动过真实文件才置 1；restore() 靠它决定要不要还原
restore() {
  if [ "$restored" = 1 ]; then return 0; fi
  restored=1
  # ⚠ 关键：trap 会在**备份步骤之前**的任何失败上也触发（swiftc 失败、sqlite3 不在
  # PATH、参数写错……）。那时 $BACKUP 是空的，而下面是「先 rm 再 cp 回」——
  # 删得掉、拷不回。没交换过就一个文件都不要动。
  if [ "$swapped" = 0 ]; then
    echo "── 未进入交换阶段，真实文件一个都没动"
    return 0
  fi
  echo "── 还原真实环境"
  pkill -9 -x AgentTimeline 2>/dev/null || true
  sleep 1
  rm -f "$DB" "$DB-wal" "$DB-shm"
  for f in store.sqlite store.sqlite-wal store.sqlite-shm; do
    if [ -f "$BACKUP/$f" ]; then cp "$BACKUP/$f" "$SUPPORT/$f"; fi
  done
  defaults delete "$DOMAIN" 2>/dev/null || true
  if [ -f "$BACKUP/defaults.plist" ]; then defaults import "$DOMAIN" "$BACKUP/defaults.plist"; fi
  # 核验：分 agent 计数 + 文件 md5 都必须与备份一致
  sqlite3 "$DB" "select agent,count(*) from nodes group by agent;" > "$WORK/after.txt" 2>/dev/null || true
  sqlite3 "$DB" "select count(*) from codenames;" >> "$WORK/after.txt" 2>/dev/null || true
  local counts_ok=0
  if diff -q "$BACKUP/counts.txt" "$WORK/after.txt" >/dev/null 2>&1; then
    counts_ok=1; echo "   ✅ 节点/词典计数一致"
  else
    echo "   ❌ 计数不一致！备份仍在 $FIXED_BACKUP" >&2
    diff "$BACKUP/counts.txt" "$WORK/after.txt" >&2 || true
  fi
  local m1 m2
  m1="$(md5 -q "$BACKUP/store.sqlite" 2>/dev/null || echo -)"
  m2="$(md5 -q "$DB" 2>/dev/null || echo -)"
  local md5_ok=0
  if [ "$m1" = "$m2" ]; then md5_ok=1; echo "   ✅ md5 一致 $m1"; else
    echo "   ❌ md5 不一致 $m1 vs ${m2}（备份在 ${FIXED_BACKUP}）" >&2
  fi
  local defaults_ok=0
  sleep 1     # 等 cfprefsd 把 import 落盘，否则 export 拿到的是半旧状态
  defaults export "$DOMAIN" "$WORK/defaults-after.plist" 2>/dev/null || true
  # 比较**解析后的键值**而不是文件字节：plist 的键序与格式不稳定，字节比对会误判
  if python3 - "$BACKUP/defaults.plist" "$WORK/defaults-after.plist" <<'PYEOF'
import plistlib, sys
try:
    a = plistlib.load(open(sys.argv[1], 'rb'))
    b = plistlib.load(open(sys.argv[2], 'rb'))
except Exception:
    sys.exit(1)
sys.exit(0 if a == b else 1)
PYEOF
  then
    defaults_ok=1; echo "   ✅ 设置一致"
  else
    echo "   ❌ 设置不一致（备份在 ${FIXED_BACKUP}）" >&2
  fi
  # 三项全对上才清中断标记。对不上就**留着**——留着好过让下一轮把演示库当成真实基线。
  if [ "$counts_ok" = 1 ] && [ "$md5_ok" = 1 ] && [ "$defaults_ok" = 1 ]; then
    rm -f "$MARKER"
    echo "   ✅ 三项全对，中断标记已清"
  else
    echo "   ⚠️ 有项目对不上，**保留**中断标记 $MARKER" >&2
    echo "      救援：$0 --recover" >&2
  fi
  open "$APP" 2>/dev/null || true
}
# ── 救援：用固定备份覆盖真实位置并清标记。放在 trap 之前——救援本身不该触发还原。
if [ "$RECOVER" = 1 ]; then
  if [ ! -d "$FIXED_BACKUP" ]; then echo "没有固定备份 ${FIXED_BACKUP}，无法救援" >&2; exit 1; fi
  echo "── 救援：从 ${FIXED_BACKUP} 覆盖真实位置"
  pkill -9 -x AgentTimeline 2>/dev/null || true
  sleep 1
  for f in store.sqlite store.sqlite-wal store.sqlite-shm; do
    rm -f "$SUPPORT/$f"
    if [ -f "$FIXED_BACKUP/$f" ]; then cp "$FIXED_BACKUP/$f" "$SUPPORT/$f"; fi
  done
  if [ -f "$FIXED_BACKUP/defaults.plist" ]; then
    defaults delete "$DOMAIN" 2>/dev/null || true
    defaults import "$DOMAIN" "$FIXED_BACKUP/defaults.plist"
  fi
  echo "   现库 md5=$(md5 -q "$DB" 2>/dev/null || echo -)"
  if [ -f "$FIXED_BACKUP/store.sqlite" ]; then echo "   备份 md5=$(md5 -q "$FIXED_BACKUP/store.sqlite")"; fi
  rm -f "$MARKER"
  echo "   ✅ 已还原并清除中断标记"
  exit 0
fi

# ── 开跑先查中断标记：上一轮没收尾就**拒绝取基线**。
# 否则会把还留在真实位置的演示库当成真实基线备份，拍完忠实还原回去——
# 计数与 md5 两条都会打勾，而真实数据已经没了。
if [ -f "$MARKER" ]; then
  echo "❌ 发现上一轮的中断标记，拒绝继续（否则会把演示库当成真实基线）" >&2
  echo "── 标记内容 ──" >&2; sed 's/^/   /' "$MARKER" >&2
  echo "   救援: $0 --recover" >&2
  echo "   确认真实数据无误后手工删除标记: rm '${MARKER}'" >&2
  exit 1
fi

trap restore EXIT INT TERM

echo "── 编译 helper（脚本模式每次重编会超时，先编成二进制）"
swiftc -O -o "$BIN/window-tool" "$REPO/macos/scripts/shots/window-tool.swift"
swiftc -O -o "$BIN/compose"     "$REPO/macos/scripts/shots/compose.swift"

echo "── 备份真实 db 与设置"
pkill -9 -x AgentTimeline 2>/dev/null || true
sleep 1
sqlite3 "$DB" "select agent,count(*) from nodes group by agent;" > "$BACKUP/counts.txt"
sqlite3 "$DB" "select count(*) from codenames;" >> "$BACKUP/counts.txt"
for f in store.sqlite store.sqlite-wal store.sqlite-shm; do
  if [ -f "$SUPPORT/$f" ]; then cp "$SUPPORT/$f" "$BACKUP/$f"; fi
done
defaults export "$DOMAIN" "$BACKUP/defaults.plist"
echo "   基线 md5=$(md5 -q "$BACKUP/store.sqlite")"; sed 's/^/   /' "$BACKUP/counts.txt"

# ⚠ 顺序即安全：标记与固定备份必须在**动真实文件之前**立好。立晚了，正好在这中间
# 被杀，下一轮照样把演示库当基线。
echo "── 立中断标记 + 固定备份"
mkdir -p "$FIXED_BACKUP"
for f in store.sqlite store.sqlite-wal store.sqlite-shm defaults.plist counts.txt; do
  if [ -f "$BACKUP/$f" ]; then cp "$BACKUP/$f" "$FIXED_BACKUP/$f"; fi
done
{
  echo "started=$(date '+%Y-%m-%d %H:%M:%S')"
  echo "baseline_md5=$(md5 -q "$BACKUP/store.sqlite" 2>/dev/null || echo -)"
  echo "fixed_backup=$FIXED_BACKUP"
  echo "work_dir=$WORK"
  echo "recover=$0 --recover"
  echo "--- baseline counts ---"
  cat "$BACKUP/counts.txt" 2>/dev/null || true
} > "$MARKER"
swapped=1          # 自此 restore() 才允许动真实文件

echo "── 灌注演示数据 + 演示配置"
rm -f "$DB" "$DB-wal" "$DB-shm"
case "$LANGUAGE" in
  ZhHans) SEED_LANG=zh ;;
  En)     SEED_LANG=en ;;
  *) echo "--language 只支持 ZhHans / En —— docs/DEMO-DATASET.md 目前只有中英两套" >&2; exit 2 ;;
esac
python3 "$REPO/macos/scripts/demo-seed.py" "$DB" --lang "$SEED_LANG"
# ⚠ key 名以 AppSettings.SettingsKey 为准：写错 key 等于没关，真实 session 会混进演示库
for k in agentClaudeEnabled agentCodexEnabled agentGrokEnabled agentKimiEnabled agentZcodeEnabled; do
  defaults write "$DOMAIN" "$k" -bool false
done
defaults write "$DOMAIN" engineMode -string rule
defaults write "$DOMAIN" backfillDays -int 0
defaults write "$DOMAIN" idleOpacity -float 0.97
defaults write "$DOMAIN" hoverOpacity -float 0.98
defaults write "$DOMAIN" alwaysOnTop -bool true
# ⚠ 必须钉死语言：四语接线后默认是「跟随系统」，不钉的话产出语言取决于**拍摄机的
# 系统 UI 语言**，而图看着完全正常——Windows 侧那台 en-US 机器一跑就拍出了英文图。
defaults write "$DOMAIN" language -string "$LANGUAGE"

# 起面板并把它放到 PANEL_X/PANEL_Y_COCOA。
#
# ⚠ 必须校验落点：FloatingPanel.restoreFrame() 读不到 panelFrame 时会走默认分支
# （贴主屏右缘 visibleFrame.maxX - w - 24）。而面板一旦贴右缘，词典弹层就没地方
# 向右展开，会被系统挤回面板内——并集宽度随之改变，产出与既有成品对不上。
# 写入与读取之间存在 cfprefsd 落盘竞态，故重试。
launch_panel() {   # $1=宽 $2=高 → 回显 "id x y w h"
  local try line x
  for try in 1 2 3; do
    pkill -9 -x AgentTimeline 2>/dev/null || true
    sleep 2
    defaults write "$DOMAIN" panelFrame -string "{{${PANEL_X}, $PANEL_Y_COCOA}, {$1, $2}}"
    defaults read "$DOMAIN" panelFrame >/dev/null   # 逼 cfprefsd 落盘
    open "$APP"
    sleep 7
    line="$("$BIN/window-tool" list | tail -1)"
    x="$(echo "$line" | awk '{print $2}')"
    if [ "${x:-0}" = "${PANEL_X}" ]; then echo "$line"; return 0; fi
    echo "   ⚠ 第 $try 次落点是 x=${x:-?}（期望 ${PANEL_X}），重试" >&2
  done
  echo "$line"   # 三次都不对：交给后面的不变式校验拦截
}

echo "── 拍摄三态（面板 ${PANEL_W}×${PANEL_H}pt，缩放 1pt=2px）"
read -r ID X Y W H <<< "$(launch_panel "$PANEL_W" "$PANEL_H")"
[ -n "${ID:-}" ] || { echo "拿不到面板窗口，app 起来了吗？" >&2; exit 1; }
echo "   面板 id=$ID @ $X,$Y ${W}×${H}"
park() { "$BIN/window-tool" move $((X - 150)) $((Y + 400)); sleep 2; }   # 指针移开：复位 hover、消 tooltip

park; screencapture -x -o -l "$ID" "$OUT/raw-timeline.png"
"$BIN/window-tool" click $((X + W - PROJ_DX)) $((Y + TOOLBAR_DY)); sleep 2
park; screencapture -x -o -l "$ID" "$OUT/raw-projects.png"
"$BIN/window-tool" click $((X + W - PROJ_DX)) $((Y + TOOLBAR_DY)); sleep 2   # 关下拉
"$BIN/window-tool" click $((X + W - DICT_DX)) $((Y + TOOLBAR_DY)); sleep 2   # 开词典
park; screencapture -x -o -l "$ID" "$OUT/raw-dictionary.png"
"$BIN/window-tool" click $((X + W - DICT_DX)) $((Y + TOOLBAR_DY)); sleep 2   # 关词典
# 折叠态：引导图要展示折叠前后对比。按钮在词典与置顶之间，距右缘 COLLAPSE_DX。
"$BIN/window-tool" click $((X + W - COLLAPSE_DX)) $((Y + TOOLBAR_DY)); sleep 2
park; screencapture -x -o -l "$ID" "$OUT/raw-collapsed.png"
"$BIN/window-tool" click $((X + W - COLLAPSE_DX)) $((Y + TOOLBAR_DY)); sleep 2   # 展开回去

# 画布 = 三态并集 + 四边 PAD。词典态最宽：弹层以按钮为中心展开，
# 恒定超出面板右缘 122pt，与面板宽度无关。
CW=0; CH=0
for f in timeline projects dictionary; do
  w=$(sips -g pixelWidth  "$OUT/raw-$f.png" | tail -1 | awk '{print $2}')
  h=$(sips -g pixelHeight "$OUT/raw-$f.png" | tail -1 | awk '{print $2}')
  if [ "$w" -gt "$CW" ]; then CW=$w; fi
  if [ "$h" -gt "$CH" ]; then CH=$h; fi
done

# 不变式：词典态必须比时间线态宽——说明弹层确实向右溢出了面板。
# 若相等，说明面板离屏幕右缘太近、弹层被挤回面板内，产出会与既有成品不一致。
w_time=$(sips -g pixelWidth "$OUT/raw-timeline.png"   | tail -1 | awk '{print $2}')
w_dict=$(sips -g pixelWidth "$OUT/raw-dictionary.png" | tail -1 | awk '{print $2}')
if [ "$w_dict" -le "$w_time" ]; then
  echo "❌ 词典弹层没有向右溢出（dict ${w_dict}px ≤ timeline ${w_time}px）。" >&2
  echo "   面板离屏幕右缘太近，弹层被挤了回去。把 PANEL_X 调小重跑" >&2
  echo "   （需要 PANEL_X + $PANEL_W + 122 ≤ 屏幕可见宽度）。" >&2
  exit 1
fi
CW=$((CW + PAD * 2)); CH=$((CH + PAD * 2))
SUFFIX=""
if [ "$LANGUAGE" = "En" ]; then SUFFIX="-en"; fi
echo "── 合成到统一画布 ${CW}×${CH}（语言 ${LANGUAGE}，产出后缀 '${SUFFIX}'）"
for f in timeline projects dictionary; do
  "$BIN/compose" "$OUT/raw-$f.png" "$OUT/screenshot-macos-$f$SUFFIX.png" "$CW" "$CH"
done

if [ "$HERO" = 1 ]; then
  echo "── 拍首图（面板 ${HERO_W}×${HERO_H}pt，不做合成）"
  read -r ID X Y W H <<< "$(launch_panel "$HERO_W" "$HERO_H")"
  park; screencapture -x -o -l "$ID" "$OUT/screenshot-dark$SUFFIX.png"
fi

if [ "$SETTINGS" = 1 ]; then
  echo "── 拍设置窗口"
  read -r ID X Y W H <<< "$(launch_panel "$PANEL_W" "$PANEL_H")"
  # 设置窗是独立 NSWindow（宽度固定 420pt、按内容自适应高度），点齿轮才会出现，
  # 故不能像面板那样提前枚举——先点开，再从「新出现的窗口」里找它：宽度 420pt
  # （SettingsView 的 .frame(width: 420)）是唯一标识，比"最后一个窗口"更不脆弱。
  "$BIN/window-tool" click $((X + W - SETTINGS_DX)) $((Y + TOOLBAR_DY))
  sleep 2
  SID=$("$BIN/window-tool" list | awk '$4==420{print $1; exit}')   # SettingsView 固定宽 420pt
  if [ -z "$SID" ]; then
    echo "❌ 没找到设置窗口（宽度不是预期的 420pt，SettingsView 布局是否改过？）" >&2
    exit 1
  fi
  screencapture -x -o -l "$SID" "$OUT/screenshot-macos-settings$SUFFIX.png"
fi

if [ "$INSTALL" = 1 ]; then
  echo "── 安装到 docs/assets"
  for f in timeline projects dictionary; do
    cp "$OUT/screenshot-macos-$f$SUFFIX.png" "$REPO/docs/assets/screenshot-macos-$f$SUFFIX.png"
  done
  if [ "$HERO" = 1 ]; then
    cp "$OUT/screenshot-dark$SUFFIX.png" "$REPO/docs/assets/screenshot-dark$SUFFIX.png"
  fi
  if [ "$SETTINGS" = 1 ]; then
    cp "$OUT/screenshot-macos-settings$SUFFIX.png" \
       "$REPO/docs/assets/screenshot-macos-settings$SUFFIX.png"
  fi
else
  echo "── 未安装（加 --install 覆盖 docs/assets）"
fi
echo "── 产物: $OUT"
