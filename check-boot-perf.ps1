# check-boot-perf.ps1 - 冷启动耗时诊断（只读，不触碰正在运行的服务）
#
# 用途：把「开机后第一次打开面板很慢」拆成可测量的几个阶段，定位瓶颈。
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File check-boot-perf.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File check-boot-perf.ps1 -BootTest
#     -BootTest 会在 3099 端口临时起一个实例（不动 3080），测完自动结束。
#
# 阶段划分（对应面板真实链路）：
#   1. powershell.exe 冷启动        -> 面板通过 ShellExecute 调用 .ps1 的开销
#   2. check-dsh.ps1 里的 npm 探测  -> npm config get cache / npm root -g
#   3. npx 入口 (npm exec)          -> start-service.ps1 的 npx 兜底（正常走直连 bin.js）
#   4. 真正的 dsh 引导              -> node bin.js web（模块加载 + 插件树 + 监听）
#   5. 前端资源                     -> index.html + assets + 客户端插件 bundle
#   6. WebView2 用户数据目录        -> 88MB 会话数据，冷启动要扫
param(
    [switch]$BootTest,
    [int]$TestPort = 3099
)
$ErrorActionPreference = 'Continue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$cfg = Join-Path $dir 'dsh-web.config'
$port = 3080
if (Test-Path $cfg) {
    $m = Select-String -LiteralPath $cfg -Pattern '^\s*port\s*=\s*(\d+)'
    if ($m) { $port = [int]$m.Matches[0].Groups[1].Value }
}
# 端口可能因冲突被顺延：以 run/dsh-web.port 记录的「实际端口」为准
$portFile = Join-Path $dir 'run\dsh-web.port'
if (Test-Path $portFile) {
    $recorded = 0
    $raw = ((Get-Content -LiteralPath $portFile -ErrorAction SilentlyContinue) -join '').Trim()
    if ([int]::TryParse($raw, [ref]$recorded) -and $recorded -gt 0) { $port = $recorded }
}

function Line($text) { Write-Host $text }

function TimeBlock([string]$label, [scriptblock]$block) {
    $t = Measure-Command $block
    Line ("  {0,-46} {1,8:N0} ms" -f $label, $t.TotalMilliseconds)
    return $t.TotalMilliseconds
}

# ---- dsh version helpers (same rules as dsh-panel.cs / start-service.ps1) ----
function ConvertTo-DshVersionKey([string]$v) {
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
    return [pscustomobject]@{ Major = $nums[0]; Minor = $nums[1]; Patch = $nums[2]; Pre = $(if ($pre) { 1 } else { 0 }); PreNum = $preNum }
}

function Compare-DshVersion([string]$a, [string]$b) {
    if (-not $a -and -not $b) { return 0 }
    if (-not $a) { return -1 }
    if (-not $b) { return 1 }
    $ka = ConvertTo-DshVersionKey $a
    $kb = ConvertTo-DshVersionKey $b
    if ($ka.Major -ne $kb.Major) { if ($ka.Major -gt $kb.Major) { return 1 } else { return -1 } }
    if ($ka.Minor -ne $kb.Minor) { if ($ka.Minor -gt $kb.Minor) { return 1 } else { return -1 } }
    if ($ka.Patch -ne $kb.Patch) { if ($ka.Patch -gt $kb.Patch) { return 1 } else { return -1 } }
    if ($ka.Pre -ne $kb.Pre) { if ($ka.Pre -lt $kb.Pre) { return 1 } else { return -1 } }
    if ($ka.PreNum -ne $kb.PreNum) { if ($ka.PreNum -gt $kb.PreNum) { return 1 } else { return -1 } }
    return 0
}

function Read-DshVersion([string]$binJs) {
    try {
        if (-not $binJs) { return $null }
        $pkg = Join-Path (Split-Path -Parent (Split-Path -Parent $binJs)) 'package.json'
        if (-not (Test-Path -LiteralPath $pkg)) { return $null }
        $text = [System.IO.File]::ReadAllText($pkg, [System.Text.Encoding]::UTF8)
        $m = [regex]::Match($text, '"version"\s*:\s*"([^"]+)"')
        if ($m.Success) { return $m.Groups[1].Value }
    } catch { }
    return $null
}

