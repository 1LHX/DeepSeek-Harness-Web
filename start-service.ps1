# start-service.ps1 - Start DeepSeek Harness Web service in background (called by dsh-panel.exe)
# v2:
#   - starts node directly (no cmd.exe wrapper), so dsh-web.pid holds the real npx node PID;
#   - pre-checks the port and fails fast when it is already in use;
#   - rotates dsh-web.log / dsh-web.err.log when they grow past 5 MB;
#   - auto-detects node.exe (PATH -> registry -> default install path);
#   - reads the port from dsh-web.config (fallback 3080).
# All arguments are constants; writes PID file and exits. Every failure is
# appended to dsh-web.err.log (the panel shows it in the log area).
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logsDir = Join-Path $dir 'logs'
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$outLog = Join-Path $logsDir 'dsh-web.log'
$errLog = Join-Path $logsDir 'dsh-web.err.log'
$pidFile = Join-Path $runDir 'dsh-web.pid'
$cfgFile = Join-Path $dir 'dsh-web.config'

# ---- shared config: port ----
$port = 3080
if (Test-Path -LiteralPath $cfgFile) {
    $m = Select-String -LiteralPath $cfgFile -Pattern '^\s*port\s*=\s*(\d+)'
    if ($m) { $port = [int]$m.Matches[0].Groups[1].Value }
}

function Test-Port([int]$p) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $ar = $c.BeginConnect('127.0.0.1', $p, $null, $null)
        if ($ar.AsyncWaitHandle.WaitOne(300)) { $c.EndConnect($ar); return $true }
    } catch { }
    finally { $c.Close() }
    return $false
}

try {
    # ---- locate node.exe: PATH -> registry (HKLM/HKCU) -> default layout ----
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
    if (-not $node) {
        $cand = 'C:\Program Files\nodejs\node.exe'
        if (Test-Path -LiteralPath $cand) { $node = $cand }
    }
    if (-not $node) { throw 'node.exe not found. Install Node.js or set PATH.' }

    $npxCli = Join-Path (Split-Path -Parent $node) 'node_modules\npm\bin\npx-cli.js'
    if (-not (Test-Path -LiteralPath $npxCli)) { throw "npx-cli.js not found: $npxCli" }

    # ---- port pre-check: fail fast instead of a bind error later ----
    if (Test-Port $port) { throw "port $port already in use; the service may already be running" }

    # ---- rotate logs (>5 MB): keep one generation ----
    foreach ($f in @($outLog, $errLog)) {
        if (Test-Path -LiteralPath $f) {
            try {
                if ((Get-Item -LiteralPath $f).Length -gt 5MB) {
                    $old = $f + '.1'
                    Remove-Item -LiteralPath $old -ErrorAction SilentlyContinue
                    Rename-Item -LiteralPath $f -NewName (Split-Path -Leaf $old) -ErrorAction SilentlyContinue
                }
            } catch { }
        }
    }

    $argLine = '"' + $npxCli + '" --yes --prefer-offline @deepseek-ai/dsh web'
    $p = Start-Process -FilePath $node -ArgumentList $argLine -WindowStyle Hidden `
        -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
    $p.Id | Set-Content -LiteralPath $pidFile -Encoding ASCII
} catch {
    Add-Content -LiteralPath $errLog -Value ("[start-service] " + $_.Exception.Message) -Encoding UTF8
    exit 1
}
