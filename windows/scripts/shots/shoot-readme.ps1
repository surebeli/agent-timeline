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

.PARAMETER Recover
  只做救援：用 %LOCALAPPDATA%\AgentTimeline\.shoot-backup 覆盖真实位置并清掉中断标记。
  上一轮被硬杀（进程被外部终止，try/finally 没机会跑）时用它——那种情况下真实位置
  留着的是演示库，**千万不要直接重跑**，重跑会把演示库当基线备份下来再"忠实还原"，
  三条 ✅ 全打勾而真实数据已经没了。2026-07-30 实测踩过一次。

.PARAMETER Scale
  期望的显示缩放百分比，**默认 100**——成品就是 100% 拍的（859×676，dip 几何与 mac
  逐位相同）。2026-07-30 用户定：「恢复到 100%，之前有记录 200% 应该是错误的」。
  默认值刻意与结论一致：默认 200 的话，谁随手一跑就把机器的全局缩放改掉了。

  当前缩放不是这个值时，脚本**自己改**（`WindowTool scale set`，走的是系统「设置」
  同一条 DisplayConfig 路径，立即生效不用注销），不再要人去点界面。两个要知道的后果：
  · 改的是全局缩放，会重排屏幕上所有已打开的窗口；
  · 拍完**不改回**——约定是把机器留在拍摄缩放上，免得每次重拍来回切、
    也免得中途失败时留下半吊子状态。要改回自己跑 `WindowTool scale set 100`。

  改不动时会停下来说明原因：这条路要求主显示器在 Windows 显示配置库里有活动路径，
  远程会话 / 虚拟显示适配器驱动的桌面通常没有（那种情况下系统「设置」里也改不动）。

.PARAMETER App
  要拍的 AgentTimeline.exe，默认取仓库 Release 构建产物。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1
  powershell -ExecutionPolicy Bypass -File windows\scripts\shots\shoot-readme.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Recover,
    [int]$Scale = 100,
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
# ⚠ 640×580 是**与 mac 对齐**的尺寸，不是按 Windows 版面调出来的：
#   mac 那边 640 宽是为了命令原文不被溢出的词典弹层截断、580 高让收尾正好落在
#   卡片边界。Windows 弹层不溢出面板（见下），580 高实测切在末条卡片中间——
#   属已知观感差异，**不要为此改尺寸**：两端同 dip 几何才是这组图的意义所在。
$PANEL_W_DIP = 640
$PANEL_H_DIP = 580
$PANEL_X_DIP = 300     # 屏幕落点（左上原点）
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

# ── 前置：缩放必须对，否则产出的是半尺寸/异比例的图，且**看不出来**
Write-Host '── 显示缩放'
& dotnet build (Join-Path $PSScriptRoot 'WindowTool\WindowTool.csproj') -c Release -o $bin -v quiet --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'WindowTool 编译失败' }
$tool = Join-Path $bin 'WindowTool.exe'
$primary = (& $tool dpi | Select-Object -First 1)
Write-Host "   $primary"
$actual = [int]([regex]::Match($primary, 'scale=(\d+)%').Groups[1].Value)

