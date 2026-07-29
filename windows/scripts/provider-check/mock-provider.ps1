<#
.SYNOPSIS
  本地 OpenAI 兼容端点（provider 档校验用）。

.DESCRIPTION
  `ProviderSummarizer` 的**成功链路**此前从未跑通过——只用假端点验过「连接被拒 →
  降级规则 → 进程存活」的失败链路。这个 mock 是真 HTTP 服务、真协议，用来打通
  补 `/v1` → Bearer 头 → temperature=0 → 解析 choices[0].message.content →
  SummaryJson.Parse → 落库 source=Provider 这一整条。

  覆盖不到的只有某个具体厂商的响应怪癖；那个要用真厂商端点验，凭据不该经手脚本。

  ⚠ 隐私：**不记录 prompt 正文**。真实会话的命令原文会被送进来（应用开着监听时），
  日志只留元信息 + 正文长度与 SHA256，足够核对请求形态，又不落地用户内容。

.PARAMETER Port
  监听端口，默认 8760。

.PARAMETER LogPath
  请求日志落点。

.PARAMETER Title
  mock 返回的摘要标题——取个一眼能认出来源的值，落库后即可证明这条摘要来自 provider。
#>
[CmdletBinding()]
param(
    [int]$Port = 8760,
    [string]$LogPath = "$env:TEMP\at-provider-mock.log",
    [string]$Title = 'provider 端点连通探针'
)

$ErrorActionPreference = 'Stop'

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
"[$(Get-Date -Format o)] listening on http://127.0.0.1:$Port/" | Out-File $LogPath -Encoding UTF8
Write-Host "mock provider listening on http://127.0.0.1:$Port/  (日志: $LogPath)"

# 摘要 JSON 契约见 Core/Summarize/ISummarizer.cs SummaryJson.ParseObject：
# title 必填且非空，否则整条判为解析失败。
$summary = @{
    title      = $Title
    kind       = '任务'
    keyPoints  = @('mock 端点返回', 'source 应落为 Provider')
    codenames  = @()
    resultLine = $null
} | ConvertTo-Json -Compress -Depth 5

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $req = $ctx.Request
        $body = ''
        if ($req.HasEntityBody) {
            $reader = [System.IO.StreamReader]::new($req.InputStream, [System.Text.Encoding]::UTF8)
            $body = $reader.ReadToEnd()
            $reader.Close()
        }

        # 只从 body 里取**形态**信息，不留正文
        $model = ''; $temp = ''; $promptLen = 0; $promptHash = ''; $parseErr = ''
        try {
            $j = $body | ConvertFrom-Json
            $model = $j.model
            $temp = $j.temperature
            $prompt = [string]$j.messages[0].content
            $promptLen = $prompt.Length
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try {
                $promptHash = [System.BitConverter]::ToString(
                    $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($prompt))
                ).Replace('-', '').Substring(0, 12)
            } finally { $sha.Dispose() }
        } catch {
            # 不吞异常：解析不出来本身就是要查的信号（请求形态变了 / 不是我们的调用方）
            $parseErr = $_.Exception.Message
        }

        $auth = $req.Headers['Authorization']
        $authShape = if ($auth) { ($auth -split ' ')[0] + ' <redacted:' + (($auth.Length - 7)) + '字符>' } else { '<无>' }
        @(
            "[$(Get-Date -Format o)] $($req.HttpMethod) $($req.Url.AbsolutePath)"
            "    Authorization : $authShape"
            "    Content-Type  : $($req.ContentType)"
            "    model         : $model"
            "    temperature   : $temp"
            "    prompt        : $promptLen 字符  sha256:$promptHash（正文按隐私要求不记录）"
            $(if ($parseErr) { "    ⚠ body 解析失败 : $parseErr" })
        ) | Where-Object { $_ } | Out-File $LogPath -Append -Encoding UTF8

        $payload = @{
            id      = 'chatcmpl-mock'
            object  = 'chat.completion'
            model   = $model
            choices = @(@{
                index         = 0
                message       = @{ role = 'assistant'; content = $summary }
                finish_reason = 'stop'
            })
        } | ConvertTo-Json -Compress -Depth 8

        $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $ctx.Response.StatusCode = 200
        $ctx.Response.ContentType = 'application/json'
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
