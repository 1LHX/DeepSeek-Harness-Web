# update-dsh.ps1 - 检查 / 升级 DeepSeek Harness（@deepseek-ai/dsh）
#
# 与 check-update.ps1 的分工：
#   check-update.ps1  只查（启动时节流 24h，面板据此亮红点）
#   update-dsh.ps1    查 + 真升级（只有用户在设置页点「更新到最新版」才会跑）
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File update-dsh.ps1              # 只查
#   ... -Apply            真正升级（先备份会话，再用 npx 预热最新版）
#   ... -DryRun           只打印将要执行的命令，不动任何东西
#   ... -Channel alpha    升级到 alpha 通道（默认 latest；可选 next）
#   ... -NoBackup         跳过 ~/.dsh/sessions 备份
#   ... -Quiet            不打印，只写结果文件
#
# 升级路径的取舍（对应文章里的三种安装方式）：
#   * 本机是 **npx 模式**（没有全局 dsh），所以用「npx 预热 @latest」这条：
#     npx --yes @deepseek-ai/dsh@latest --version
#     注意：只写 npx @deepseek-ai/dsh 是不够的——npx 会复用缓存里已满足的版本，
#     必须显式带 @latest / @<version> 才会拉新版（这是文章里"重跑就是升级"不准确的地方）。
#   * 全局安装过（%APPDATA%\npm）的机器：这条命令同样能工作（在缓存里落一份新版本），
#     启动器按「版本最高」选入口，所以新版本会被自动用上。
#   * 源码构建（git clone + pnpm）不走这个脚本：见 README。
#
# 结果：run/update-check.txt（current/latest/upgrade/checked/applied/error），
#       过程写 run/update-apply.log。脚本自身**不重启服务**——重启由面板做。
param(
    [switch]$Apply,
    [switch]$DryRun,
    [string]$Channel = 'latest',
    [switch]$NoBackup,
    [switch]$Quiet
)
$ErrorActionPreference = 'Continue'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$resultFile = Join-Path $runDir 'update-check.txt'
$logFile = Join-Path $runDir 'update-apply.log'
$CRLF = [string][char]13 + [string][char]10

function Log([string]$msg) {
    $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $msg
    Add-Content -LiteralPath $logFile -Value $line -Encoding UTF8
    if (-not $Quiet) { Write-Host ('[update] ' + $msg) }
}

function Write-Result([hashtable]$fields) {
    $lines = New-Object System.Collections.ArrayList
    foreach ($k in @('current', 'latest', 'next', 'alpha', 'upgrade', 'copies', 'checked', 'applied', 'error')) {
        if ($fields.ContainsKey($k)) { [void]$lines.Add($k + '=' + $fields[$k]) }
    }
    Set-Content -LiteralPath $resultFile -Value ($lines -join $CRLF) -Encoding UTF8
}

# ---- 版本比较（与 check-update.ps1 同一套规则） ----
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

