# check-dsh.ps1 - check whether @deepseek-ai/dsh is available (npx cache / global install)
# Called by dsh-panel.exe (hidden). Writes "installed" or "missing" to run/dsh-check.txt.
# Read-only: only writes the result file under run/.
$ErrorActionPreference = 'SilentlyContinue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$out = Join-Path $runDir 'dsh-check.txt'
Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
$found = $false

# ---- locate node.exe: PATH -> registry (same logic as start-service.ps1) ----
$node = $null
$cmd = Get-Command node.exe -ErrorAction SilentlyContinue
if ($cmd) { $node = $cmd.Source }
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

function Test-NpxDshDir([string]$npxDir) {
    if (-not $npxDir -or -not (Test-Path -LiteralPath $npxDir)) { return $false }
    $hits = Get-ChildItem -LiteralPath $npxDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'node_modules\@deepseek-ai\dsh') }
    return ($null -ne $hits -and $hits.Count -gt 0)
}

if ($node) {
    $npm = Join-Path (Split-Path -Parent $node) 'npm.cmd'
    # 1) npx cache from npm config
    if (Test-Path -LiteralPath $npm) {
        $cache = (& $npm config get cache 2>$null | Select-Object -First 1)
        if ($cache -and (Test-NpxDshDir (Join-Path $cache '_npx'))) { $found = $true }
        # 2) global install
        if (-not $found) {
            $globalRoot = (& $npm root -g 2>$null | Select-Object -First 1)
            if ($globalRoot -and (Test-Path -LiteralPath (Join-Path $globalRoot '@deepseek-ai\dsh'))) { $found = $true }
        }
    }
    # 3) default npm-cache fallback
    if (-not $found) {
        $def = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
        if (Test-NpxDshDir $def) { $found = $true }
    }
}

if ($found) { 'installed' | Set-Content -LiteralPath $out -Encoding ASCII }
else { 'missing' | Set-Content -LiteralPath $out -Encoding ASCII }
