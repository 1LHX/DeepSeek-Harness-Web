# start-service.ps1 - Start DeepSeek Harness Web service in background (called by dsh-panel.exe)
#
# v3 (boot-time work):
#   - launches `node <bin.js> web` DIRECTLY when an installed dsh can be located
#     (npx cache / %APPDATA%\npm / beside node.exe). That skips `npx` -- a whole extra
#     npm process plus its registry resolution and reify check: ~1 s warm, and tens of
#     seconds on a cold boot.
#   - can enable Node's V8 compile cache for THIS process only, through
#     hooks/compile-cache.mjs (`node --import`), so module compilation is skipped on
#     later boots. OFF BY DEFAULT (`compilecache=1` in dsh-web.config turns it on):
#     measured on this machine the warm boot is 1.89 s with and 1.89 s without it, so
#     the compile phase is not where the wait comes from -- keep the default path
#     simple, and keep the switch for a machine where it does help.
#   - escalation, never silent failure: if an attempt exits before the port opens, the
#     next cheaper-safe attempt runs (with hook -> without hook -> npx). An attempt that
#     is still running is never doubled.
#   - waits for the port and writes one timing line per phase to run/boot-timing.log
#     (the panel appends to the same file), so a slow boot can be attributed.
#   - v3.3: port conflict fallback. If the configured port is held by another program the
#     service moves to the next free port in [port+1, port+20] and records it in
#     run/dsh-web.port (the panel adopts that port only after verifying the listener PID
#     matches run/dsh-web.pid). If the holder is our own dsh, this exits 0 as "already up".
#   - keeps v2 behaviour: node detection (PATH -> registry -> default layout), log rotation
#     (>5 MB, one generation), PID file.
#
# All arguments are constants; writes the PID file. Every failure is appended to
# dsh-web.err.log (the panel shows it in the log area).
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logsDir = Join-Path $dir 'logs'
$runDir = Join-Path $dir 'run'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$outLog = Join-Path $logsDir 'dsh-web.log'
$errLog = Join-Path $logsDir 'dsh-web.err.log'
$pidFile = Join-Path $runDir 'dsh-web.pid'
$portFile = Join-Path $runDir 'dsh-web.port'
$cfgFile = Join-Path $dir 'dsh-web.config'
$timingLog = Join-Path $runDir 'boot-timing.log'
$hookFile = Join-Path $dir 'hooks\compile-cache.mjs'
$cacheDir = Join-Path $runDir 'node-compile-cache'

# ---- shared config: port (+ optional compilecache=1 / prewarm knobs) ----
$port = 3080
$compileCacheEnabled = $false
if (Test-Path -LiteralPath $cfgFile) {
    $m = Select-String -LiteralPath $cfgFile -Pattern '^\s*port\s*=\s*(\d+)'
    if ($m) { $port = [int]$m.Matches[0].Groups[1].Value }
    $c = Select-String -LiteralPath $cfgFile -Pattern '^\s*compilecache\s*=\s*(\S+)'
    if ($c) {
        $v = $c.Matches[0].Groups[1].Value.Trim().ToLowerInvariant()
        if ($v -eq '1' -or $v -eq 'true' -or $v -eq 'on' -or $v -eq 'yes') { $compileCacheEnabled = $true }
    }
}