function Get-DshCopies {
    $paths = New-Object System.Collections.ArrayList
    $npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    if (Test-Path -LiteralPath $npxRoot) {
        foreach ($d in (Get-ChildItem -LiteralPath $npxRoot -Directory -ErrorAction SilentlyContinue)) {
            [void]$paths.Add((Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\package.json'))
        }
    }
    [void]$paths.Add((Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\package.json'))
    $nodeExe = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    if ($nodeExe) {
        [void]$paths.Add((Join-Path (Split-Path -Parent $nodeExe) 'node_modules\@deepseek-ai\dsh\package.json'))
    }
    $list = New-Object System.Collections.ArrayList
    foreach ($p in $paths) {
        if (-not $p -or -not (Test-Path -LiteralPath $p)) { continue }
        try {
            $v = (Get-Content -LiteralPath $p -Raw | ConvertFrom-Json).version
            if ($v) { [void]$list.Add([pscustomobject]@{ Path = $p; Version = $v }) }
        } catch { }
    }
    return ,$list
}

function Get-CurrentVersion {
    $best = ''
    foreach ($c in (Get-DshCopies)) {
        if (-not $best -or (Test-VersionNewer $c.Version $best)) { $best = $c.Version }
    }
    return $best
}

function Find-Npm() {
    $nodeExe = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    $cands = New-Object System.Collections.ArrayList
    if ($nodeExe) { [void]$cands.Add((Join-Path (Split-Path -Parent $nodeExe) 'npm.cmd')) }
    [void]$cands.Add('npm.cmd')
    foreach ($c in $cands) { if ($c -and (Get-Command $c -ErrorAction SilentlyContinue)) { return $c } }
    return $null
}

function Find-Npx() {
    $nodeExe = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    $cands = New-Object System.Collections.ArrayList
    if ($nodeExe) {
        [void]$cands.Add((Join-Path (Split-Path -Parent $nodeExe) 'npx.cmd'))
        [void]$cands.Add((Join-Path (Split-Path -Parent $nodeExe) 'node_modules\npm\bin\npx-cli.js'))
    }
    [void]$cands.Add('npx.cmd')
    foreach ($c in $cands) {
        if (-not $c) { continue }
        if ($c.EndsWith('.js')) {
            if (Test-Path -LiteralPath $c) { return @{ Kind = 'js'; Path = $c } }
        } elseif (Get-Command $c -ErrorAction SilentlyContinue) {
            return @{ Kind = 'cmd'; Path = $c }
        }
    }
    return $null
}

$current = Get-CurrentVersion
$copies = @()
foreach ($c in (Get-DshCopies)) { $copies += $c.Version }
Log ('installed copies: ' + $(if ($copies.Count) { $copies -join ', ' } else { '(none)' }) + '  -> current=' + $(if ($current) { $current } else { 'unknown' }))

# ---- 查目标版本 ----
$target = ''
$err = ''
$npm = Find-Npm
if (-not $npm) {
    $err = '找不到 npm.cmd，无法查询/升级（请确认 Node.js 安装完好）'
} else {
    $raw = & $npm view ('@deepseek-ai/dsh@' + $Channel) version --fetch-timeout=15000 --fetch-retries=1 2>$null
    if ($LASTEXITCODE -eq 0 -and $raw) {
        $target = ($raw | Select-Object -First 1).ToString().Trim()
        if ($target -notmatch '^\d') { $target = '' }
    }
    if (-not $target) { $err = '查不到 ' + $Channel + ' 通道的版本（可能是网络/镜像问题）' }
}
if (-not $target) {
    Write-Result @{ current = $(if ($current) { $current } else { 'unknown' }); latest = 'unknown'; upgrade = 'unknown'; copies = ($copies -join ','); checked = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK'); error = $err }
    Log ('FAIL: ' + $err)
    exit 1
}

$needUpgrade = (-not $current) -or (Test-VersionNewer $target $current) -or ($target -ne $current)
Log ('current=' + $(if ($current) { $current } else { 'unknown' }) + '  target(' + $Channel + ')=' + $target + '  needUpgrade=' + $needUpgrade)

$npx = Find-Npx
$pkgSpec = '@deepseek-ai/dsh@' + $target
$npxArgs = @('--yes', $pkgSpec, '--version')

if ($DryRun) {
    Log ('DRY RUN: would run -> ' + $(if ($npx) { $npx.Path + ' ' + ($npxArgs -join ' ') } else { '(npx not found!)' }))
    if (-not $NoBackup) { Log ('DRY RUN: would back up ' + (Join-Path $env:USERPROFILE '.dsh\sessions')) }
    exit 0
}

if (-not $needUpgrade) {
    Write-Result @{ current = $current; latest = $target; upgrade = 'no'; copies = ($copies -join ','); checked = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK'); applied = 'no' }
    Log 'already up to date'
    exit 0
}

if (-not $Apply) {
    Write-Result @{ current = $(if ($current) { $current } else { 'unknown' }); latest = $target; upgrade = 'yes'; copies = ($copies -join ','); checked = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK') }
    Log 'upgrade available (use -Apply to perform it)'
    exit 0
}

# ---- 升级：先备份会话，再用 npx 预热目标版本 ----
if (-not $npx) {
    Write-Result @{ current = $current; latest = $target; upgrade = 'yes'; copies = ($copies -join ','); checked = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK'); error = '找不到 npx' }
    Log 'FAIL: npx not found'
    exit 1
}

$backupPath = ''
if (-not $NoBackup) {
    $src = Join-Path $env:USERPROFILE '.dsh\sessions'
    if (Test-Path -LiteralPath $src) {
        $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
        $backupPath = Join-Path $env:USERPROFILE ('.dsh-sessions-backup-' + $stamp)
        try {
            Copy-Item -LiteralPath $src -Destination $backupPath -Recurse -Force -ErrorAction Stop
            Log ('sessions backed up -> ' + $backupPath)
        } catch {
            Log ('WARN: session backup failed (continuing): ' + $_.Exception.Message)
            $backupPath = ''
        }
    } else {
        Log 'WARN: %USERPROFILE%\.dsh\sessions not found, skip backup'
    }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
if ($npx.Kind -eq 'js') {
    $out = & node $npx.Path @npxArgs 2>&1
} else {
    $out = & $npx.Path @npxArgs 2>&1
}
$code = $LASTEXITCODE
$sw.Stop()
$outText = (($out | ForEach-Object { $_.ToString() }) -join ' ').Trim()
$outHead = $outText.Substring(0, [Math]::Min(200, $outText.Length))
Log ('npx exit=' + $code + '  elapsed=' + [int]$sw.Elapsed.TotalSeconds + 's  output=' + $outHead)

$after = Get-CurrentVersion
$ok = $false
if ($after -and -not (Test-VersionNewer $target $after)) { $ok = $true }
$upgradeFlag = 'no'
if (-not $ok) { $upgradeFlag = 'yes' }
$fields = @{
    current = $(if ($after) { $after } else { 'unknown' })
    latest  = $target
    upgrade = $upgradeFlag
    copies  = (((Get-DshCopies) | ForEach-Object { $_.Version }) -join ',')
    checked = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
    applied = $(if ($ok) { 'yes' } else { 'no' })
}
if (-not $ok) {
    if ($code -ne 0) { $fields['error'] = 'npx install failed (exit ' + $code + ')' }
    else { $fields['error'] = 'target version not detected after install' }
}
Write-Result $fields

if ($ok) {
    Log ('OK: upgraded to ' + $after + ' (restart the service to take effect)')
    exit 0
} else {
    Log ('FAIL: upgrade did not take effect, current=' + $(if ($after) { $after } else { 'unknown' }))
    exit 1
}