Line "==================== DSH Web 启动耗时诊断 ===================="
Line ("时间: " + (Get-Date -Format 'yyyy/MM/dd HH:mm:ss'))
Line ("端口: $port")
Line ""

# ---- 环境 ----
Line "[0] 环境"
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
Line ("  本次开机时间                          {0}" -f $boot)
Line ("  已开机                              {0:N1} 小时" -f ((Get-Date) - $boot).TotalHours)
$os = Get-CimInstance Win32_OperatingSystem
Line ("  可用内存                             {0:N1} GB / {1:N1} GB" -f ($os.FreePhysicalMemory/1MB), ($os.TotalVisibleMemorySize/1MB))
$disk = Get-PhysicalDisk | Select-Object -First 1
Line ("  系统盘介质                           {0} / {1}" -f $disk.MediaType, $disk.BusType)
try {
    $mp = Get-MpComputerStatus
    Line ("  Defender 实时保护                    {0}" -f $mp.RealTimeProtectionEnabled)
} catch { Line "  Defender 状态未知" }
try {
    $ex = (Get-MpPreference).ExclusionPath
    if ($ex) { Line ("  Defender 排除路径                    " + ($ex -join '; ')) }
    else { Line "  Defender 排除路径                    （无）" }
} catch {
    Line "  Defender 排除路径                    需要管理员权限才能查看（当前非管理员）"
}
Line ""

# ---- 定位 node / npx / bin.js ----
Line "[1] 定位入口"
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
Line ("  node.exe                            $node")
$npxCli = Join-Path (Split-Path -Parent $node) 'node_modules\npm\bin\npx-cli.js'
Line ("  npx-cli.js                          $(Test-Path $npxCli)")

$npxRoot = Join-Path $env:LOCALAPPDATA 'npm-cache\_npx'
$candidates = New-Object System.Collections.ArrayList
if (Test-Path $npxRoot) {
    foreach ($d in Get-ChildItem $npxRoot -Directory -ErrorAction SilentlyContinue) {
        [void]$candidates.Add((Join-Path $d.FullName 'node_modules\@deepseek-ai\dsh\lib\bin.js'))
    }
}
[void]$candidates.Add((Join-Path $env:APPDATA 'npm\node_modules\@deepseek-ai\dsh\lib\bin.js'))
[void]$candidates.Add((Join-Path (Split-Path -Parent $node) 'node_modules\@deepseek-ai\dsh\lib\bin.js'))
# Same rule as start-service.ps1 / dsh-launch.mjs: highest version wins, mtime only breaks ties.
$binJs = $null
$bestVer = $null
$bestTime = [datetime]::MinValue
foreach ($cand in $candidates) {
    if (-not $cand -or -not (Test-Path -LiteralPath $cand)) { continue }
    $ver = Read-DshVersion $cand
    $t = (Get-Item -LiteralPath $cand).LastWriteTimeUtc
    if (-not $binJs) { $binJs = $cand; $bestVer = $ver; $bestTime = $t; continue }
    $cmp = Compare-DshVersion $ver $bestVer
    if (($cmp -gt 0) -or (($cmp -eq 0) -and ($t -gt $bestTime))) { $binJs = $cand; $bestVer = $ver; $bestTime = $t }
}
Line ("  dsh bin.js                          $binJs $(if ($bestVer) { '(v' + $bestVer + ')' })")
Line ""

if ($binJs) {
    Line "[2] 各阶段独立耗时（当前为热态基准；冷态会明显更慢）"
    TimeBlock "node 裸启动 (-e 0)" { & $node -e "0" } | Out-Null
    TimeBlock "npx 入口 (--no-install；未命中会快速失败)" {
        & $node $npxCli --no-install @deepseek-ai/dsh --version 2>$null | Out-Null
    } | Out-Null
    TimeBlock "直连 node bin.js --version" { & $node $binJs --version 2>$null | Out-Null } | Out-Null
    TimeBlock "powershell.exe -NoProfile 冷启动" {
        powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "exit 0" | Out-Null
    } | Out-Null
    TimeBlock "npm.cmd config get cache（check-dsh 用）" { npm.cmd config get cache 2>$null | Out-Null } | Out-Null
    TimeBlock "npm.cmd root -g（check-dsh 用）" { npm.cmd root -g 2>$null | Out-Null } | Out-Null
    Line ""
}

