# update-plugins.ps1 - 更新 DSH 插件（dsh plugin --profile web update）
#
# 与 update-dsh.ps1 的分工：那个升 DSH 本体，这个升插件（两条独立的升级线）。
#   dsh plugin 子命令会把参数原样转发给 profile 目录里的 pnpm，所以本质是
#   「找一份已安装的 dsh → node <bin.js> plugin --profile web update」。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File update-plugins.ps1
#   ... -Profile tui     指定 profile（默认 web）
#   ... -DryRun          只打印命令
#   ... -Quiet
#
# 结果：run/update-plugins.txt（ok=yes|no + error），过程写 run/update-plugins.log
param(
    [string]$Profile = 'web',
    [switch]$DryRun,
    [switch]$Quiet
)
$ErrorActionPreference = 'Continue'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$resultFile = Join-Path $runDir 'update-plugins.txt'
$logFile = Join-Path $runDir 'update-plugins.log'

function Log([string]$msg) {
    $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $msg
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
    if (-not $Quiet) { Write-Host ('[plugins] ' + $msg) }
}

# 找一份 dsh 入口（与启动器一致的顺序：npx 缓存 -> 全局 npm -> node 旁）
function Find-DshBinJs {
    $cands = New-Object System.Collections.ArrayList
    $npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    if (Test-Path -LiteralPath $npxRoot) {
        foreach ($d in (Get-ChildItem -LiteralPath $npxRoot -Directory -ErrorAction SilentlyContinue)) {
            [void]$cands.Add((Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js'))
        }
    }
    [void]$cands.Add((Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\lib\bin.js'))
    $nodeExe = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    if ($nodeExe) { [void]$cands.Add((Join-Path (Split-Path -Parent $nodeExe) 'node_modules\@deepseek-ai\dsh\lib\bin.js')) }
    $best = $null
    $bestTime = [datetime]::MinValue
    foreach ($c in $cands) {
        if (-not $c -or -not (Test-Path -LiteralPath $c)) { continue }
        $t = (Get-Item -LiteralPath $c).LastWriteTimeUtc
        if ((-not $best) -or ($t -gt $bestTime)) { $best = $c; $bestTime = $t }
    }
    return $best
}

$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
$binJs = Find-DshBinJs
if (-not (Test-Path -LiteralPath $node) -or -not $binJs) {
    Set-Content -LiteralPath $resultFile -Value ('ok=no' + [string][char]13 + [string][char]10 + 'error=缺少 node.exe 或 dsh 入口') -Encoding UTF8
    Log 'FAIL: node.exe or dsh bin.js not found'
    exit 1
}

$args = @($binJs, 'plugin', '--profile', $Profile, 'update')
if ($DryRun) {
    Log ('DRY RUN: ' + $node + ' ' + ($args -join ' '))
    exit 0
}

Log ('running: ' + $node + ' ' + ($args -join ' '))
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$out = & $node @args 2>&1
$code = $LASTEXITCODE
$sw.Stop()
$text = (($out | ForEach-Object { $_.ToString() }) -join [string][char]10)
Add-Content -LiteralPath $logFile -Value $text -Encoding UTF8
Log ('exit=' + $code + '  elapsed=' + [int]$sw.Elapsed.TotalSeconds + 's')

$lines = New-Object System.Collections.ArrayList
if ($code -eq 0) {
    [void]$lines.Add('ok=yes')
} else {
    [void]$lines.Add('ok=no')
    [void]$lines.Add('error=dsh plugin update failed (exit ' + $code + ')')
}
[void]$lines.Add('checked=' + (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK'))
Set-Content -LiteralPath $resultFile -Value ($lines -join ([string][char]13 + [string][char]10)) -Encoding UTF8
if ($code -eq 0) { Log 'OK: plugins updated (restart the service to load them)'; exit 0 } else { Log 'FAIL'; exit 1 }
