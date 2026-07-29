<#
.SYNOPSIS
  README「Windows 实机一览」三张图的拍摄编排（参数规范见 windows/DEBUG-PLAYBOOK.md §3b）。

.DESCRIPTION
  对标 mac 侧 macos/scripts/shots/shoot-readme.sh：备份 → 灌演示数据 → 三态拍摄
  → 合成 → 还原 → 核验。

  铁律（照做，别省）：
    1. 隐私红线——公开截图只用 docs/DEMO-DATASET.md 的演示数据，真实时间线绝不出镜；
    2. 数据安全——真实 db 与设置先备份，**文件级交换**不删目录（win 端曾因子目录被
       进程占用导致目录级还原失败），拍完立即还原，并用分 agent 计数 + md5 双重核验；
       本脚本用 try/finally 兜底：中途失败 / Ctrl-C 也会还原；
    3. 隔离干扰——演示配置 = 纯规则摘要 + 全部 agent 监听关闭 + 回填 0 天；
    4. 演示数据在场时间越短越好；任何一步失败**先还原再排障**。

  与 mac 编排的三处必要差异（实测依据见 WindowTool/Program.cs 文件头）：
    · 每一态都**重启应用**再拍：本机 WinUI 的 UIA 树跑久了会退化到只剩几个节点，
      按 AutomationId 就找不到控件；重启最省心，也顺带清掉上一态残留的弹层。
    · 按钮走 **UIA 调用**而不是点坐标：本机合成鼠标输入被系统吞掉（指针都挪不动）。
    · **不套用 mac「词典态必须比时间线态宽」的不变式**：WinUI 3 的 Flyout 默认受
      ShouldConstrainToRootBounds 约束，弹层被挤回面板内，三态抓取尺寸恒等。
      对应的防呆换成「调用后面板像素必须有实质变化」，由 WindowTool 硬判。

.PARAMETER Install
  拍完直接覆盖 docs/assets/ 下的成品（默认只写工作目录，便于先目验）。

.PARAMETER Scale
  期望的显示缩放百分比，默认 200（§3b 规定 1dip = 2px）。
  脚本**只校验不修改**显示设置：不匹配就停下来，请在「设置 → 系统 → 显示 →
  缩放」里改主显示器再重跑。改全局缩放会重排你所有开着的窗口，不该由脚本背着人做。

.PARAMETER App
  要拍的 AgentTimeline.exe，默认取仓库 Release 构建产物。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1
  powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [int]$Scale = 200,
    [string]$App = '',
    [string]$Work = ''
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $App) {
    $App = Join-Path $repo 'windows\AgentTimeline\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\AgentTimeline.exe'
}
if (-not (Test-Path $App)) { throw "找不到应用: $App（先 msbuild 构建，见 DEBUG-PLAYBOOK §0）" }

