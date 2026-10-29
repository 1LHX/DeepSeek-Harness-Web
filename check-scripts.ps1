# check-scripts.ps1 - 脚本编码与语法门禁（run-tests.cmd 第一步）
#
# 为什么需要它：本会话里反复出现的一类问题，根源都是**编码**，而不只是操作失误：
#   * Windows PowerShell 5.1 读取「无 BOM 的 UTF-8」时按 ANSI 解释，一条中文注释就能让
#     整个脚本语法错误——`start-service.ps1` 真的这样坏过一次，表现是「服务静默起不来、
#     面板只弹一句启动失败」，日志里没有任何线索；
#   * cmd.exe 按 OEM 代码页解析 .bat/.cmd，.cmd 里出现中文会直接报
#     "xxx 不是内部或外部命令"，甚至把注释行当命令执行；
#   * .mjs 是 node 直接读的，语法错误会让启动器/代理静默退出。
# 这三类都属于「写的时候看起来没问题、跑起来才炸」，所以用门禁在提交/测试阶段拦住。
#
# 规则：
#   1. 每个 .ps1 —— 含非 ASCII 字符时**必须**有 UTF-8 BOM；
#   2. 每个 .ps1 —— 必须能被 **Windows PowerShell 5.1** 解析（它才是面板实际调用的解释器）；
#   3. 每个 .cmd / .bat —— 必须纯 ASCII（0 个 >0x7F 字节）；
#   4. 每个 .mjs —— `node --check` 通过；
#   5. dsh-web.config —— 必须纯 ASCII（PS 5.1 与启动器对它的解码方式必须一致）；
#   6. dsh-panel.exe 的版本必须与 dsh-panel.cs 里的 AssemblyVersion 一致——
#      否则「源码改了、exe 没重编」会让所有验证看起来正常但实际跑的是旧逻辑（本项目踩过两次）。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File check-scripts.ps1
#   ... -Quiet        只输出失败项
#   ... -Fast         跳过「解析」和「node --check」（纯字节检查，毫秒级）；供双击入口调用
#   ... -MinFiles 18  至少检查到这么多个文件，否则视为门禁空转并失败（默认 18）
# 退出码：0 = 全部通过；1 = 有违规。
param(
    [switch]$Quiet,
    [switch]$Fast,
    [int]$MinFiles = 18
)
$ErrorActionPreference = 'Continue'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$fail = 0
$warn = 0
$checked = 0

function Say([string]$text) { if (-not $Quiet) { Write-Host $text } }
function Bad([string]$text) { Write-Host ("  [FAIL] " + $text); $script:fail++ }
function Warn([string]$text) { Write-Host ("  [WARN] " + $text); $script:warn++ }

# PS 5.1 的解析器才是权威：如果本脚本被 pwsh 7 跑，就另起 powershell.exe 做解析
$runningOnPs5 = ($PSVersionTable.PSVersion.Major -le 5)
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$canParse51 = $runningOnPs5 -or (Test-Path -LiteralPath $psExe)
if ((-not $Fast) -and (-not $canParse51)) {
    # 明确降级告警：少了这一条，门禁就只剩「字节检查」，中文语法问题会再次漏网
    Warn '找不到 Windows PowerShell 5.1（powershell.exe），「PS 5.1 解析」这一项无法执行——门禁能力下降'
}

function Test-Parse51([string]$path) {
    if ($runningOnPs5) {
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors)
        if ($errors -and $errors.Count) { return $errors[0].Message }
        return $null
    }
    if (-not (Test-Path -LiteralPath $psExe)) { return $null }
    $out = & $psExe -NoProfile -Command "`$e=`$null; [void][System.Management.Automation.Language.Parser]::ParseFile('$path',[ref]`$null,[ref]`$e); if(`$e.Count){ Write-Output `$e[0].Message }" 2>&1
    $text = ($out -join ' ').Trim()
    if ($text.Length -gt 0) { return $text }
    return $null
}

function Get-Bytes([string]$path) { return [System.IO.File]::ReadAllBytes($path) }

function Test-HasBom([byte[]]$bytes) {
    return ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
}

function Test-HasNonAscii([byte[]]$bytes) {
    foreach ($b in $bytes) { if ($b -gt 0x7F) { return $true } }
    return $false
}

Say "=== 脚本编码/语法门禁 ==="

