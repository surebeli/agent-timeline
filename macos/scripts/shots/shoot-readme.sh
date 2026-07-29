#!/usr/bin/env bash
# README「macOS 实机一览」三张图的拍摄编排（参数规范见 windows/DEBUG-PLAYBOOK.md §3b）。
#
#   macos/scripts/shots/shoot-readme.sh [--install] [--hero] [--app <路径>]
#
#   --install   拍完直接覆盖 docs/assets/ 下的成品（默认只写 out 目录，便于先目验）
#   --hero      额外拍 README 首图 screenshot-dark.png（面板 430×698，不做合成）
#   --app       指定 .app（默认 macos/dist/AgentTimeline.app）
#
# 铁律（照做，别省）：
#   1. 隐私红线——公开截图只用 docs/DEMO-DATASET.md 的演示数据，真实时间线绝不出镜；
#   2. 数据安全——真实 db 与设置先备份，拍完立即还原，并用 md5 + 分 agent 计数双重核验；
#      本脚本用 trap 兜底：中途失败/Ctrl-C 也会还原；
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
while [ $# -gt 0 ]; do
  case "$1" in
    --install) INSTALL=1 ;;
    --hero)    HERO=1 ;;
    --app)     APP="$2"; shift ;;
    *) echo "未知参数: $1" >&2; exit 2 ;;
  esac
  shift
done

DOMAIN=com.litianyi.agent-timeline
SUPPORT="$HOME/Library/Application Support/AgentTimeline"
DB="$SUPPORT/store.sqlite"
WORK="$(mktemp -d /tmp/agent-timeline-shots.XXXXXX)"
BACKUP="$WORK/backup"; OUT="$WORK/out"; BIN="$WORK/bin"
mkdir -p "$BACKUP" "$OUT" "$BIN"

# ── 几何常量（与 macOS 三张严格一致，改这里必须同步 DEBUG-PLAYBOOK.md §3b）
PANEL_W=640; PANEL_H=580          # pt；640 宽命令原文不被弹层截断，580 高收尾落在卡片边界
PANEL_X=500; PANEL_Y_COCOA=365    # 屏幕上的落点（Cocoa 左下原点）
HERO_W=430;  HERO_H=698
PAD=96                            # 合成画布四边留白（px，即 48pt @2x）
DICT_DX=61                        # 词典按钮距面板右缘（pt）
PROJ_DX=170                       # 「全部」下拉距面板右缘（pt）
TOOLBAR_DY=15                     # 工具栏按钮距面板顶（pt）

restored=0
restore() {
  if [ "$restored" = 1 ]; then return 0; fi
  restored=1
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
  if diff -q "$BACKUP/counts.txt" "$WORK/after.txt" >/dev/null 2>&1; then
    echo "   ✅ 节点/词典计数一致"
  else
    echo "   ❌ 计数不一致！备份仍在 $BACKUP" >&2; diff "$BACKUP/counts.txt" "$WORK/after.txt" >&2 || true
  fi
  local m1 m2
  m1="$(md5 -q "$BACKUP/store.sqlite" 2>/dev/null || echo -)"
  m2="$(md5 -q "$DB" 2>/dev/null || echo -)"
  [ "$m1" = "$m2" ] && echo "   ✅ md5 一致 $m1" || echo "   ❌ md5 不一致 $m1 vs $m2（备份在 $BACKUP）" >&2
  open "$APP" 2>/dev/null || true
}
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

echo "── 灌注演示数据 + 演示配置"
rm -f "$DB" "$DB-wal" "$DB-shm"
python3 "$REPO/macos/scripts/demo-seed.py" "$DB"
# ⚠ key 名以 AppSettings.SettingsKey 为准：写错 key 等于没关，真实 session 会混进演示库
for k in agentClaudeEnabled agentCodexEnabled agentGrokEnabled agentKimiEnabled agentZcodeEnabled; do
  defaults write "$DOMAIN" "$k" -bool false
done
defaults write "$DOMAIN" engineMode -string rule
defaults write "$DOMAIN" backfillDays -int 0
defaults write "$DOMAIN" idleOpacity -float 0.97
defaults write "$DOMAIN" hoverOpacity -float 0.98
defaults write "$DOMAIN" alwaysOnTop -bool true

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
    defaults write "$DOMAIN" panelFrame -string "{{$PANEL_X, $PANEL_Y_COCOA}, {$1, $2}}"
    defaults read "$DOMAIN" panelFrame >/dev/null   # 逼 cfprefsd 落盘
    open "$APP"
    sleep 7
    line="$("$BIN/window-tool" list | tail -1)"
    x="$(echo "$line" | awk '{print $2}')"
    if [ "${x:-0}" = "$PANEL_X" ]; then echo "$line"; return 0; fi
    echo "   ⚠ 第 $try 次落点是 x=${x:-?}（期望 $PANEL_X），重试" >&2
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
echo "── 合成到统一画布 ${CW}×${CH}"
for f in timeline projects dictionary; do
  "$BIN/compose" "$OUT/raw-$f.png" "$OUT/screenshot-macos-$f.png" "$CW" "$CH"
done

if [ "$HERO" = 1 ]; then
  echo "── 拍首图（面板 ${HERO_W}×${HERO_H}pt，不做合成）"
  read -r ID X Y W H <<< "$(launch_panel "$HERO_W" "$HERO_H")"
  park; screencapture -x -o -l "$ID" "$OUT/screenshot-dark.png"
fi

if [ "$INSTALL" = 1 ]; then
  echo "── 安装到 docs/assets"
  for f in timeline projects dictionary; do
    cp "$OUT/screenshot-macos-$f.png" "$REPO/docs/assets/screenshot-macos-$f.png"
  done
  if [ "$HERO" = 1 ]; then cp "$OUT/screenshot-dark.png" "$REPO/docs/assets/screenshot-dark.png"; fi
else
  echo "── 未安装（加 --install 覆盖 docs/assets）"
fi
echo "── 产物: $OUT"