if (-not $Work) { $Work = Join-Path $env:TEMP ("at-shots-" + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$bin    = Join-Path $Work 'bin'
$backup = Join-Path $Work 'backup'
$raw    = Join-Path $Work 'raw'
$out    = Join-Path $Work 'out'
foreach ($d in @($Work, $bin, $backup, $raw, $out)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

$data     = Join-Path $env:LOCALAPPDATA 'AgentTimeline'
$db       = Join-Path $data 'timeline.db'
$settings = Join-Path $data 'settings.json'
$dbFiles  = @('timeline.db', 'timeline.db-wal', 'timeline.db-shm')

# ── 几何常量（与 macOS 三张严格一致，改这里必须同步 DEBUG-PLAYBOOK.md §3b）
$PANEL_W_DIP = 640     # 640 宽命令原文不被弹层截断
$PANEL_H_DIP = 580     # 580 高收尾正好落在卡片边界而非切半行
$PANEL_X_DIP = 300     # 屏幕落点（左上原点）；离右缘远一点，给弹层留地方
$PANEL_Y_DIP = 30
# 画布 = mac 三态并集 763×580dip + 四边 48dip 留白 = 859×676dip。
# 写成 dip 常量而不是"取本端三态并集"：Windows 三态尺寸恒等（弹层不溢出面板），
# 按本端并集算会得到 736×676，与 mac 比例不同，README 两行就对不齐了。
# @200% 即 1718×1352px，与 mac 成品逐位相同。
$CANVAS_W_DIP = 859
$CANVAS_H_DIP = 676

$k = $Scale / 100.0
$panelW = [int]($PANEL_W_DIP * $k); $panelH = [int]($PANEL_H_DIP * $k)
$panelX = [int]($PANEL_X_DIP * $k); $panelY = [int]($PANEL_Y_DIP * $k)
$canvasW = [int]($CANVAS_W_DIP * $k); $canvasH = [int]($CANVAS_H_DIP * $k)

function Stop-App {
    Get-Process AgentTimeline -ErrorAction SilentlyContinue | Stop-Process -Force
    # 浮层打开时不要走优雅退出：模态事件循环会挡住退出消息（mac 上卡到超时两次）。
    # 直接强杀——设置随后本来就要从备份还原。
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Process AgentTimeline -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Seconds 1
}

function Start-App([int]$WaitSeconds = 15) {
    Start-Process $App
    Start-Sleep -Seconds $WaitSeconds
}

# 计数脚本落成文件再调：多行 -c 在 Windows PowerShell 5.1 下引号转义不稳。
$countPy = Join-Path $Work 'counts.py'
@'
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
try:
    print(sorted(c.execute('select agent,count(*) from nodes group by agent').fetchall()))
    print(c.execute('select count(*) from codenames').fetchone()[0])
except Exception as e:
    print('ERR', e)
'@ | Set-Content $countPy -Encoding UTF8

function Get-DbCounts([string]$path) {
    if (-not (Test-Path $path)) { return '<无库>' }
    return (python $script:countPy $path 2>&1 | Out-String).Trim()
}

function Get-Md5([string]$path) {
    if (-not (Test-Path $path)) { return '<无>' }
    return (Get-FileHash $path -Algorithm MD5).Hash
}

# ── 前置校验：缩放必须对，否则产出的是半尺寸/异比例的图，且**看不出来**
Write-Host '── 校验显示缩放'
& dotnet build (Join-Path $PSScriptRoot 'WindowTool\WindowTool.csproj') -c Release -o $bin -v quiet --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'WindowTool 编译失败' }
$tool = Join-Path $bin 'WindowTool.exe'
$primary = (& $tool dpi | Select-Object -First 1)
Write-Host "   $primary"
$actual = [int]([regex]::Match($primary, 'scale=(\d+)%').Groups[1].Value)
if ($actual -ne $Scale) {
    throw ("主显示器缩放是 $actual%，本次要求 $Scale%。请在「设置 → 系统 → 显示 → 缩放」" +
           "改主显示器后重跑；或显式传 -Scale $actual（产出像素尺寸会随之减半，比例不变）。")
}

$restored = $false
function Restore-Real {
    if ($script:restored) { return }
    $script:restored = $true
    Write-Host '── 还原真实环境'
    Stop-App
    # 文件级交换，不删目录（win 端曾因子目录被进程占用导致目录级还原失败）
    foreach ($f in $dbFiles) {
        $live = Join-Path $data $f
        $bak  = Join-Path $backup $f
        if (Test-Path $live) { Remove-Item $live -Force -ErrorAction SilentlyContinue }
        if (Test-Path $bak) { Copy-Item $bak $live -Force }
    }
    if (Test-Path (Join-Path $backup 'settings.json')) {
        Copy-Item (Join-Path $backup 'settings.json') $settings -Force
    }
    # 双重核验：分 agent 计数 + md5，都要与备份一致
    $afterCounts = Get-DbCounts $db
    $afterDbMd5  = Get-Md5 $db
    $afterSetMd5 = Get-Md5 $settings
    if ($afterCounts -eq $script:beforeCounts) { Write-Host '   ✅ 节点/词典计数一致' }
    else { Write-Warning "   ❌ 计数不一致！备份仍在 $backup`n     前: $script:beforeCounts`n     后: $afterCounts" }
    if ($afterDbMd5 -eq $script:beforeDbMd5) { Write-Host "   ✅ db md5 一致 $afterDbMd5" }
    else { Write-Warning "   ❌ db md5 不一致 $script:beforeDbMd5 vs $afterDbMd5（备份在 $backup）" }
    if ($afterSetMd5 -eq $script:beforeSetMd5) { Write-Host '   ✅ settings.json md5 一致' }
    else { Write-Warning "   ❌ settings.json md5 不一致（备份在 $backup）" }
    Start-Process $App
}

try {
    Write-Host '── 备份真实 db 与设置'
    Stop-App
    $script:beforeCounts = Get-DbCounts $db
    $script:beforeDbMd5  = Get-Md5 $db
    $script:beforeSetMd5 = Get-Md5 $settings
    foreach ($f in $dbFiles) {
        $live = Join-Path $data $f
        if (Test-Path $live) { Copy-Item $live (Join-Path $backup $f) -Force }
    }
    if (Test-Path $settings) { Copy-Item $settings (Join-Path $backup 'settings.json') -Force }
    Write-Host "   基线 db md5=$script:beforeDbMd5"
    Write-Host "   $($script:beforeCounts -replace "`r?`n", ' / ')"
    if ($script:beforeDbMd5 -eq '<无>') { throw '没有真实 db 可备份，先跑一次应用' }

    Write-Host '── 演示配置 + 演示数据'
    # ⚠ 键名以 AppSettings 属性名为准：写错等于没关，真实 session 会混进演示库
    $demo = [ordered]@{
        Engine = 'Rule'; CliCommand = 'auto'
        ProviderBaseUrl = ''; ProviderApiKey = ''; ProviderModel = ''
        HoverOpacity = 0.98; IdleOpacity = 0.97; AlwaysOnTop = $true
        WindowX = $panelX; WindowY = $panelY; WindowWidth = $panelW; WindowHeight = $panelH
        BackfillDays = 0
        EnableClaude = $false; EnableCodex = $false; EnableGrok = $false
        EnableKimi = $false; EnableZcode = $false
        ZcodeSessionRoot = ''; CodenameReplayVersion = 99
    }
    foreach ($f in $dbFiles) {
        $live = Join-Path $data $f
        if (Test-Path $live) { Remove-Item $live -Force }
    }
    $demo | ConvertTo-Json | Set-Content $settings -Encoding UTF8
    # demo-seed.py 只 INSERT、不建表 → 先让应用起一次把 schema 建出来。
    # 这一趟顺便量出「窗口矩形 − 客户区」的边框厚度。
    Start-App 12
    $b = (& $tool border) -split ' '
    if ($LASTEXITCODE -ne 0) { throw '量不到窗口边框' }
    $borderW = [int]$b[2]; $borderH = [int]$b[3]
    Write-Host "   不可见边框: 左上偏移 $($b[0]),$($b[1])  总宽高差 ${borderW}x${borderH}"
    Stop-App
    python (Join-Path $repo 'windows\scripts\demo-seed.py') $db
    if ($LASTEXITCODE -ne 0) { throw '灌注演示数据失败' }
    # AppSettings 存的是窗口矩形，可见面板是客户区 → 反推，让**客户区**正好 640×580dip
    $demo.WindowWidth  = $panelW + $borderW
    $demo.WindowHeight = $panelH + $borderH
    $demo.WindowX = $panelX - [int]$b[0]
    $demo.WindowY = $panelY - [int]$b[1]

    Write-Host "── 拍摄三态（面板 ${PANEL_W_DIP}×${PANEL_H_DIP}dip @ $Scale% = ${panelW}×${panelH}px）"
    # 每态一次全新启动：UIA 树退化与上一态残留弹层，重启一并解决
    $states = @(
        @{ name = 'timeline';   invoke = $null },
        @{ name = 'projects';   invoke = 'ProjectFilterButton' },
        @{ name = 'dictionary'; invoke = 'DictionaryButton' }
    )
    $shots = @{}
    foreach ($s in $states) {
        $demo | ConvertTo-Json | Set-Content $settings -Encoding UTF8
        Start-App 15
        $toolArgs = @('shoot', (Join-Path $raw $s.name))
        if ($s.invoke) { $toolArgs += @('--invoke', $s.invoke) }
        $lines = & $tool @toolArgs
        if ($LASTEXITCODE -ne 0) { throw "拍 $($s.name) 失败" }
        $shots[$s.name] = @($lines)
        Write-Host "   $($s.name): $($lines -join ' | ')"
        Stop-App
    }

    Write-Host "── 合成到统一画布 ${canvasW}×${canvasH}"
    foreach ($s in $states) {
        # 必须 @(...) 兜住：只有一层时 $specs 会退化成字符串，@splat 会把它按**字符**摊开
        $specs = @(foreach ($line in $shots[$s.name]) {
            # idx hwnd x y w h class file → file@x,y
            # 限定切 8 段：路径里可能有空格（用户名带空格的机器），文件名必须留在最后一段
            $p = $line -split ' ', 8
            "$($p[7])@$($p[2]),$($p[3])"
        })
        & $tool compose (Join-Path $out "screenshot-windows-$($s.name).png") $canvasW $canvasH @specs
        if ($LASTEXITCODE -ne 0) { throw "合成 $($s.name) 失败" }
    }

    if ($Install) {
        Write-Host '── 安装到 docs/assets'
        foreach ($s in $states) {
            Copy-Item (Join-Path $out "screenshot-windows-$($s.name).png") `
                      (Join-Path $repo "docs\assets\screenshot-windows-$($s.name).png") -Force
        }
    } else {
        Write-Host '── 未安装（加 -Install 覆盖 docs/assets）'
    }
    Write-Host "── 产物: $out"
}
finally {
    Restore-Real
}
