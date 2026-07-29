<#
.SYNOPSIS
  引子续接（docs/TEXT-NORMALIZATION.md §3.3b）的差分执行编排。

.DESCRIPTION
  在本机真实 agent 语料上，用「改动前」「改动后」两个源码状态各跑一遍
  ParserUtil.ResultExcerpt，出 §3.3b 那张指标表。

  改动前的源码状态用 `git worktree` 拿（默认 259a458^，即引子续接落地前的父提交），
  不动工作区——`git stash` 会在语料扫描跑到一半时改掉当前分支的文件，且失败中断
  会把改动留在 stash 里；worktree 是只读的另一份检出，随起随删。

  语料只抽一次并冻结成快照，前后两次吃同一份：`.claude\projects` /
  `.codex\sessions` 正被真实会话写入，各扫各的就不是同一份语料了。

  ⚠ 产出含真实项目名与命令原文，只写临时目录，**不要入仓**。

.PARAMETER BeforeRef
  「改动前」的 git ref。默认 259a458^（引子续接提交的父）。

.PARAMETER Agents
  语料来源，逗号分隔：claude,codex,grok,kimi,zcode。默认 claude,codex（§3.3b 的口径）。

.PARAMETER Work
  工作目录。默认在 TEMP 下新建。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File windows\scripts\leadin-diff\run-diff.ps1
#>
[CmdletBinding()]
param(
    [string]$BeforeRef = '259a458^',
    [string]$Agents    = 'claude,codex',
    [string]$Work      = ''
)

$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$here = $PSScriptRoot
if (-not $Work) { $Work = Join-Path $env:TEMP ("leadin-diff-" + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$beforeSrc = Join-Path $Work 'before-src'      # git worktree（改动前的整棵树）
$afterBin  = Join-Path $Work 'bin-after'
$beforeBin = Join-Path $Work 'bin-before'
$corpus    = Join-Path $Work 'corpus.tsv'
$afterTsv  = Join-Path $Work 'after.tsv'
$beforeTsv = Join-Path $Work 'before.tsv'
$samples   = Join-Path $Work 'samples.txt'

# 工具源码原地不动，只把两份编译产物分开放；CoreRoot 决定链接哪一份 Core/。
function Build-Tool([string]$coreRoot, [string]$outDir, [string]$label) {
    Write-Host "── 编译 $label（CoreRoot=$coreRoot）"
    $proj = Join-Path $here 'LeadInDiff.csproj'
    # CoreRoot 末尾不能带反斜杠：会把命令行的引号转义掉。
    $coreRoot = $coreRoot.TrimEnd('\')
    & dotnet build $proj -c Release -o $outDir "-p:CoreRoot=$coreRoot" -v quiet --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$label 编译失败" }
    return (Join-Path $outDir 'LeadInDiff.dll')
}

$worktreeAdded = $false
try {
    Write-Host "── 取改动前的源码状态: $BeforeRef"
    & git -C $repo worktree add --detach $beforeSrc $BeforeRef | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git worktree add 失败" }
    $worktreeAdded = $true

    $afterDll  = Build-Tool (Join-Path $repo   'windows\AgentTimeline') $afterBin  '改动后'
    $beforeDll = Build-Tool (Join-Path $beforeSrc 'windows\AgentTimeline') $beforeBin '改动前'

    Write-Host "── 抽语料并冻结（agents=$Agents）"
    & dotnet $afterDll extract $corpus $Agents
    if ($LASTEXITCODE -ne 0) { throw "抽语料失败（一条都没抽到？）" }

    Write-Host "── 改动后摘录"; & dotnet $afterDll  excerpt $corpus $afterTsv
    if ($LASTEXITCODE -ne 0) { throw "改动后摘录失败" }
    Write-Host "── 改动前摘录"; & dotnet $beforeDll excerpt $corpus $beforeTsv
    if ($LASTEXITCODE -ne 0) { throw "改动前摘录失败" }

    & dotnet $afterDll compare $beforeTsv $afterTsv $samples
    $verdict = $LASTEXITCODE

    # 「续接后仍以冒号收尾」的残留数两端差一个量级（mac 2 条 / win 四位数），
    # 归因清楚才敢说不是实现少接了——顺手跑，不参与判定。
    Write-Host "── 冒号残留归因"
    & dotnet $afterDll residual $corpus $afterTsv (Join-Path $Work 'residual.txt') | Out-Null
    # -Encoding UTF8 不能省：工具写的是无 BOM 的 UTF-8，Windows PowerShell 5.1
    # 默认按 ANSI 读，中文回显会整段变成乱码
    Write-Host (Get-Content (Join-Path $Work 'residual.txt') -Raw -Encoding UTF8)

    Write-Host "── 工作目录: $Work"
    if ($verdict -ne 0) {
        Write-Host "❌ 硬约束不成立（变短 >0 或 前缀不成立）——按 SYNC-KICKOFF 要求停下来报告，不要自行改规范" -ForegroundColor Red
    }
    exit $verdict
}
finally {
    if ($worktreeAdded) {
        & git -C $repo worktree remove --force $beforeSrc 2>&1 | Out-Null
    }
}