if ($actual -ne $Scale) {
    # 脚本自己改，不再要求人去点「设置」。改完**不改回**：约定是把机器留在拍摄缩放上，
    # 免得每次重拍都要来回切、也免得中途失败留下半吊子状态。
    Write-Host "   主显示器 $actual% → $Scale%（会重排屏幕上所有已打开的窗口）"
    $scaleOut = & $tool scale set $Scale 2>&1
    if ($LASTEXITCODE -ne 0) {
        # 只报工具原话，不替它归因——写死一套"可能原因"会把真正的原因盖掉
        # （实测踩过：参数解析的 bug 被这段文案说成了"虚拟显示器改不动"）。
        throw (
            "改缩放失败：$scaleOut`n" +
            "  若信息里是「没有活动显示路径」，说明主显示器不在 Windows 显示配置库（CCD）里" +
            "——远程会话 / 虚拟显示适配器驱动的桌面常见此结果，那种情况下系统「设置」里同样改不动。`n" +
            "  绕过办法：显式传 -Scale $actual 按当前缩放拍（比例与 dip 几何不变，像素密度减半）。")
    }
    Write-Host "   $scaleOut"
    Start-Sleep -Seconds 2   # 等 DWM 把新 DPI 广播下去，否则随后启动的应用仍按旧 DPI 布局

    $primary = (& $tool dpi | Select-Object -First 1)
    $actual = [int]([regex]::Match($primary, 'scale=(\d+)%').Groups[1].Value)
    if ($actual -ne $Scale) {
        throw "改缩放后读回仍是 $actual%（要求 $Scale%）——设置没有真正生效，停下来别拍出错图。"
    }
    Write-Host "   已生效：$primary"
}

# 面板高度换算成物理像素后不能超出屏幕可用高度：超了 PrintWindow 抓到的下半截是未渲染区，
# 而产出的图**尺寸完全正常**，属于最坏的静默失败。
$screenH = [int]([regex]::Match($primary, '\d+x(\d+)').Groups[1].Value)
if ($screenH -gt 0 -and ($PANEL_H_DIP * $Scale / 100) -gt $screenH) {
    throw ("面板 ${PANEL_H_DIP}dip @ $Scale% = $($PANEL_H_DIP * $Scale / 100)px 高，" +
           "超过主显示器的 ${screenH}px。换更高的屏，或用更低的 -Scale。")
}

# ── 断电级保护：try/finally 挡不住进程被硬杀
#
# 2026-07-30 实测踩过一次真实数据丢失，链条是这样的：
#   1. 一轮拍摄在「已写入演示库、尚未还原」时被**外部强杀**（不是异常、不是 Ctrl-C，
#      finally 根本没机会跑）→ 演示库留在了真实位置；
#   2. 下一轮开跑，把**演示库当成真实基线**备份下来，拍完又忠实地还原回去
#      → 真实数据被水泥封死，而且那三条 ✅ 全部打勾，因为它确实"原样还原"了。
# 第 2 步才是要命的：校验通过、数据没了。
#
# 所以加两样东西：
#   · 进行中标记：写在数据目录里、还原成功才删。开跑先看它，在就说明上一轮没收尾，
#     **拒绝备份当前库**并指出救援路径；
#   · 稳定备份位置：备份除了留在本轮临时目录，再放一份到数据目录下的固定路径，
#     救援时不必去猜是哪个 at-shots-* 目录。
$marker    = Join-Path $data '.shoot-in-progress'
$safeBackup = Join-Path $data '.shoot-backup'

function Clear-Marker {
    if (Test-Path $marker) { Remove-Item -LiteralPath $marker -Force }
}

function Set-Marker {
    $body = "  基线 md5=$script:beforeDbMd5",
            "  基线计数=$($script:beforeCounts -replace "`r?`n", ' / ')",
            "  固定备份=$safeBackup"
    $body -join "`n" | Set-Content -LiteralPath $marker -Encoding UTF8
}

function Test-StaleRun {
    if (-not (Test-Path $marker)) { return }
    $info = (Get-Content $marker -Raw).Trim()
    throw (
        "上一轮拍摄没有收尾就被中断，**当前 timeline.db 很可能是演示库**，不能拿它当基线。`n" +
        "  中断标记: $marker`n$info`n" +
        "  救援：先确认 $safeBackup\timeline.db 的节点数是你的真实量级，" +
        "再用它覆盖 $db（连 -wal/-shm 一起删掉重来），然后删除标记文件重跑。`n" +
        "  或者跑 `-Recover` 让脚本替你做这件事。")
}

