# check-update.ps1 - 比对已安装的 @deepseek-ai/dsh 与 npm 上的最新版本
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File check-update.ps1
#   ... -Force            忽略节流，立刻查一次
#   ... -MaxAgeHours 6    调整缓存有效期（默认 24 小时）
#   ... -Quiet            只写结果文件，不打印
#
# 输出：run/update-check.txt（key=value 纯文本，面板读它）
#   current=0.1.0-rc.7
#   latest=0.1.0-rc.8
#   upgrade=yes|no
#   checked=2026-10-29T22:40:00+08:00
#   error=...            （查询失败时才有）
#
# 安全/稳健：
#   * 只读操作，不安装任何东西——升级由用户自己确认后执行（见 -Quiet 之后的提示行）；
#   * 走 npm 自己的代理/镜像配置（比脚本直连 registry 更符合本机环境）；
#   * 网络请求自带 8 秒超时、0 次重试，失败只记 error=，绝不阻塞面板；
#   * 结果文件按 -MaxAgeHours 节流，避免每次启动都联网。
param(
    [switch]$Force,
    [int]$MaxAgeHours = 24,
    [switch]$Quiet
)
$ErrorActionPreference = 'SilentlyContinue'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$out = Join-Path $runDir 'update-check.txt'

function Write-Result([hashtable]$fields) {
    $lines = New-Object System.Collections.ArrayList
    foreach ($k in @('current', 'latest', 'upgrade', 'checked', 'error')) {
        if ($fields.ContainsKey($k)) { [void]$lines.Add($k + '=' + $fields[$k]) }
    }
    Set-Content -LiteralPath $out -Value ($lines -join "`r`n") -Encoding UTF8
}

# ---- 节流：结果文件足够新就直接返回 ----
if (-not $Force -and (Test-Path -LiteralPath $out)) {
    $age = (Get-Date) - (Get-Item -LiteralPath $out).LastWriteTime
    if ($age.TotalHours -lt $MaxAgeHours) {
        if (-not $Quiet) {
            $cached = @{}
            Get-Content -LiteralPath $out | ForEach-Object {
                $i = $_.IndexOf('='); if ($i -gt 0) { $cached[$_.Substring(0, $i)] = $_.Substring($i + 1) }
            }
            $msg = '缓存结果（' + [int]$age.TotalHours + ' 小时前）：current=' + $cached['current'] + ' latest=' + $cached['latest'] + ' upgrade=' + $cached['upgrade']
            Write-Host $msg
        }
        exit 0
    }
}

# ---- 定位已安装的 dsh（与 start-service.ps1 相同的顺序） ----
function Resolve-DshPackageJson {
    $paths = New-Object System.Collections.ArrayList
    $npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    if (Test-Path -LiteralPath $npxRoot) {
        foreach ($d in (Get-ChildItem -LiteralPath $npxRoot -Directory -ErrorAction SilentlyContinue)) {
            [void]$paths.Add((Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\package.json'))
        }
    }
    [void]$paths.Add((Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\package.json'))
    $best = $null
    $bestTime = [datetime]::MinValue
    foreach ($p in $paths) {
        if ($p -and (Test-Path -LiteralPath $p)) {
            $t = (Get-Item -LiteralPath $p).LastWriteTimeUtc
            if ((-not $best) -or ($t -gt $bestTime)) { $best = $p; $bestTime = $t }
        }
    }
    return $best
}

$current = ''
$pkg = Resolve-DshPackageJson
if ($pkg) {
    try { $current = (Get-Content -LiteralPath $pkg -Raw | ConvertFrom-Json).version } catch { }
}

# ---- 查 npm 上的最新版本 ----
$latest = ''
$err = ''
$npmCandidates = New-Object System.Collections.ArrayList
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
if (Test-Path -LiteralPath $node) {
    [void]$npmCandidates.Add((Join-Path (Split-Path -Parent $node) 'npm.cmd'))
}
[void]$npmCandidates.Add('npm.cmd')

foreach ($npm in $npmCandidates) {
    if (-not (Get-Command $npm -ErrorAction SilentlyContinue)) { continue }
    $raw = & $npm view '@deepseek-ai/dsh' version --fetch-timeout=8000 --fetch-retries=0 2>$null
    if ($LASTEXITCODE -eq 0 -and $raw) {
        $latest = ($raw | Select-Object -First 1).ToString().Trim()
        if ($latest -notmatch '^\d') { $latest = '' }
        if ($latest) { break }
    } else {
        $err = 'npm view 失败（可能是网络/镜像问题：' + $npm + '）'
    }
}
if (-not $latest -and -not $err) { $err = '查询失败：找不到可用的 npm' }

$upgrade = 'no'
if ($current -and $latest -and ($current -ne $latest)) { $upgrade = 'yes' }

$fields = @{
    current = $(if ($current) { $current } else { 'unknown' })
    latest  = $(if ($latest) { $latest } else { 'unknown' })
    upgrade = $(if ($err) { 'unknown' } else { $upgrade })
    checked = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
}
if ($err) { $fields['error'] = $err }
Write-Result $fields

if (-not $Quiet) {
    if ($err) {
        Write-Host ('[update] ' + $err)
    } elseif ($upgrade -eq 'yes') {
        Write-Host ('[update] 有新版本：当前 ' + $current + ' -> 最新 ' + $latest)
        Write-Host ''
        Write-Host '  升级方式（任选其一，升级后请重启服务）：'
        Write-Host '    npx --yes @deepseek-ai/dsh@latest --version      # 只刷新 npx 缓存，最省事'
        Write-Host '    npm install -g @deepseek-ai/dsh@latest           # 改为全局安装'
    } else {
        Write-Host ('[update] 已是最新：' + $current)
    }
}
exit 0
