# defender-exclusions.ps1 - 管理 Microsoft Defender 的排除路径（只针对 npx 缓存这一条）
#
# 为什么是这一条：
#   dsh 的整棵依赖树都在 %LOCALAPPDATA%\npm-cache\_npx\<hash>\node_modules 下（本机 3.2 万个
#   文件 / 210 MB）。开机后第一次启动要逐个打开它们，实时保护对每次 open 都要过一遍过滤驱动：
#   实测冷启动 46.2 秒、热启动 1.4 秒，差距主要在这里。
#
# 为什么只排除这一条（收窄后的取舍）：
#   * 曾在清单里、但已移除的三条：C:\Program Files\nodejs（慢的不是 node.exe，收益≈0）、
#     ~/.dsh（插件树已指向 _npx，剩下是小 JSON，收益小）、dsh-web 目录（500 个文件，且是
#     会被面板执行的脚本目录，性价比最差）。
#   * 剩下这条的代价要说清：恶意 npm 包（抢注/投毒/postinstall）正是先落在这里，排除后
#     它被写入、被执行都不再被扫描。对冲手段是这个目录本身是可丢弃的缓存——怀疑有问题就
#     整个删掉，下次由 npm 重新下载（npm cache verify 也能校验完整性）。
#   * 不改 ACL、不改网络、不关实时保护；SmartScreen / 防火墙 / ASR / 漏洞利用防护不受影响。
#   * 随时可回滚：remove-defender-exclusions.cmd（或 -Remove）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File defender-exclusions.ps1 -Status
#   powershell -NoProfile -ExecutionPolicy Bypass -File defender-exclusions.ps1 -Add     [-Wait]
#   powershell -NoProfile -ExecutionPolicy Bypass -File defender-exclusions.ps1 -Remove  [-Wait]
#   ... -DryRun   只打印将要执行的命令，不做任何修改（非管理员也能看）
#
# 结果会追加写入 run/defender-exclusions.log（提权后的窗口可能一闪而过，看这个文件最稳）。
param(
    [switch]$Add,
    [switch]$Remove,
    [switch]$Status,
    [switch]$DryRun,
    # 提权调用是否等待子进程结束（便于父进程随后读取结果日志）
    [switch]$Wait
)
$ErrorActionPreference = 'Continue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$logFile = Join-Path $runDir 'defender-exclusions.log'

# 唯一目标：npx 缓存根目录（覆盖其中所有 <hash> 目录；将来换版本重新下载也仍在范围内）
function Get-TargetPaths {
    $npx = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    $parent = Join-Path $env:LOCALAPPDATA 'npm-cache'
    # _npx 还不存在时退回上一级，避免加一条指向不存在目录的排除项
    if (Test-Path -LiteralPath $npx) { return @($npx) }
    if (Test-Path -LiteralPath $parent) { return @($parent) }
    return @()
}

function Write-Log([string]$text) {
    try { Add-Content -LiteralPath $logFile -Value ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $text) -Encoding UTF8 } catch { }
}

$isAdmin = $false
try {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} catch { }

$targets = Get-TargetPaths

# ---------- 只读查询 ----------
if ($Status) {
    Write-Host "=== 当前 Defender 排除路径 ==="
    if (-not $isAdmin) {
        Write-Host "  （需要管理员权限才能读取；可查看 run\defender-exclusions.log 或 Windows 安全中心）"
    } else {
        try {
            $cur = (Get-MpPreference).ExclusionPath
            if ($cur) { $cur | ForEach-Object { Write-Host "  $_" } } else { Write-Host "  （无）" }
        } catch { Write-Host "  读取失败：$($_.Exception.Message)" }
    }
    Write-Host ""
    Write-Host "=== 建议排除的路径（只有这一条） ==="
    foreach ($p in $targets) { Write-Host "  $p" }
    try {
        $mp = Get-MpComputerStatus
        Write-Host ""
        Write-Host ("实时保护: {0}" -f $mp.RealTimeProtectionEnabled)
    } catch { }
    exit 0
}

if (-not ($Add -or $Remove)) {
    Write-Host "用法: defender-exclusions.ps1 -Add [-Wait] | -Remove [-Wait] | -Status | -DryRun"
    exit 1
}

$verb = if ($Remove) { 'Remove' } else { 'Add' }

# ---------- 演练（不需要管理员） ----------
if ($DryRun) {
    Write-Host ("=== Defender 排除路径：{0}（DryRun，未修改）===" -f $verb)
    if ($targets.Count -eq 0) {
        Write-Host '  （找不到 _npx / npm-cache 目录，无事可做）'
    }
    foreach ($p in $targets) {
        Write-Host ('  [dry-run] {0}-MpPreference -ExclusionPath "{1}"' -f $verb, $p)
    }
    Write-Host ""
    Write-Host "说明：只排除这一条（npx 缓存）。不改 ACL、不关实时保护，随时可 -Remove 回滚。"
    exit 0
}

# ---------- 未提权：以管理员身份重新执行自身 ----------
if (-not $isAdmin) {
    Write-Host "需要管理员权限，正在请求提权（UAC 窗口会弹出来，请点「是」）..."
    Write-Log ("requesting elevation for -" + $verb)
    try {
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
        if ($Add) { $argList += '-Add' }
        if ($Remove) { $argList += '-Remove' }
        $sp = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList -PassThru
        if ($Wait) {
            [void]$sp.WaitForExit(90000)
            Write-Host ""
            Write-Host "=== 结果（提权进程已退出；以下摘自 run\defender-exclusions.log）==="
            if (Test-Path -LiteralPath $logFile) { Get-Content -LiteralPath $logFile -Tail 12 }
        } else {
            Write-Host "已提交提权请求。结果写入：$logFile"
        }
    } catch {
        Write-Host "提权失败或被取消：$($_.Exception.Message)"
        Write-Log ("elevation failed: " + $_.Exception.Message)
        exit 1
    }
    exit 0
}

# ---------- 已提权：执行修改 ----------
Write-Host ("=== Defender 排除路径：{0} ===" -f $verb)
Write-Log ("apply -" + $verb + " as admin; targets=" + ($targets -join '; '))

if ($targets.Count -eq 0) {
    Write-Host "  找不到 _npx / npm-cache 目录，未做任何修改。"
    Write-Log "aborted: no target directory"
    exit 1
}

foreach ($p in $targets) {
    try {
        if ($Remove) { Remove-MpPreference -ExclusionPath $p -ErrorAction Stop }
        else         { Add-MpPreference    -ExclusionPath $p -ErrorAction Stop }
        Write-Host ("  [ok] " + $p)
        Write-Log ("  [ok] " + $verb + ' ' + $p)
    } catch {
        Write-Host ("  [fail] " + $p + " -> " + $_.Exception.Message)
        Write-Log ("  [fail] " + $verb + ' ' + $p + ' -> ' + $_.Exception.Message)
    }
}

Write-Host ""
Write-Host "=== 生效后的排除路径 ==="
try {
    $cur = (Get-MpPreference).ExclusionPath
    if ($cur) {
        $cur | ForEach-Object { Write-Host "  $_"; Write-Log ("  now: " + $_) }
    } else {
        Write-Host "  （无）"; Write-Log "  now: (none)"
    }
} catch {
    Write-Host "  重新读取失败：$($_.Exception.Message)"
    Write-Log ("  readback failed: " + $_.Exception.Message)
}
Write-Host ""
Write-Host "下一步：重启一次，然后对比 run\boot-timing.log 里的 PORT READY（冷启动基准 46.2s）。"
Write-Log "done"
exit 0
