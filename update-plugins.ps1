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

$CRLF = [string][char]13 + [string][char]10

# ---- 版本比较（与 update-dsh.ps1 / check-update.ps1 同一套规则） ----
function ConvertTo-VersionKey([string]$v) {
    if (-not $v) { return $null }
    $main = $v
    $pre = ''
    $dash = $v.IndexOf([char]45)
    if ($dash -ge 0) { $main = $v.Substring(0, $dash); $pre = $v.Substring($dash + 1) }
    $parts = $main.Split([char]46)
    $nums = @(0, 0, 0)
    for ($i = 0; $i -lt 3 -and $i -lt $parts.Count; $i++) {
        $tmp = 0
        if ([int]::TryParse($parts[$i], [ref]$tmp)) { $nums[$i] = $tmp }
    }
    $preNum = 0
    if ($pre) {
        $m = [regex]::Match($pre, '([0-9]+)$')
        if ($m.Success) { $tmp2 = 0; if ([int]::TryParse($m.Groups[1].Value, [ref]$tmp2)) { $preNum = $tmp2 } }
    }
    return [pscustomobject]@{ Major = $nums[0]; Minor = $nums[1]; Patch = $nums[2]; Pre = $(if ($pre) { 1 } else { 0 }); PreNum = $preNum; Raw = $v }
}

function Test-VersionNewer([string]$a, [string]$b) {
    $ka = ConvertTo-VersionKey $a
    $kb = ConvertTo-VersionKey $b
    if (-not $ka) { return $false }
    if (-not $kb) { return $true }
    if ($ka.Major -ne $kb.Major) { return ($ka.Major -gt $kb.Major) }
    if ($ka.Minor -ne $kb.Minor) { return ($ka.Minor -gt $kb.Minor) }
    if ($ka.Patch -ne $kb.Patch) { return ($ka.Patch -gt $kb.Patch) }
    if ($ka.Pre -ne $kb.Pre) { return ($ka.Pre -lt $kb.Pre) }
    return ($ka.PreNum -gt $kb.PreNum)
}

