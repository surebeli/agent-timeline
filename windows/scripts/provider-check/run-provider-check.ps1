<#
.SYNOPSIS
  provider 档端到端校验：真 HTTP 端点 → 落库 source=Provider。

.DESCRIPTION
  补上 `windows/README.md` 长期挂着的未闭环项「provider 档未接过真端点」。此前只用
  假端点（连接被拒）验过失败链路，成功链路一次没跑通过。本脚本打通整条：

      设置里的 baseUrl（不带 /v1）
        → BuildChatCompletionsUrl 自动补 /v1
        → Bearer 头 + temperature=0 + BuildPrompt 正文
        → HTTP 200
        → 解析 choices[0].message.content
        → SummaryJson.Parse
        → 落库 summary_source='Provider' + LLM 标题替换规则标题

  端点用 mock-provider.ps1（真 HTTP 服务、真协议，本机 127.0.0.1）。覆盖不到的只有
  某个具体厂商的响应怪癖——那个要用真厂商端点验，凭据不该经手脚本，见文末提示。

  数据安全同 §3b 铁律：真实 db 与 settings 先备份，文件级交换不删目录，try/finally
  兜底，还原后用分 agent 计数 + md5 双重核验。

  ⚠ 校验窗口内 claude 监听必须开着（fixture 是一个 claude session），期间真实会话
  新追加的命令也会被送进本地 mock。mock **不记录 prompt 正文**（只留长度与哈希），
  且全程不出本机；库随后整体还原。