# ---- 1/2) PowerShell 脚本 ----
foreach ($f in Get-ChildItem -LiteralPath $dir -Filter *.ps1) {
    $checked++
    $bytes = Get-Bytes $f.FullName
    $nonAscii = Test-HasNonAscii $bytes
    $bom = Test-HasBom $bytes
    if ($nonAscii -and -not $bom) {
        Bad ("$($f.Name): 含非 ASCII 但没有 UTF-8 BOM —— PS 5.1 会按 ANSI 读，中文注释会变成语法错误")
        continue
    }
    if (-not $Fast) {
        $parseError = Test-Parse51 $f.FullName
        if ($parseError) { Bad ("$($f.Name): Windows PowerShell 5.1 解析失败 -> $parseError"); continue }
    }
    if (-not $Quiet) { Say ("  [ok]   " + $f.Name) }
}

# ---- 3) 批处理必须纯 ASCII ----
foreach ($f in @(Get-ChildItem -LiteralPath $dir -Filter *.cmd) + @(Get-ChildItem -LiteralPath $dir -Filter *.bat)) {
    $checked++
    $bytes = Get-Bytes $f.FullName
    if (Test-HasNonAscii $bytes) {
        $count = ($bytes | Where-Object { $_ -gt 0x7F } | Measure-Object).Count
        Bad ("$($f.Name): 含 $count 个非 ASCII 字节 —— cmd.exe 按 OEM 代码页解析，中文会直接报错（注释行也会被执行）")
    } elseif (-not $Quiet) { Say ("  [ok]   " + $f.Name) }
}

# ---- 4) ES 模块语法 ----
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
if (-not $node) { $node = 'C:\Program Files\nodejs\node.exe' }
if ($Fast) {
    Say "  [skip] -Fast: 跳过 .mjs 语法检查"
} elseif (-not (Test-Path -LiteralPath $node)) {
    # 找不到 node 只是"少查一项"，必须明确说出来——静默跳过会让门禁看起来比实际更强
    Warn "找不到 node.exe，跳过 .mjs 语法检查（门禁能力下降）"
} else {
    # v3.5：.mjs 现在分布在根目录、hooks\ 与 workbench\（边车与冒烟脚本）
    $modules = @(Get-ChildItem -LiteralPath $dir -Filter *.mjs -ErrorAction SilentlyContinue)
    foreach ($sub in @('hooks', 'workbench')) {
        $subDir = Join-Path $dir $sub
        if (Test-Path -LiteralPath $subDir) {
            $modules += @(Get-ChildItem -LiteralPath $subDir -Filter *.mjs -ErrorAction SilentlyContinue)
        }
    }
    if ($modules.Count -eq 0) { Warn "没有找到任何 .mjs（预期至少 dsh-launch.mjs / remote-proxy.mjs / hooks\compile-cache.mjs / workbench\session-reader.mjs）" }
    foreach ($f in $modules) {
        $checked++
        & $node --check $f.FullName 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Bad ("$($f.Name): node --check 失败") }
        elseif (-not $Quiet) { Say ("  [ok]   " + $f.Name) }
    }
}

# ---- 5) 配置必须纯 ASCII ----
$cfg = Join-Path $dir 'dsh-web.config'
if (Test-Path -LiteralPath $cfg) {
    $checked++
    if (Test-HasNonAscii (Get-Bytes $cfg)) {
        Bad 'dsh-web.config: 含非 ASCII —— PS 5.1（ANSI）与启动器（UTF-8）会读出不同结果'
    } elseif (-not $Quiet) { Say "  [ok]   dsh-web.config" }
}

# ---- 6) 源码版本 vs 已编译 exe 版本 ----
# 这一条专治「源码改了、exe 没重编」：本项目踩过两次（面板还跑 v3.1.1 时源码已是 v3.3）。
$cs = Join-Path $dir 'dsh-panel.cs'
$exe = Join-Path $dir 'dsh-panel.exe'
if (Test-Path -LiteralPath $cs) {
    $checked++
    $srcVersion = $null
    try {
        $text = [System.IO.File]::ReadAllText($cs)
        $m = [regex]::Match($text, 'AssemblyVersion\("([0-9]+(?:\.[0-9]+){1,3})"\)')
        if ($m.Success) { $srcVersion = $m.Groups[1].Value }
    } catch { }
    if (-not $srcVersion) {
        Warn 'dsh-panel.cs: 找不到 AssemblyVersion，跳过版本一致性检查'
    } elseif (-not (Test-Path -LiteralPath $exe)) {
        Warn 'dsh-panel.exe 不存在（首次构建前正常）；记得编译后再分发'
    } else {
        $exeVersion = $null
        try { $exeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion } catch { }
        if (-not $exeVersion) {
            Warn 'dsh-panel.exe: 读不到 FileVersion，跳过版本一致性检查'
        } elseif ($exeVersion.Trim() -ne $srcVersion.Trim()) {
            Bad ("dsh-panel.exe 版本 $exeVersion 与源码 AssemblyVersion $srcVersion 不一致 —— " +
                 "现在跑的是旧逻辑。运行 apply-panel-update.cmd（关面板→重编译→重开）")
        } elseif (-not $Quiet) { Say ("  [ok]   dsh-panel.exe v" + $exeVersion) }
    }
}