# v3.9.1：入口改按「版本号最高」选，与启动器 / 面板 / update-dsh.ps1 一致。
# 此前按 mtime：旧缓存被 touch 一下就可能在 0.2.x 迁移过 profile 之后
# 用 0.1.x 的 dsh 去跑 plugin 命令，那是跨大版本改 profile。
function Find-DshBinJs {
    $cands = New-Object System.Collections.ArrayList
    $npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    if (Test-Path -LiteralPath $npxRoot) {
        foreach ($d in (Get-ChildItem -LiteralPath $npxRoot -Directory -ErrorAction SilentlyContinue)) {
            [void]$cands.Add((Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\package.json'))
        }
    }
    [void]$cands.Add((Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\package.json'))
    $nodeExe = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    if ($nodeExe) { [void]$cands.Add((Join-Path (Split-Path -Parent $nodeExe) 'node_modules\@deepseek-ai\dsh\package.json')) }
    $best = $null
    $bestVer = ''
    foreach ($c in $cands) {
        if (-not $c -or -not (Test-Path -LiteralPath $c)) { continue }
        $bin = Join-Path (Split-Path -Parent $c) 'lib\bin.js'
        if (-not (Test-Path -LiteralPath $bin)) { continue }
        try {
            $v = (Get-Content -LiteralPath $c -Raw | ConvertFrom-Json).version
            if (-not $v) { continue }
            if ((-not $best) -or (Test-VersionNewer $v $bestVer)) { $best = $bin; $bestVer = $v }
        } catch { }
    }
    if ($best) { Log ('dsh entry: ' + $best + ' (v' + $bestVer + ')') }
    return $best
}

# 结果文件统一出口：只写非空字段，checked 自动补
function Write-Result([hashtable]$fields) {
    $lines = New-Object System.Collections.ArrayList
    foreach ($k in @('ok', 'note', 'error', 'detail')) {
        if ($fields.ContainsKey($k)) {
            $v = [string]$fields[$k]
            if ($v) { [void]$lines.Add($k + '=' + $v) }
        }
    }
    [void]$lines.Add('checked=' + (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK'))
    Set-Content -LiteralPath $resultFile -Value ($lines -join $CRLF) -Encoding UTF8
}

$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
$binJs = Find-DshBinJs
if (-not (Test-Path -LiteralPath $node) -or -not $binJs) {
    Write-Result @{ ok = 'no'; error = '缺少 node.exe 或 dsh 入口' }
    Log 'FAIL: node.exe or dsh bin.js not found'
    exit 1
}

# v3.9.1：先看 profile 有没有树外插件。DSH 本体的组合包（dsh-base / dsh-web-app）
# 由 launcher 自带、不写进 profile 的 dependencies，所以为空是常态——此时
# pnpm update 只会打印 Already up to date 并 exit 0，面板就会误报「插件已更新」。
$profilePkg = Join-Path (Join-Path $env:USERPROFILE '.dsh\profiles') (Join-Path $Profile 'package.json')
$depCount = -1
if (Test-Path -LiteralPath $profilePkg) {
    try {
        $pj = Get-Content -LiteralPath $profilePkg -Raw | ConvertFrom-Json
        if ($pj.dependencies) { $depCount = @($pj.dependencies.PSObject.Properties).Count } else { $depCount = 0 }
    } catch { $depCount = -1 }
}
if ($depCount -eq 0) {
    Log ('skip: profile ' + $Profile + ' has no out-of-tree plugins (dependencies is empty)')
    Write-Result @{ ok = 'yes'; note = 'no-plugins'; detail = 'profile ' + $Profile + ' dependencies is empty' }
    exit 0
}

# v3.9.1：pnpm 预检。dsh 那条「pnpm was not found」提示挂在 exitCode 127 上，
# 而 Windows 的 cmd 找不到命令返回 1，所以不预检就只能拿到一句 exit 1。
$pnpmCmd = Get-Command pnpm -ErrorAction SilentlyContinue
if (-not $pnpmCmd) {
    Write-Result @{ ok = 'no'; error = '未找到 pnpm：请在 PowerShell 里运行 npm i -g pnpm（或 corepack enable pnpm）后重试' }
    Log 'FAIL: pnpm not found on PATH'
    exit 1
}
Log ('pnpm: ' + $pnpmCmd.Source)

$dshArgs = @($binJs, 'plugin', '--profile', $Profile, 'update')
if ($DryRun) {
    Log ('DRY RUN: ' + $node + ' ' + ($dshArgs -join ' '))
    exit 0
}

# v3.9.1：给 dsh plugin 一个硬超时（面板侧 900s，这里 600s），分开收 stdout/stderr。
# 用 .NET Process 而不是 Start-Process -PassThru：实测 PS 5.1 里后者的 ExitCode
# 取回来是空值，导致一次成功的更新也被判成失败（exit= 空、ok=no）。
$timeoutMs = 600000
$timedOut = $false
$code = $null
$stdout = ''
$stderr = ''

Log ('running: ' + $node + ' ' + ($dshArgs -join ' '))
$sw = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $node
    $psi.Arguments = (($dshArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    $psi.WorkingDirectory = $dir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    if (-not $proc.WaitForExit($timeoutMs)) {
        $timedOut = $true
        try { $proc.Kill() } catch { }
    }
    $proc.WaitForExit()
    $code = $proc.ExitCode
    $stdout = $outTask.Result
    $stderr = $errTask.Result
} catch {
    Log ('spawn failed: ' + $_.Exception.Message)
}
$sw.Stop()

$text = ([string]$stdout + [string]$stderr).Trim()
if ($text) { Add-Content -LiteralPath $logFile -Value $text -Encoding UTF8 }
Log ('exit=' + $code + '  elapsed=' + [int]$sw.Elapsed.TotalSeconds + 's' + $(if ($timedOut) { '  TIMEOUT' } else { '' }))

# 诊断行：只挑「像失败」的（pnpm 成功时也会往 stderr 打 deprecation 之类噪音）；
# dsh 的 warning 与 allow-version 提示带 dsh: 前缀，必须保留下来给面板看。
$detail = ''
foreach ($line in (([string]$stderr + "`n" + [string]$stdout) -split "`r?`n")) {
    $t = $line.Trim()
    if (-not $t) { continue }
    if ($t -match '^\(node:') { continue }
    if ($t -match '(?i)dsh:|ERR_|error|fail|not found|refus|incompat|denied') { $detail = $t; break }
}

if ($timedOut) {
    Write-Result @{ ok = 'no'; error = ('dsh plugin update 超过 ' + [int]($timeoutMs / 1000) + ' 秒仍未结束，已终止'); detail = $detail }
    Log 'FAIL: timeout'
    exit 1
}
if ($code -eq 0) {
    Write-Result @{ ok = 'yes'; detail = $detail }
    Log 'OK: plugins updated (restart the service to load them)'
    exit 0
}
if (-not $detail -and $text) { $detail = (($text -split "`r?`n")[0]).Trim() }
Write-Result @{ ok = 'no'; error = ('dsh plugin update failed (exit ' + $code + ')'); detail = $detail }
Log 'FAIL'
exit 1