.PARAMETER Port
  mock 端点端口，默认 8760。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File windows\scripts\provider-check\run-provider-check.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 8760,
    [string]$App = '',
    [string]$Work = ''
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $App) {
    $App = Join-Path $repo 'windows\AgentTimeline\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\AgentTimeline.exe'
}
if (-not (Test-Path $App)) { throw "找不到应用: $App（先 msbuild 构建）" }
if (-not $Work) { $Work = Join-Path $env:TEMP ("at-provider-" + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
$backup = Join-Path $Work 'backup'
New-Item -ItemType Directory -Force -Path $Work, $backup | Out-Null

$data     = Join-Path $env:LOCALAPPDATA 'AgentTimeline'
$db       = Join-Path $data 'timeline.db'
$settings = Join-Path $data 'settings.json'
$dbFiles  = @('timeline.db', 'timeline.db-wal', 'timeline.db-shm')
$mockLog  = Join-Path $Work 'mock.log'
$title    = 'provider 端点连通探针'

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

$probePy = Join-Path $Work 'probe.py'
@'
import sqlite3, sys
c = sqlite3.connect(sys.argv[1])
rows = c.execute("select id, summary_source, title from nodes where summary_source='Provider'").fetchall()
print(len(rows))
for r in rows[:3]:
    print(r[0], r[1], r[2])
'@ | Set-Content $probePy -Encoding UTF8

function Stop-App {
    Get-Process AgentTimeline -ErrorAction SilentlyContinue | Stop-Process -Force
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Process AgentTimeline -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    Start-Sleep -Seconds 1
}
function Get-DbCounts([string]$p) {
    if (-not (Test-Path $p)) { return '<无库>' }
    return (python $script:countPy $p 2>&1 | Out-String).Trim()
}
function Get-Md5([string]$p) {
    if (-not (Test-Path $p)) { return '<无>' }
    return (Get-FileHash $p -Algorithm MD5).Hash
}

$restored = $false
$mock = $null
function Restore-Real {
    if ($script:restored) { return }
    $script:restored = $true
    Write-Host '── 还原真实环境'
    Stop-App
    if ($script:mock -and -not $script:mock.HasExited) { Stop-Process -Id $script:mock.Id -Force -ErrorAction SilentlyContinue }
    foreach ($f in $dbFiles) {
        $live = Join-Path $data $f
        $bak = Join-Path $backup $f
        if (Test-Path $live) { Remove-Item $live -Force -ErrorAction SilentlyContinue }
        if (Test-Path $bak) { Copy-Item $bak $live -Force }
    }
    if (Test-Path (Join-Path $backup 'settings.json')) { Copy-Item (Join-Path $backup 'settings.json') $settings -Force }
    $afterCounts = Get-DbCounts $db
    if ($afterCounts -eq $script:beforeCounts) { Write-Host '   ✅ 节点/词典计数一致' }
    else { Write-Warning "   ❌ 计数不一致！备份仍在 $backup`n     前: $script:beforeCounts`n     后: $afterCounts" }
    $m = Get-Md5 $db
    if ($m -eq $script:beforeDbMd5) { Write-Host "   ✅ db md5 一致 $m" }
    else { Write-Warning "   ❌ db md5 不一致 $script:beforeDbMd5 vs $m（备份在 $backup）" }
    if ((Get-Md5 $settings) -eq $script:beforeSetMd5) { Write-Host '   ✅ settings.json md5 一致' }
    else { Write-Warning '   ❌ settings.json md5 不一致（备份在 $backup）' }
    Start-Process $App
}

try {
    Write-Host '── 备份真实 db 与设置'
    Stop-App
    $script:beforeCounts = Get-DbCounts $db
    $script:beforeDbMd5 = Get-Md5 $db
    $script:beforeSetMd5 = Get-Md5 $settings
    foreach ($f in $dbFiles) { $l = Join-Path $data $f; if (Test-Path $l) { Copy-Item $l (Join-Path $backup $f) -Force } }
    if (Test-Path $settings) { Copy-Item $settings (Join-Path $backup 'settings.json') -Force }
    if ($script:beforeDbMd5 -eq '<无>') { throw '没有真实 db 可备份，先跑一次应用' }
    Write-Host "   基线 db md5=$script:beforeDbMd5"

    Write-Host "── 起本地 OpenAI 兼容端点 :$Port"
    # ⚠ 两处都栽过：
    #   1. -ArgumentList 数组按空格拼接且**不代加引号**，$title/$mockLog 里只要有空格
    #      就会被切成游离参数、powershell 绑定失败即退——必须自己加引号；
    #   2. -WindowStyle Hidden 不重定向 = 失败原因全丢，只剩一句猜出来的"端口被占"。
    $mockOut = Join-Path $Work 'mock-stdout.log'
    $mockErr = Join-Path $Work 'mock-stderr.log'
    $script:mock = Start-Process powershell -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $mockOut -RedirectStandardError $mockErr `
        -ArgumentList @(
            '-ExecutionPolicy', 'Bypass',
            '-File', "`"$(Join-Path $PSScriptRoot 'mock-provider.ps1')`"",
            '-Port', $Port,
            '-LogPath', "`"$mockLog`"",
            '-Title', "`"$title`"")
    Start-Sleep -Seconds 4
    if ($script:mock.HasExited) {
        $err = if (Test-Path $mockErr) { (Get-Content $mockErr -Raw -Encoding UTF8).Trim() } else { '<无 stderr>' }
        throw "mock 端点起不来（退出码 $($script:mock.ExitCode)）。stderr:`n$err"
    }

    Write-Host '── 写 provider 演示配置'
    # baseUrl **故意不带 /v1**：要验的正是 BuildChatCompletionsUrl 的自动补全
    ([ordered]@{
        Engine = 'Provider'; CliCommand = 'auto'
        ProviderBaseUrl = "http://127.0.0.1:$Port"
        ProviderApiKey = 'at-provider-check-key'
        ProviderModel = 'mock-model'
        HoverOpacity = 0.95; IdleOpacity = 0.95; AlwaysOnTop = $false
        WindowX = 200; WindowY = 200; WindowWidth = 500; WindowHeight = 600
        BackfillDays = 0
        EnableClaude = $true      # fixture 是 claude session，必须开
        EnableCodex = $false; EnableGrok = $false; EnableKimi = $false; EnableZcode = $false
        ZcodeSessionRoot = ''; CodenameReplayVersion = 99
    } | ConvertTo-Json) | Set-Content $settings -Encoding UTF8

    foreach ($f in $dbFiles) { $l = Join-Path $data $f; if (Test-Path $l) { Remove-Item $l -Force } }

    Write-Host '── 起应用（建库 + 开监听）'
    Start-Process $App
    Start-Sleep -Seconds 15

    Write-Host '── 灌 fixture session（产生待摘要节点）'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $repo 'windows\scripts\seed-fixture-session.ps1') | Out-Null

    Write-Host '── 等 provider 摘要落库（最多 120s）'
    $hit = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 3
        $out = (python $probePy $db 2>&1 | Out-String).Trim() -split "`r?`n"
        if ($out[0] -match '^\d+$' -and [int]$out[0] -gt 0) {
            Write-Host "   ✅ 命中 $($out[0]) 条 summary_source='Provider'"
            $out | Select-Object -Skip 1 | ForEach-Object { Write-Host "      $_" }
            $hit = $true
            break
        }
    }
    if (-not $hit) { Write-Warning '   ❌ 120s 内没有任何节点落成 Provider' }

    Write-Host ''
    Write-Host '── mock 端点收到的请求（核对形态）'
    if (Test-Path $mockLog) { Get-Content $mockLog -Encoding UTF8 | Select-Object -First 20 | ForEach-Object { Write-Host "   $_" } }

    Write-Host ''
    Write-Host '── 判定'
    $log = if (Test-Path $mockLog) { Get-Content $mockLog -Raw -Encoding UTF8 } else { '' }
    $checks = [ordered]@{
        '落库 summary_source=Provider'        = $hit
        'baseUrl 不带 /v1 时自动补全'         = ($log -match '/v1/chat/completions')
        'Bearer 鉴权头带上了'                 = ($log -match 'Authorization : Bearer')
        'model 透传设置值'                    = ($log -match 'model\s+: mock-model')
        'temperature=0（摘要要可复现）'       = ($log -match 'temperature\s+: 0')
    }
    $allOk = $true
    foreach ($k in $checks.Keys) {
        Write-Host ("   {0} {1}" -f $(if ($checks[$k]) { '✅' } else { '❌' }), $k)
        if (-not $checks[$k]) { $allOk = $false }
    }
    Write-Host ''
    Write-Host "── 工作目录: $Work"
    if (-not $allOk) { Write-Warning 'provider 链路未全绿——先还原再排障' }
}
finally {
    Restore-Real
}

Write-Host ''
Write-Host '提示：要验**真厂商端点**，不要把 key 交给脚本或贴进对话——在应用「设置」里'
Write-Host '      自己填 Base URL / Key / Model，然后看 %LOCALAPPDATA%\AgentTimeline\logs\app.log'
Write-Host '      有无 ProviderSummarizer 的 HTTP 警告，以及库里是否出现 summary_source=Provider。'
