# check-dsh.ps1 - check whether @deepseek-ai/dsh is available
# Called by dsh-panel.exe (hidden). Writes "installed" or "missing" to run/dsh-check.txt.
#
# v2: file system first. The v1 script asked npm twice
# (`npm config get cache`, `npm root -g`), and every `npm` is a full node+npm boot:
# ~0.35 s each when warm, seconds on a cold boot. The panel needs a yes/no answer, not
# npm's opinion, so the common layouts are probed directly and npm is only consulted
# when nothing was found on disk.
#
# Read-only: only writes the result file under run/.
$ErrorActionPreference = 'SilentlyContinue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$out = Join-Path $runDir 'dsh-check.txt'
Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
$found = $false

# ---- 1) file system probes (no process is started) ----
function Test-DshEntry([string]$p) {
    return [bool]($p -and (Test-Path -LiteralPath $p))
}

# 1a) npx cache: %LOCALAPPDATA%\npm-cache\_npx\<hash>\node_modules\@deepseek-ai\dsh
$npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
if (Test-Path -LiteralPath $npxRoot) {
    foreach ($d in (Get-ChildItem -LiteralPath $npxRoot -Directory -ErrorAction SilentlyContinue)) {
        if (Test-DshEntry (Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js')) { $found = $true; break }
        if (Test-DshEntry (Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\package.json')) { $found = $true; break }
    }
}
# 1b) global install: %APPDATA%\npm\node_modules\@deepseek-ai\dsh
if (-not $found) {
    if (Test-DshEntry (Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\lib\bin.js')) { $found = $true }
}
# 1c) inside the node installation itself
if (-not $found) {
    $node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
    if (Test-Path -LiteralPath $node) {
        if (Test-DshEntry (Join-Path (Split-Path -Parent $node) 'node_modules\@deepseek-ai\dsh\lib\bin.js')) { $found = $true }
    }
}

# ---- 2) npm fallbacks (only when the file system found nothing) ----
if (-not $found) {
    $node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
    if (-not $node) {
        foreach ($root in 'HKLM:\SOFTWARE\Node.js', 'HKCU:\SOFTWARE\Node.js', 'HKLM:\SOFTWARE\WOW6432Node\Node.js') {
            try {
                $reg = Get-ItemProperty -LiteralPath $root -ErrorAction Stop
                if ($reg.InstallPath) {
                    $cand = Join-Path $reg.InstallPath 'node.exe'
                    if (Test-Path -LiteralPath $cand) { $node = $cand; break }
                }
            } catch { }
        }
    }
    if ($node) {
        $npm = Join-Path (Split-Path -Parent $node) 'npm.cmd'
        if (Test-Path -LiteralPath $npm) {
            # npm's configured cache (may be relocated away from %LOCALAPPDATA%)
            $cache = (& $npm config get cache 2>$null | Select-Object -First 1)
            if ($cache -and (Test-Path -LiteralPath $cache)) {
                $npxDir = Join-Path $cache '_npx'
                if (Test-Path -LiteralPath $npxDir) {
                    foreach ($d in (Get-ChildItem -LiteralPath $npxDir -Directory -ErrorAction SilentlyContinue)) {
                        if (Test-Path -LiteralPath (Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh')) { $found = $true; break }
                    }
                }
            }
            # global root (respects a relocated prefix)
            if (-not $found) {
                $globalRoot = (& $npm root -g 2>$null | Select-Object -First 1)
                if ($globalRoot -and (Test-Path -LiteralPath (Join-Path $globalRoot '@deepseek-ai\dsh'))) { $found = $true }
            }
        }
    }
}

if ($found) { 'installed' | Set-Content -LiteralPath $out -Encoding ASCII }
else { 'missing' | Set-Content -LiteralPath $out -Encoding ASCII }