# ---- 文件规模（冷启动要读的量） ----
Line "[3] 冷启动要扫描的文件规模"
if ($npxRoot) {
    $f = Get-ChildItem $npxRoot -Recurse -File -Force -ErrorAction SilentlyContinue
    Line ("  _npx 缓存                           {0:N0} 个文件 / {1:N1} MB" -f $f.Count, (($f | Measure-Object Length -Sum).Sum / 1MB))
    Line ("  其中 .js                            {0:N0} 个" -f ($f | Where-Object { $_.Extension -in '.js', '.mjs', '.cjs' }).Count)
}
$dshHome = if ($env:DSH_HOME) { $env:DSH_HOME } else { Join-Path $env:USERPROFILE '.dsh' }
Line ("  DSH_HOME                            $dshHome")
$wv = Join-Path $dir 'run\webview2-data'
if (Test-Path $wv) {
    $wf = Get-ChildItem $wv -Recurse -File -Force -ErrorAction SilentlyContinue
    Line ("  WebView2 用户数据                   {0:N0} 个文件 / {1:N1} MB" -f $wf.Count, (($wf | Measure-Object Length -Sum).Sum / 1MB))
}
Line ""

# ---- 开机自启 / 常驻状态 ----
Line "[4] 开机自启与服务常驻"
$runKey = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue
$entry = $runKey.PSObject.Properties | Where-Object { $_.Name -match 'Dsh|dsh' }
if ($entry) { Line ("  开机自启                            已注册: " + ($entry | ForEach-Object { "$($_.Name)=$($_.Value)" })) }
else { Line "  开机自启                            未注册（每次开机都是完整冷启动）" }
$svc = Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskName -match 'dsh|DeepSeek' }
if ($svc) { Line ("  计划任务                            " + (($svc | ForEach-Object { "$($_.TaskName)[$($_.State)]" }) -join ', ')) }
else { Line "  计划任务                            无" }
$listening = netstat -ano | Select-String ":$port\s+.*LISTENING"
Line ("  当前 $port 监听                      " + ($(if ($listening) { '在' } else { '不在' })))
Line ""