# ---- boot timing log (the panel appends to the same file) ----
function Write-Timing([string]$phase) {
    try {
        if (Test-Path -LiteralPath $timingLog) {
            if ((Get-Item -LiteralPath $timingLog).Length -gt 256KB) {
                Remove-Item -LiteralPath ($timingLog + '.1') -ErrorAction SilentlyContinue
                Move-Item -LiteralPath $timingLog -Destination ($timingLog + '.1') -Force -ErrorAction SilentlyContinue
            }
        }
        $line = ('{0:yyyy-MM-dd HH:mm:ss.fff} [start-service] {1}' -f (Get-Date), $phase)
        Add-Content -LiteralPath $timingLog -Value $line -Encoding UTF8
    } catch { }
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

# ---- locate an installed dsh entry point (pure file system; starts no process) ----
function Resolve-DshBinJs([string]$nodePath) {
    $paths = New-Object System.Collections.ArrayList
    $npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
    if (Test-Path -LiteralPath $npxRoot) {
        foreach ($d in (Get-ChildItem -LiteralPath $npxRoot -Directory -ErrorAction SilentlyContinue)) {
            [void]$paths.Add((Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js'))
        }
    }
    [void]$paths.Add((Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\lib\bin.js'))
    if ($nodePath) {
        [void]$paths.Add((Join-Path (Split-Path -Parent $nodePath) 'node_modules\@deepseek-ai\dsh\lib\bin.js'))
    }
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

# ---- keep the output of a failed attempt instead of letting the next one truncate it ----
function Preserve-AttemptLogs([string]$tag) {
    foreach ($f in @($outLog, $errLog)) {
        try {
            if (Test-Path -LiteralPath $f) {
                $dst = $f + '.' + $tag
                Remove-Item -LiteralPath $dst -ErrorAction SilentlyContinue
                Move-Item -LiteralPath $f -Destination $dst -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
}

try {
    $bootWatch = [System.Diagnostics.Stopwatch]::StartNew()
    # 启动期需要事后告知用户的事项（例如端口顺延）。注意不能立刻写 err.log：
    # Start-Process -RedirectStandardError 会以「截断」方式打开该文件，写早了会被抹掉。
    $script:bootNotices = New-Object System.Collections.ArrayList
    Write-Timing 'ps started'

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
    Write-Timing ('node found: ' + $node)

    $npxCli = Join-Path (Split-Path -Parent $node) 'node_modules\npm\bin\npx-cli.js'
    $binJs = Resolve-DshBinJs $node
    if ((-not $binJs) -and (-not (Test-Path -LiteralPath $npxCli))) {
        throw "No dsh entry point found (npx cache / global npm / beside node.exe) and npx-cli.js is missing: $npxCli"
    }

    # ---- port pre-check + conflict fallback ----
    # The configured port is the single source of truth, but it is not always available:
    # another app (or a leftover dsh) may hold it. Behaviour:
    #   * the listener is our own dsh  -> treat as "already running", record the port, exit 0;
    #   * someone else holds it        -> move to the next free port in [port+1, port+20],
    #                                     record it in run/dsh-web.port and warn loudly;
    #   * nothing free                 -> fail fast with the exact range that was tried.
    # The panel only adopts the recorded port after checking that the listener's PID matches
    # run/dsh-web.pid, so a stale file can never point the panel at someone else's server.
    $requestedPort = $port
    function Test-OwnDshListener([int]$p) {
        try {
            $listener = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction Stop |
                Select-Object -First 1
            if (-not $listener) { return $false }
            $proc = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $listener.OwningProcess) -ErrorAction Stop
            if (-not $proc -or -not $proc.CommandLine) { return $false }
            return ($proc.CommandLine -match '@deepseek-ai[\\/]dsh' -or $proc.CommandLine -match 'dsh[\\/]lib[\\/]bin\.js')
        } catch { return $false }
    }

    if (Test-Path -LiteralPath $portFile) {
        $recorded = 0
        try { [void][int]::TryParse(((Get-Content -LiteralPath $portFile -ErrorAction Stop) -join '').Trim(), [ref]$recorded) } catch { }
        if (($recorded -gt 0) -and (Test-Port $recorded) -and (Test-OwnDshListener $recorded)) {
            # 已经在跑（可能停在上次顺延出来的端口）：先退出，绝不重复拉起，
            # 否则第二次重定向会因为运行中的 node 占着 err.log 而直接失败。
            Write-Timing ('already running on recorded port ' + $recorded + ' (configured ' + $port + '); nothing to do')
            exit 0
        }
    }

    if (Test-Port $port) {
        if (Test-OwnDshListener $port) {
            Set-Content -LiteralPath $portFile -Value ([string]$port) -Encoding ASCII
            Write-Timing ('port ' + $port + ' already served by our own dsh; nothing to do')
            exit 0
        }
        $picked = 0
        for ($i = 1; $i -le 20; $i++) {
            $cand = $port + $i
            if ($cand -gt 65535) { break }
            if (-not (Test-Port $cand)) { $picked = $cand; break }
        }
        if ($picked -eq 0) {
            throw ("port " + $port + " is in use and no free port was found in " + ($port + 1) + "-" + ($port + 20) + "; free it or change port= in dsh-web.config")
        }
        $holderPid = 0
        try {
            $holderPid = (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop |
                Select-Object -First 1).OwningProcess
        } catch { }
        $holderName = 'unknown'
        if ($holderPid -gt 0) {
            try { $holderName = (Get-Process -Id $holderPid -ErrorAction Stop).ProcessName } catch { }
        }
        # 不要在这里写 err.log：Start-Process -RedirectStandardError 会以「截断」方式打开它，
        # 写早了就被抹掉。这条信息已进 boot-timing.log，面板采纳新端口时还会在活动日志里提示。
        $script:bootNotices += ('port ' + $port + ' is held by ' + $holderName + ' (pid ' + $holderPid + '); running on port ' + $picked + ' instead')
        Write-Timing ('port ' + $port + ' busy (' + $holderName + ' pid ' + $holderPid + ') -> fallback ' + $picked)
        $port = $picked
    }
    Set-Content -LiteralPath $portFile -Value ([string]$port) -Encoding ASCII
    $portLabel = ''
    if ($port -ne $requestedPort) { $portLabel = ' (fallback from ' + $requestedPort + ')' }
    Write-Timing ('port free: ' + $port + $portLabel + ' | entry: ' + $(if ($binJs) { $binJs } else { 'npx fallback' }))

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

    # ---- the log files must be writable: a still-running node holds them open ----
    # Start-Process opens its redirect targets exclusively, so a locked file would abort the
    # launch. Use a per-launch file name instead of failing outright.
    function Test-LogWritable([string]$f) {
        try {
            if (-not (Test-Path -LiteralPath $f)) { return $true }
            $fs = [System.IO.File]::Open($f, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
            $fs.Close()
            return $true
        } catch { return $false }
    }
    if (-not ((Test-LogWritable $outLog) -and (Test-LogWritable $errLog))) {
        $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
        $outLog = Join-Path $logsDir ('dsh-web.' + $stamp + '.log')
        $errLog = Join-Path $logsDir ('dsh-web.' + $stamp + '.err.log')
        Write-Timing ('service logs were locked by another process; writing ' + (Split-Path -Leaf $outLog) + ' instead')
    }

    # ---- compile cache hook: used only after a successful probe ----
    $hookUri = $null
    $hookOk = $false
    if ($compileCacheEnabled -and (Test-Path -LiteralPath $hookFile)) {
        try {
            $hookUri = (New-Object System.Uri($hookFile)).AbsoluteUri
            New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
            $env:DSH_COMPILE_CACHE_DIR = $cacheDir
            $env:DSH_COMPILE_CACHE_FLUSH_MS = '15000'
            $probe = & $node '--import' $hookUri '-e' 'process.exit(0)' 2>&1
            if ($LASTEXITCODE -eq 0) {
                $hookOk = $true
                Write-Timing 'compile cache hook ok'
            } else {
                Write-Timing ('compile cache hook probe failed (exit ' + $LASTEXITCODE + '): ' + ($probe -join ' '))
            }
        } catch {
            $hookOk = $false
            Write-Timing ('compile cache hook unavailable: ' + $_.Exception.Message)
        }
        finally {
            if (-not $hookOk) {
                Remove-Item Env:DSH_COMPILE_CACHE_DIR -ErrorAction SilentlyContinue
                Remove-Item Env:DSH_COMPILE_CACHE_FLUSH_MS -ErrorAction SilentlyContinue
            }
        }
    }

    # ---- attempt ladder: hook -> plain entry -> npx. Only an EXITED attempt is retried. ----
    # `--port` is passed explicitly: dsh-web.config is the single source of truth for the
    # port. Without it dsh would bind the profile default (3080) and a changed `port=`
    # would leave the panel probing a port nothing listens on.
    $webArgs = @('web', '--port', [string]$port)
    $entryArgs = @()
    if ($binJs) { $entryArgs = @(('"' + $binJs + '"')) + $webArgs }
    else { $entryArgs = @(('"' + $npxCli + '"'), '--yes', '--prefer-offline', '@deepseek-ai/dsh') + $webArgs }
    $npxArgs = @(('"' + $npxCli + '"'), '--yes', '--prefer-offline', '@deepseek-ai/dsh') + $webArgs

    $attempts = New-Object System.Collections.ArrayList
    if ($hookOk) {
        [void]$attempts.Add([pscustomobject] @{ Name = 'entry+hook'; Args = (@(('--import "' + $hookUri + '"')) + $entryArgs); Hook = $true })
    }
    [void]$attempts.Add([pscustomobject] @{ Name = $(if ($binJs) { 'entry' } else { 'npx' }); Args = $entryArgs; Hook = $false })
    if ($binJs -and (Test-Path -LiteralPath $npxCli)) {
        [void]$attempts.Add([pscustomobject] @{ Name = 'npx-fallback'; Args = $npxArgs; Hook = $false })
    }

    $readyOnce = $false
    $lastPid = 0
    $index = 0
    foreach ($attempt in $attempts) {
        $index++
        if ($attempt.Hook) { $env:DSH_COMPILE_CACHE_DIR = $cacheDir; $env:DSH_COMPILE_CACHE_FLUSH_MS = '15000' }
        else {
            Remove-Item Env:DSH_COMPILE_CACHE_DIR -ErrorAction SilentlyContinue
            Remove-Item Env:DSH_COMPILE_CACHE_FLUSH_MS -ErrorAction SilentlyContinue
        }
        $argLine = ($attempt.Args -join ' ')
        $p = Start-Process -FilePath $node -ArgumentList $argLine -WindowStyle Hidden `
            -RedirectStandardOutput $outLog -RedirectStandardError $errLog -PassThru
        $lastPid = $p.Id
        $p.Id | Set-Content -LiteralPath $pidFile -Encoding ASCII
        Write-Timing ('attempt ' + $index + '/' + $attempts.Count + ' "' + $attempt.Name + '" spawned pid=' + $p.Id + ' at +' + [int]$bootWatch.Elapsed.TotalMilliseconds + 'ms')

        $deadline = (Get-Date).AddSeconds(120)
        $exited = $false
        while ((Get-Date) -lt $deadline) {
            if (Test-Port $port) { $readyOnce = $true; break }
            try { if ($p.HasExited) { $exited = $true; break } } catch { $exited = $true; break }
            Start-Sleep -Milliseconds 150
        }

        if ($readyOnce) {
            Write-Timing ('PORT READY in ' + [math]::Round($bootWatch.Elapsed.TotalSeconds, 2) + 's (attempt "' + $attempt.Name + '", pid=' + $p.Id + ')')
            break
        }
        if (-not $exited) {
            # Still running but not listening: do not spawn a second instance.
            Write-Timing ('no port after 120s, process still running (attempt "' + $attempt.Name + '", pid=' + $p.Id + ')')
            Add-Content -LiteralPath $errLog -Value ('[start-service] no port ' + $port + ' after 120s; process ' + $p.Id + ' is still running (left alone)') -Encoding UTF8
            break
        }

        $code = 'n/a'
        try { if ($null -ne $p.ExitCode) { $code = $p.ExitCode } } catch { $code = 'n/a' }
        Write-Timing ('attempt "' + $attempt.Name + '" exited before the port opened (exit=' + $code + ')')
        Preserve-AttemptLogs ('failed' + $index)
    }

    # 启动期通知（端口顺延等）：在所有 Start-Process 重定向（截断式打开）之后才写，
    # 否则会被下一次重定向抹掉；面板会把 err.log 的新增行显示在日志窗口里。
    foreach ($n in $script:bootNotices) {
        try { Add-Content -LiteralPath $errLog -Value ('[start-service] ' + $n) -Encoding UTF8 } catch { }
    }

    if (-not $readyOnce) {
        Write-Timing ('failed: service did not open port ' + $port + ' in ' + [math]::Round($bootWatch.Elapsed.TotalSeconds, 2) + 's')
        Add-Content -LiteralPath $errLog -Value ('[start-service] service did not open port ' + $port + ' (see logs\dsh-web.err.log.failed* for each attempt)') -Encoding UTF8
        exit 1
    }
} catch {
    Write-Timing ('error: ' + $_.Exception.Message)
    Add-Content -LiteralPath $errLog -Value ('[start-service] ' + $_.Exception.Message) -Encoding UTF8
    exit 1
}
