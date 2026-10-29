# start-remote.ps1 - 在局域网内以「口令鉴权」的方式开放 DSH Web（默认关闭，显式启动）
#
# 用法（双击 start-remote.cmd 亦可）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File start-remote.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File start-remote.ps1 -Port 3081 -NewToken
#   powershell -NoProfile -ExecutionPolicy Bypass -File start-remote.ps1 -AllowPublic
#
# 行为：
#   1. 从 run/dsh-web.port -> dsh-web.config 依次取「本地服务端口」，服务没在跑就直接拒绝；
#   2. 口令存在 run/remote-token.txt（首次运行自动生成 48 位十六进制）；-NewToken 重新生成；
#   3. 前台运行 remote-proxy.mjs：令牌校验 + WebSocket 透传 + 仅接受内网来源；
#   4. 关闭这个窗口 / Ctrl+C 即立刻收回入口（不留后台进程）。
#
# 安全边界（务必读完再开）：
#   * 持有口令 = 可以在本机执行命令（DSH 的 agent 有 shell 权限）；
#   * 只监听局域网、只接受内网来源地址；公网暴露必须显式加 -AllowPublic；
#   * 上游 dsh 本身仍然只绑 127.0.0.1 且无鉴权——这里是一道独立的、可随时关闭的门。
param(
    [int]$Port = 3081,
    [switch]$NewToken,
    [switch]$AllowPublic,
    [string]$Bind = '0.0.0.0'
)
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
$logsDir = Join-Path $dir 'logs'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
$tokenFile = Join-Path $runDir 'remote-token.txt'
$proxy = Join-Path $dir 'remote-proxy.mjs'

function Get-LocalDshPort {
    $portFile = Join-Path $runDir 'dsh-web.port'
    if (Test-Path -LiteralPath $portFile) {
        $raw = ((Get-Content -LiteralPath $portFile -ErrorAction SilentlyContinue) -join '').Trim()
        $p = 0
        if ([int]::TryParse($raw, [ref]$p) -and $p -gt 0) { return $p }
    }
    $cfg = Join-Path $dir 'dsh-web.config'
    if (Test-Path -LiteralPath $cfg) {
        $m = Select-String -LiteralPath $cfg -Pattern '^\s*port\s*=\s*(\d+)'
        if ($m) { return [int]$m.Matches[0].Groups[1].Value }
    }
    return 3080
}

function Test-PortOpen([int]$p) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $ar = $c.BeginConnect('127.0.0.1', $p, $null, $null)
        if ($ar.AsyncWaitHandle.WaitOne(500)) { $c.EndConnect($ar); return $true }
    } catch { }
    finally { $c.Close() }
    return $false
}

$localPort = Get-LocalDshPort
if (-not (Test-PortOpen $localPort)) {
    Write-Host ("[remote] 本地服务没有运行（127.0.0.1:{0} 无监听）。请先打开面板/启动服务。" -f $localPort)
    exit 1
}

if (-not (Test-Path -LiteralPath $proxy)) {
    Write-Host "[remote] 缺少 remote-proxy.mjs，无法启动。"
    exit 1
}

# ---- 口令：首次自动生成，-NewToken 重新生成 ----
if ($NewToken -or -not (Test-Path -LiteralPath $tokenFile)) {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $hex = -join ($bytes | ForEach-Object { $_.ToString('x2') })
    Set-Content -LiteralPath $tokenFile -Value $hex -Encoding ASCII
    Write-Host ("[remote] 已生成新口令：{0}" -f $tokenFile)
}

$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
if (-not (Test-Path -LiteralPath $node)) {
    Write-Host "[remote] 找不到 node.exe。"
    exit 1
}

$token = (Get-Content -LiteralPath $tokenFile -Raw).Trim()
if ($token.Length -lt 16) {
    Write-Host "[remote] 口令文件内容异常（太短）。删掉 run\remote-token.txt 后重试（会自动重新生成）。"
    exit 1
}

$nodeArgs = @($proxy, '--target-port', [string]$localPort, '--listen-port', [string]$Port, '--token-file', $tokenFile, '--bind', $Bind)
if ($AllowPublic) { $nodeArgs += '--allow-public' }

Write-Host ""
Write-Host ("  本地服务：http://127.0.0.1:{0}" -f $localPort)
Write-Host ("  监听    ：{0}:{1}" -f $Bind, $Port)
Write-Host ("  口令文件：{0}" -f $tokenFile)
Write-Host "  关闭此窗口（或 Ctrl+C）即收回入口。"
Write-Host ""

# 前台运行：异常退出由这里报告，退出码原样透传
& $node @nodeArgs
exit $LASTEXITCODE