# ---- 真实引导（可选，用 3099 端口） ----
if ($BootTest -and $binJs) {
    Line "[5] 真实引导耗时（$TestPort 端口，测完即结束；不影响 $port）"
    $c = New-Object System.Net.Sockets.TcpClient
    $busy = $false
    try { $c.Connect('127.0.0.1', $TestPort); $busy = $true } catch { } finally { $c.Close() }
    if ($busy) { Line "  $TestPort 已被占用，跳过" }
    else {
        $out = Join-Path $env:TEMP 'bootperf.out.log'
        $err = Join-Path $env:TEMP 'bootperf.err.log'
        Remove-Item $out, $err -ErrorAction SilentlyContinue
        # dsh 0.2.x opens the default browser on boot; keep the boot test headless too.
        # 0.1.x rejects the flag, so only pass it for 0.2.x+.
        $noOpen = @()
        if ($binJs) {
            $k = ConvertTo-DshVersionKey (Read-DshVersion $binJs)
            if ($k -and (($k.Major -gt 0) -or ($k.Minor -ge 2))) { $noOpen = @('--no-open') }
        }
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $p = Start-Process -FilePath $node -ArgumentList (@("`"$binJs`"", 'web', '--port', $TestPort) + $noOpen) `
            -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
        $ready = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(240)
        while ([DateTime]::UtcNow -lt $deadline) {
            $tc = New-Object System.Net.Sockets.TcpClient
            try {
                $ar = $tc.BeginConnect('127.0.0.1', $TestPort, $null, $null)
                if ($ar.AsyncWaitHandle.WaitOne(200)) { $tc.EndConnect($ar); $ready = $sw.Elapsed.TotalSeconds; break }
            } catch { } finally { $tc.Close() }
            if ($p.HasExited) { break }
            Start-Sleep -Milliseconds 100
        }
        $sw.Stop()
        if ($ready) { Line ("  从 spawn 到端口监听                 {0:N2} 秒" -f $ready) }
        else {
            Line "  引导失败，stderr:"
            Get-Content $err -Tail 10 -ErrorAction SilentlyContinue | ForEach-Object { Line "    $_" }
        }
        if (-not $p.HasExited) { & taskkill /PID $p.Id /T /F | Out-Null }
        Line ""
    }
}

# ---- 前端资源（服务在跑时才有意义） ----
# 0.2.x 的 Web UI 要 launch token；从服务日志取最近一条匹配端口的地址，
# 并用同一个 WebSession 保存兑换出来的 Cookie（0.1.x 没有 token 时回落到裸地址）。
function Get-DshWebUrl([string]$baseUrl) {
    try {
        $log = Join-Path $dir 'logs\dsh-web.log'
        if (Test-Path -LiteralPath $log) {
            $lines = @(Get-Content -LiteralPath $log -Tail 400 -ErrorAction SilentlyContinue)
            for ($i = $lines.Count - 1; $i -ge 0; $i--) {
                $m = [regex]::Match($lines[$i], 'dsh web:\s*(http://[^\s]*token=[^\s]+)')
                if ($m.Success -and $m.Groups[1].Value -match (':' + $port + '[/]')) { return $m.Groups[1].Value }
            }
        }
    } catch { }
    return $baseUrl
}

$c2 = New-Object System.Net.Sockets.TcpClient
$up = $false
try { $c2.Connect('127.0.0.1', $port); $up = $true } catch { } finally { $c2.Close() }
if ($up) {
    Line "[6] 前端资源（服务在跑，热态）"
    try {
        $webUrl = Get-DshWebUrl ("http://127.0.0.1:$port/")
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $t = Measure-Command { $r = Invoke-WebRequest -Uri $webUrl -UseBasicParsing -TimeoutSec 20 -WebSession $session }
        Line ("  index.html                          {0,8:N0} ms  {1,9:N0} B" -f $t.TotalMilliseconds, $r.Content.Length)
        Line ("  客户端插件 bundle 引用数            " + ([regex]::Matches($r.Content, '(?:src|href)="[^"]*plugins/')).Count)
        # 0.2.x 的入口用相对路径（./assets/...、plugins/...），且带 HTML 实体；
        # 统一解码后用页面 URL 解析，不能只在前面拼 http://127.0.0.1:<port>。
        $seen = 0
        foreach ($m in [regex]::Matches($r.Content, '(?:src|href)="([^"]+\.js[^"]*)"')) {
            if ($seen -ge 10) { break }
            $rel = [System.Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
            # 插件 bundle 动辄数 MB（0.2.1 的两个 URL 合计 >10 MB），只计数不下载；
            # 这里只测主应用 assets，避免诊断本身变成一次大流量操作。
            if ($rel -match 'plugins/') { continue }
            $seen++
            try { $u = ([Uri]::new([Uri]$webUrl, $rel)).AbsoluteUri } catch { continue }
            $t2 = Measure-Command { $x = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec 20 -WebSession $session }
            Line ("  {0,-36} {1,8:N0} ms  {2,9:N0} B" -f $rel, $t2.TotalMilliseconds, $x.RawContentLength)
        }
    } catch { Line ("  取样失败: " + $_.Exception.Message) }
    Line ""
}

Line "==================== 建议关注 ===================="
Line "  · 冷启动慢的主因是「第一次读文件还要过杀软 + V8 全量重新编译」，"
Line "    热态基准通常只有 1~3 秒；两者差距就是这个代价。"
Line "  · 让开机自启常驻（或开机时预热服务），可以把这段代价挪到你不等待的时候。"
Line "  · 运行 add-defender-exclusions.cmd（只排除 _npx；实测其他路径收益≈0，见 README）。"
Line "  · start-service.ps1 已改为直连 node bin.js（npx 仅兜底），热态省约 1 秒、冷态省更多。"
exit 0
