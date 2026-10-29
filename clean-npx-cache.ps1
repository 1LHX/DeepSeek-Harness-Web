# clean-npx-cache.ps1 - 清理 npx 缓存里不再使用的历史目录（G：环境清理）
#
# 背景：npx 每次下载的包会留在 %LOCALAPPDATA%\npm-cache\_npx\<hash>\ 下，从不自动删除。
# 本机这份缓存已经到了「7.5 万文件 / 547 MB」，其中大部分是历史目录。文件越多，冷启动时
# 杀软与文件系统的扫描面越大，也白白吃磁盘。
#
# 默认只做「演练」：列出可删除的目录与可回收空间，不动任何文件。
#   powershell -NoProfile -ExecutionPolicy Bypass -File clean-npx-cache.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File clean-npx-cache.ps1 -Apply
#   ... -Days 60                    调整保守阈值（默认只清理 30 天没动过的目录）
#   ... -IncludeOldDsh -Apply       连历史 dsh 版本目录一起清（只保留当前在用的那份）
#
# 安全策略（宁可少删）：
#   1. 当前在用的 dsh 目录（含 @deepseek-ai/dsh 且 bin.js 最新）一律保留；
#   2. 其它含 @deepseek-ai/dsh 的历史目录默认也保留，只有 -IncludeOldDsh 才参与清理；
#   3. 最近 -Days 天内被写过的目录一律保留（可能正被别的工具/MCP 服务使用）；
#   4. -Apply 时逐个删除，单个失败不影响其它，失败原因会打印出来。
param(
    [switch]$Apply,
    [int]$Days = 30,
    [switch]$IncludeOldDsh
)
$ErrorActionPreference = 'Continue'

$npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
if (-not (Test-Path -LiteralPath $npxRoot)) {
    Write-Host "npx 缓存目录不存在：$npxRoot"
    exit 0
}

$cutoff = (Get-Date).AddDays(-$Days)
$all = @(Get-ChildItem -LiteralPath $npxRoot -Directory -ErrorAction SilentlyContinue)
Write-Host ("=== npx 缓存：{0}" -f $npxRoot)
Write-Host ("    共 {0} 个目录；保留 {1} 天内使用过的、以及当前在用的 dsh 目录" -f $all.Count, $Days)
if ($IncludeOldDsh) { Write-Host "    -IncludeOldDsh：历史 dsh 目录也参与清理（只保留当前在用的那份）" }
Write-Host ""

# 当前在用的 dsh 目录 = 含 @deepseek-ai/dsh 且 bin.js 最新的那个（与启动脚本的选取规则一致）
$dshDirs = @($all | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'node_modules\@deepseek-ai\dsh') })
$activeDsh = $null
$activeTime = [datetime]::MinValue
foreach ($d in $dshDirs) {
    $bin = Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    if (Test-Path -LiteralPath $bin) { $t = (Get-Item -LiteralPath $bin).LastWriteTimeUtc }
    else { $t = $d.LastWriteTimeUtc }
    if ((-not $activeDsh) -or ($t -gt $activeTime)) { $activeDsh = $d.FullName; $activeTime = $t }
}

$keep = New-Object System.Collections.ArrayList
$drop = New-Object System.Collections.ArrayList
$reclaim = 0L

foreach ($d in $all) {
    $files = Get-ChildItem -LiteralPath $d.FullName -Recurse -File -Force -ErrorAction SilentlyContinue
    $size = 0L
    foreach ($f in $files) { $size += $f.Length }
    $hasDsh = Test-Path -LiteralPath (Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh')
    $recent = $d.LastWriteTime -gt $cutoff
    $isActive = ($null -ne $activeDsh) -and ($d.FullName -eq $activeDsh)

    if ($isActive) {
        [void]$keep.Add([pscustomobject] @{ Name = $d.Name; Size = $size; Why = '在用（当前 dsh）' })
    } elseif ($hasDsh -and -not $IncludeOldDsh) {
        [void]$keep.Add([pscustomobject] @{ Name = $d.Name; Size = $size; Why = '历史 dsh（默认保留；-IncludeOldDsh 才清理）' })
    } elseif ($recent) {
        [void]$keep.Add([pscustomobject] @{ Name = $d.Name; Size = $size; Why = ('{0} 天内写过' -f $Days) })
    } else {
        [void]$drop.Add([pscustomobject] @{ Name = $d.Name; Path = $d.FullName; Size = $size; Last = $d.LastWriteTime })
        $reclaim += $size
    }
}

Write-Host "--- 保留 ---"
foreach ($k in $keep) { Write-Host ("  {0,-18} {1,9:N1} MB  {2}" -f $k.Name, ($k.Size / 1MB), $k.Why) }
Write-Host ""
Write-Host "--- 可清理 ---"
if ($drop.Count -eq 0) { Write-Host "  （没有符合条件的目录）" }
foreach ($x in $drop) {
    Write-Host ("  {0,-18} {1,9:N1} MB  最后使用 {2:yyyy-MM-dd}" -f $x.Name, ($x.Size / 1MB), $x.Last)
}
Write-Host ""
Write-Host ("可回收约 {0:N1} MB" -f ($reclaim / 1MB))

if (-not $Apply) {
    Write-Host ""
    Write-Host "以上为演练结果，未删除任何文件。确认无误后加 -Apply 执行："
    Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File clean-npx-cache.ps1 -Apply"
    exit 0
}

Write-Host ""
Write-Host "=== 开始删除 ==="
$freed = 0L
foreach ($x in $drop) {
    try {
        Remove-Item -LiteralPath $x.Path -Recurse -Force -ErrorAction Stop
        $freed += $x.Size
        Write-Host ("  [ok] {0}" -f $x.Name)
    } catch {
        Write-Host ("  [fail] {0} -> {1}" -f $x.Name, $_.Exception.Message)
    }
}
Write-Host ""
Write-Host ("完成：回收约 {0:N1} MB" -f ($freed / 1MB))
Write-Host "提示：已删除的包在下次 npx 用到时会重新下载；dsh 本身不受影响（在用目录已保留）。"