function Invoke-Recover {
    $bakDb = Join-Path $safeBackup 'timeline.db'
    if (-not (Test-Path $bakDb)) { throw "没有可用的救援备份：$bakDb 不存在" }
    Write-Host '── 救援：用固定备份覆盖真实位置'
    Stop-App
    Write-Host "   备份: $(Get-DbCounts $bakDb)"
    # 演示库的 -wal/-shm 必须删掉：留着会被当成新库的日志重放
    foreach ($f in $dbFiles) {
        $live = Join-Path $data $f
        if (Test-Path $live) { Remove-Item $live -Force }
    }
    Copy-Item $bakDb $db -Force
    $s = Join-Path $safeBackup 'settings.json'
    if (Test-Path $s) { Copy-Item $s $settings -Force }
    Write-Host "   还原后: $(Get-DbCounts $db)"
    Clear-Marker
    Write-Host '   ✅ 已还原并清除中断标记；起一次应用会按 file_offsets 重扫补齐空窗期'
    Start-Process $App
}

$restored = $false
$swapped = $false      # 真的动过真实文件了吗
function Restore-Real {
    if ($script:restored) { return }
    $script:restored = $true

    # ⚠ 没进入交换阶段就**一个文件都不要动**。
    #
    # finally 会在 try 里**任何**位置抛出时跑到这里——包括备份步骤之前（比如
    # Test-StaleRun 拦下来的那种）。那时 $backup 是空目录，而下面的循环是
    # 「先删真实文件、再从备份拷回」——删得掉、拷不回，等于直接毁数据；
    # 随后的 Start-Process 还会让应用重建一个空库并开始回填，把现场彻底盖掉。
    # 2026-07-30 实测踩过一次：加防护的那次测试自己把真实库删了。
    if (-not $script:swapped -or -not (Test-Path (Join-Path $backup 'timeline.db'))) {
        Write-Host '── 未进入交换阶段，真实环境未被改动，无需还原'
        return
    }

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
    # 三项都对上才算收尾，标记才能清——留着标记好过让下一轮把演示库当成基线
    if ($afterCounts -eq $script:beforeCounts -and $afterDbMd5 -eq $script:beforeDbMd5 `
        -and $afterSetMd5 -eq $script:beforeSetMd5) {
        Clear-Marker
    }
    else {
        Write-Warning "   ⚠ 还原未完全对上，**保留**中断标记 $marker；固定备份在 $safeBackup"
    }
    Start-Process $App
}

if ($Recover) { Invoke-Recover; return }

try {
    Test-StaleRun
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
    # 再落一份到固定位置：救援时不必去猜是哪个 at-shots-* 临时目录
    New-Item -ItemType Directory -Force -Path $safeBackup | Out-Null
    Get-ChildItem $backup -File | ForEach-Object { Copy-Item $_.FullName $safeBackup -Force }
    Write-Host "   基线 db md5=$script:beforeDbMd5"
    Write-Host "   $($script:beforeCounts -replace "`r?`n", ' / ')"
    if ($script:beforeDbMd5 -eq '<无>') { throw '没有真实 db 可备份，先跑一次应用' }

    # 标记与 $swapped 都必须在**动真实文件之前**立起来：
    # 标记立晚了、正好在这中间被杀，下一轮会把演示库当基线；
    # $swapped 立晚了，还原逻辑会以为没交换过而跳过还原，演示库就留在真实位置。
    Set-Marker
    $script:swapped = $true
    Write-Host '── 演示配置 + 演示数据'
    # ⚠ 键名以 AppSettings 属性名为准：写错等于没关，真实 session 会混进演示库
    $demo = [ordered]@{
        Engine = 'Rule'; CliCommand = 'auto'
        # 语言必须**钉死**，不能留「跟随系统」：否则产出的语言取决于拍摄机的系统 UI 语言
        # （本机是 en-US，一跑就拍出英文图），而这是最坏的一类失败——图看着完全正常。
        # README 三张是中文说明的配图，故钉 ZhHans。
        Language = 'ZhHans'
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
