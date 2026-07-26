# Seed a fixture Claude Code session so the full pipeline (watcher → parser →
# store → codename lifecycle → ledger UI) can be debugged on a Windows machine
# with no agent CLI installed. Format follows docs/SESSION-FORMATS.md §1.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File windows\scripts\seed-fixture-session.ps1
#   ... -Bulk 500        # additionally append N synthetic nodes for scroll/perf testing
#   ... -Append          # only append one live-tail probe line to the existing session
param(
    [int]$Bulk = 0,
    [switch]$Append
)

$ErrorActionPreference = "Stop"
$dir = Join-Path $env:USERPROFILE ".claude\projects\-fixture-demo"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$file = Join-Path $dir "fixture-session-0001.jsonl"
$cwd = "C:\\Users\\$env:USERNAME\\fixture-demo"

function New-Line([string]$type, [string]$content, [datetime]$ts) {
    $iso = $ts.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    $obj = [ordered]@{
        type      = $type
        message   = [ordered]@{ role = $type; content = $content }
        uuid      = [guid]::NewGuid().ToString()
        timestamp = $iso
        cwd       = $cwd.Replace('\\', '\')
        sessionId = "fixture-session-0001"
        gitBranch = "main"
    }
    return ($obj | ConvertTo-Json -Compress -Depth 5)
}

# Windows PowerShell 5.1's -Encoding utf8 writes a BOM, which breaks the first
# jsonl line's JSON parse — always write BOM-less via .NET.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if ($Append) {
    $probe = New-Line "user" ("实时 tail 探针 " + (Get-Date -Format "HH:mm:ss") + "，T2 继续推进") (Get-Date)
    [IO.File]::AppendAllLines($file, [string[]]@($probe), $utf8NoBom)
    Write-Host "appended live-tail probe -> $file"
    exit 0
}

# 无参重跑时删除重建：原地截断重写不改变 NTFS fileId 且新旧内容长度相同，
# watcher 的两个重扫条件（fileId 变化 / offset > length）都不会触发，重写对
# watcher 完全不可见（M3 实机审计发现）。删除重建 → 新 fileId → 归零重扫。
if (Test-Path $file) { Remove-Item $file -Force }

$t = (Get-Date).AddHours(-3)
$lines = @(
    (New-Line "user" "帮我规划登录模块改造，把需求整理编号" $t),
    (New-Line "assistant" "好的，需求编号如下：`nN1: 登录页视觉改版`nN2: 支付流程重构`nN3: 消息中心优化" $t.AddMinutes(1)),
    (New-Line "user" "按优先级拆任务：T1: 先做 N1 的页面骨架，T2: 打通 N2 的退款接口，另外记录 REQ-AUTH-3: 第三方账号绑定" $t.AddMinutes(5)),
    (New-Line "assistant" "任务已登记，开始执行 T1。" $t.AddMinutes(6)),
    (New-Line "user" "N2完成，N3变更：改为只做红点提醒" $t.AddMinutes(70)),
    (New-Line "user" "T1 完成，接下去执行T2" $t.AddMinutes(95)),
    (New-Line "assistant" "T2 已开始：退款接口联调中，预计两小时完成。" $t.AddMinutes(96)),
    (New-Line "user" "调研一下 REQ-AUTH-3 需要的 OAuth 供应商，输出对比" $t.AddMinutes(120))
)

if ($Bulk -gt 0) {
    $bulkStart = (Get-Date).AddDays(-3)
    for ($i = 1; $i -le $Bulk; $i++) {
        $lines += New-Line "user" "批量灌注节点 #$i：滚动与回收性能测试用" $bulkStart.AddMinutes($i * 2)
    }
}

[IO.File]::WriteAllLines($file, [string[]]$lines, $utf8NoBom)
Write-Host "seeded $($lines.Count) lines -> $file"
Write-Host "expect: timeline nodes + dictionary N1/N2/N3/T1/T2/REQ-AUTH-3 (N2 completed, T1 completed, T2 active, N3 changed)"