# ---- 7) v3.5：工作台界面（workbench/ui.html）----
# 这份文件是被 dsh-workbench.cs 运行时注入进 DSH 页面的，三个分节标记是两边共同的契约；
# 浏览器按 UTF-8 解析，但 C# 侧是按 UTF-8 读的固定路径/内嵌资源，所以：
#   a) 必须能按 UTF-8 严格解码（有坏字节 -> 界面直接空白，且没有任何报错）；
#   b) 三个标记必须齐全且非空（改坏标记 -> 注入内容是空字符串，界面同样不出现）；
#   c) 内嵌进 exe 的资源名必须还在（否则「源码能跑、单文件分发版空白」）。
$wbUi = Join-Path $dir 'workbench/ui.html'
if (Test-Path -LiteralPath $wbUi) {
    $checked++
    $uiText = $null
    try {
        $strict = New-Object System.Text.UTF8Encoding($false, $true)
        $uiText = $strict.GetString((Get-Bytes $wbUi))
    } catch {
        Bad 'workbench/ui.html: 不是合法 UTF-8（浏览器会显示空白/乱码）'
    }
    if ($uiText) {
        $sections = @('STYLE', 'MARKUP', 'SCRIPT')
        $missing = @()
        foreach ($s in $sections) {
            $begin = '<!--@' + $s + '@-->'
            $end = '<!--@/' + $s + '@-->'
            $a = $uiText.IndexOf($begin)
            $b = -1
            if ($a -ge 0) { $b = $uiText.IndexOf($end, $a + $begin.Length) }
            if ($a -lt 0 -or $b -lt 0 -or ($b - ($a + $begin.Length)) -lt 32) { $missing += $s }
        }
        if ($missing.Count -gt 0) {
            Bad ('workbench/ui.html: 分节标记缺失或内容为空 -> ' + ($missing -join ', ') +
                 '（注入顺序是 STYLE/MARKUP/SCRIPT，改坏后界面会整个不出现）')
        } elseif ($uiText.IndexOf('id="wb-root"', [System.StringComparison]::Ordinal) -lt 0) {
            Bad 'workbench/ui.html: 找不到 id="wb-root"（工作台根节点），注入后无法挂载'
        } elseif (-not $Quiet) { Say '  [ok]   workbench/ui.html' }
    }
} else {
    Bad 'workbench/ui.html 不存在 —— 构建脚本需要它（会被内嵌进 dsh-panel.exe）'
}

# ---- 8) v3.5：exe 内必须真的带上工作台界面 ----
if ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $wbUi)) {
    $checked++
    $found = $false
    try {
        $exeBytes = Get-Bytes $exe
        $needle = [System.Text.Encoding]::ASCII.GetBytes('dsh-workbench-ui.html')
        for ($i = 0; $i -le ($exeBytes.Length - $needle.Length); $i++) {
            if ($exeBytes[$i] -eq $needle[0]) {
                $okAll = $true
                for ($k = 1; $k -lt $needle.Length; $k++) {
                    if ($exeBytes[$i + $k] -ne $needle[$k]) { $okAll = $false; break }
                }
                if ($okAll) { $found = $true; break }
            }
        }
    } catch { }
    if (-not $found) {
        Bad ('dsh-panel.exe 里找不到内嵌资源 dsh-workbench-ui.html —— exe 是旧构建，' +
             'workbench/ui.html 删掉后工作台就没了。运行 build-dsh-panel.cmd 重编')
    } elseif (-not $Quiet) { Say '  [ok]   exe 内嵌工作台界面' }
}

Say ""
# ---- 空转保护：文件数明显少于预期，说明路径/过滤器出错，门禁其实什么都没查 ----
if ($checked -lt $MinFiles) {
    Write-Host ("门禁：只检查到 $checked 个文件（预期至少 $MinFiles 个）—— 很可能路径或过滤器出错，" +
                "门禁在空转。请确认在项目根目录运行，或放宽 -MinFiles。")
    exit 1
}
if ($fail -gt 0) {
    Write-Host ("门禁：$fail 项违规（共检查 $checked 个文件，$warn 项警告）。修好再提交/发布。")
    exit 1
}
if ($warn -gt 0) {
    Write-Host ("门禁：通过，但有 $warn 项警告（共检查 $checked 个文件）。")
} else {
    Write-Host ("门禁：全部通过（$checked 个文件）。")
}
exit 0
