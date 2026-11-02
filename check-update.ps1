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
    [switch]$Quiet,
    [switch]$WithTags
)
$ErrorActionPreference = 'SilentlyContinue'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$out = Join-Path $runDir 'update-check.txt'

function Write-Result([hashtable]$fields) {
    $lines = New-Object System.Collections.ArrayList
    foreach ($k in @('current', 'latest', 'next', 'alpha', 'upgrade', 'copies', 'checked', 'error')) {
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

# ---- 版本比较 ----
# 主.次.修订 数字优先；同号时预发布版（rc/beta）比正式版小，预发布之间按尾部数字比。
# 例：0.1.0-rc.7 < 0.2.1-rc.5（数字段）；0.2.1-rc.5 < 0.2.1（预发布更小）。
function ConvertTo-VersionKey([string]$v) {
    if (-not $v) { return $null }
    $main = $v
    $pre = ""
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
        $m = [regex]::Match($pre, "([0-9]+)$")
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

# ---- 列出所有已安装副本（npx 缓存每个目录一份 + 全局 npm + node 旁） ----
# 取「版本最高」的那份当 current：npx 缓存里可能同时存在 rc.6/rc.7，按 mtime 会选错。
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

$current = ''
$copies = @()
foreach ($c in (Get-DshCopies)) {
    $copies += $c.Version
    if (-not $current -or (Test-VersionNewer $c.Version $current)) { $current = $c.Version }
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

# ---- 可选：拿 dist-tags（latest / next / alpha）----
$nextTag = ''
$alphaTag = ''
if ($WithTags) {
    foreach ($npm in $npmCandidates) {
        if (-not (Get-Command $npm -ErrorAction SilentlyContinue)) { continue }
        $raw = & $npm view '@deepseek-ai/dsh' dist-tags --json --fetch-timeout=8000 --fetch-retries=0 2>$null
        if ($LASTEXITCODE -eq 0 -and $raw) {
            try {
                $tags = ($raw -join ' ') | ConvertFrom-Json
                if ($tags.latest) { $latest = $tags.latest }
                if ($tags.next) { $nextTag = $tags.next }
                if ($tags.alpha) { $alphaTag = $tags.alpha }
                break
            } catch { }
        }
    }
}

$upgrade = 'no'
if ($current -and $latest -and (Test-VersionNewer $latest $current)) { $upgrade = 'yes' }
elseif ($current -and $latest -and ($current -ne $latest)) { $upgrade = 'yes' }

$fields = @{
    current = $(if ($current) { $current } else { 'unknown' })
    latest  = $(if ($latest) { $latest } else { 'unknown' })
    upgrade = $(if ($err) { 'unknown' } else { $upgrade })
    checked = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
}
if ($nextTag) { $fields['next'] = $nextTag }
if ($alphaTag) { $fields['alpha'] = $alphaTag }
if ($copies.Count -gt 0) { $fields['copies'] = ($copies -join ',') }
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
