# dsh-web 控制面板

DeepSeek Harness Web（http://127.0.0.1:3080）的 Windows 原生控制面板。
UI 零第三方框架：使用 .NET Framework 自带 `csc.exe` 编译 C#（WinForms 自绘界面）+ PowerShell 启动脚本。
v2.0 起内嵌 WebView2（Chromium），v3.0 起主界面完全网页化——打开即自动启动服务并全屏显示 DSH 界面，原控制功能全部收进顶栏与托盘。
v3.5 起顶栏多一个「工作台」（快捷键 F2）：文件、终端、任务、变更、Git 五个页签以抽屉形式叠在 DSH 页面上
（注入式 Shadow DOM，不改 DSH 自己的 DOM/CSS），面板由此从「控制面板」变成「能干活的工作台」。

## 快速开始

1. 双击 `dsh-panel.cmd`（首次会自动编译 `dsh-panel.exe`）
2. 打开即自动检测 DeepSeek Harness：已安装则自动启动服务并全屏内嵌显示 DSH 网页；
   未安装则显示安装指引页（`npm install -g @deepseek-ai/dsh`），安装完成后点击「重新检测」进入
3. 关闭窗口即最小化到系统托盘（服务继续运行）；托盘菜单可启停 / 开浏览器 / 退出

> 要求：Windows 10/11（需 WebView2 Runtime，随 Edge 自动更新；缺失时面板会提示，
> 并回退为打开系统浏览器）。

## 开发约定（不变量，改代码前先看这一节）

这个项目里最贵的 bug 都是**「写的时候看不出、跑起来才炸」**的类型，所以有两条硬约定。
违反它们不会被编译器发现，但会让服务静默起不来——所以有门禁兜着。

| 文件类型 | 规则 | 违反后的症状 |
|---|---|---|
| `.ps1` | 含中文（任何非 ASCII）**必须**带 UTF-8 BOM | PS 5.1 按 ANSI 读，一条中文注释就让整份脚本语法错误：服务静默起不来、日志一个字都没有、面板只弹"启动失败" |
| `.cmd` / `.bat` | **只写 ASCII**（中文放到 `.ps1` 里） | cmd.exe 按 OEM 代码页解析，报「xxx 不是内部或外部命令」，严重时把注释行当命令执行 |
| `.mjs` | 改完先 `node --check` | 启动器 / 远程代理静默退出，表现为"没有反应" |
| `dsh-web.config` | **只写 ASCII** | PS 5.1（ANSI）与启动器（UTF-8）读出不同结果，端口/开关时灵时不灵 |
| `dsh-panel.cs` / `dsh-workbench.cs` | 改完必须重编 exe；版本号一起改（`AssemblyVersion` 与 `AssemblyFileVersion` 都要） | 「源码改了、exe 没重编」→ 所有验证都通过，但跑的是旧逻辑（本项目发生过两次） |
| `workbench/*.mjs` / `workbench/ui.html` | `.mjs` 过 `node --check`；`ui.html` 必须 UTF-8，且 `<!--@STYLE@-->` / `<!--@MARKUP@-->` / `<!--@SCRIPT@-->` 三段标记齐全（脚本里别用反引号模板串） | 注入内容为空或解析错位 → 工作台整个界面不出现，控制台也不报错（本项目真踩过：转义漏吃掉一个 `/`，`</div>` 变成 `<//div>`，界面被 HTML 解析器吞掉） |

**验证方式**（三条防线，从快到慢）：

1. **面板启动自检**（v3.4.2，自动、毫秒级）：检查上面 1/2/5 条，结果写
   `run/boot-timing.log` 的 `selfcheck:` 一行，并进活动日志。只报告，不阻塞启动——
   脚本坏了的时候面板本身必须还能用，用户才有救火入口。
2. **`check-scripts.ps1`**（手工/维护时）：7 类编码/结构检查 + PS 5.1 解析 + `node --check`
   + 版本一致性 + 工作台界面分段标记/内嵌资源 + 空转保护（检查到的文件数少于 `-MinFiles` 直接判失败）。
   `apply-panel-update.cmd` 会**阻塞性地**先跑它，且**在关闭面板之前**跑，
   避免"门禁失败但面板已经被关了"。
3. **`run-tests.cmd`**：门禁 → 编译 → GUI 冒烟三步。

> 为什么自检不阻塞、而 apply-panel-update 阻塞：前者在用户每次启动的路径上，任何阻塞都
> 会放大成"打不开"；后者是维护动作，宁可失败也别把坏脚本重新打包一遍。

## 文件说明

根目录保持「平铺」是刻意的：这里每个 `.cmd` 都是给用户双击的入口（README 里写着「双击哪个」），
分到子目录会让「拷贝即用」这件事多一层寻路。所以只把**源码/界面/工具**收进子目录。

| 文件 | 说明 |
|---|---|
| **主程序** | |
| `dsh-panel.cs` | 面板主程序（C# 5.0 / WinForms，自绘界面 + 托盘 + 内嵌 WebView2） |
| `dsh-workbench.cs` | v3.5 工作台桥接：文件读写/预览/搜索、终端进程、git、基线快照与 diff、会话边车管理（唯一允许起进程的地方） |
| `dsh-panel.exe` | 编译产物（须与 WebView2 三个 DLL 同目录：Core / WinForms / Loader） |
| `dsh-panel.cmd` | 双击启动器（缺 exe 时先编译） |
| `dsh-panel.ico` / `dsh-panel.manifest` / `generate-icon.ps1` | 程序图标（9 尺寸，蓝底 + 白色「›_」标记）/ DPI 感知清单（PerMonitorV2）/ 图标生成脚本（改完重编 exe） |
| `lib/` | 内嵌浏览器 SDK（Microsoft.Web.WebView2，MIT）：Core/WinForms 托管程序集 + x64 原生 loader |
| `Microsoft.Web.WebView2.*.dll`、`WebView2Loader.dll`（根目录副本） | 构建产物：从 `lib/` 复制到 exe 旁（CLR 只在 exe 目录找），`.gitignore` 忽略 |
| **工作台（v3.5）** | |
| `workbench/ui.html` | 工作台界面（单文件 HTML+CSS+JS，构建时内嵌进 exe，运行时注入 DSH 页面） |
| `workbench/session-reader.mjs` | 会话事件流边车：解 zstd 帧、抽轮次卡（turn/start|end + tool/call）、todo 清单、排队消息（任务看板的数据源；缺失时其余页签照常用） |
| `workbench/smoke.mjs` | 工作台自动化冒烟（CDP 驱动：挂载/桥接/五页签/轮次卡/抽屉宽度/终端策略/编辑-应用-回滚，20 项；需临时打开 `debugport`） |
| **构建与测试** | |
| `build-dsh-panel.cmd` | 编译脚本（csc /optimize+，引用 lib/ 的 WebView2 SDK，内嵌 `workbench/ui.html`，自动复制 loader） |
| `apply-panel-update.cmd` | 改完 `.cs` 后一键「门禁 → 关面板 → 重编译 → 重开」（服务不停） |
| `check-scripts.ps1` | **脚本编码/语法门禁**：`.ps1` 含中文必须有 BOM 且能被 PS 5.1 解析、`.cmd` 必须纯 ASCII、`.mjs` 过 `node --check`、`dsh-web.config` 必须纯 ASCII、`workbench/ui.html` 必须合法 UTF-8 且三段标记齐全、exe 必须内嵌 `dsh-workbench-ui.html`（`run-tests.cmd` 第一步就会调它） |
| `run-tests.cmd` | 门禁 → 编译 → GUI 冒烟三步（不动正在运行的服务） |
| **服务启停** | |
| `start-service.ps1` | 后台启动服务（唯一权威：试次阶梯、端口顺延、npx 兜底、失败现场保留） |
| `dsh-launch.mjs` | **快启动路径**：面板优先用它直连 `node bin.js`（省掉 PowerShell 冷启动 ~1.4 s）；任何意外同步转交 `start-service.ps1` |
| `stop-dsh.cmd` | 命令行停止服务，支持 `--dry-run` 预览 |
| `check-dsh.ps1` | 检测 @deepseek-ai/dsh 是否可用（文件系统优先，npm 仅兜底），结果写 run/dsh-check.txt |
| `check-update.ps1` | 比对已装 dsh 与 npm 最新版（24 小时节流），结果写 run/update-check.txt |
| `dsh-web.config` | 共享配置（`port` 端口、可选 `compilecache` / `workspace` / `debugport`，纯 ASCII） |
| `hooks/compile-cache.mjs` | 可选的 V8 编译缓存钩子（`compilecache=1` 时由 `node --import` 注入） |
| **远程访问（默认关闭）** | |
| `start-remote.cmd` / `start-remote.ps1` | 令牌鉴权的远程/局域网入口（前台运行，关窗口即收回） |
| `remote-proxy.mjs` | 代理本体：HTTP + WebSocket 透传、口令校验、内网来源限制 |
| **诊断与维护** | |
| `check-boot-perf.ps1` | 启动耗时诊断（只读，`-BootTest` 会在 3099 端口真实引导一次） |
| `show-timing.cmd` | **cmd 里直接跑**的分段耗时速查（不用 PowerShell）：最后一条 PORT READY、面板打点、入口/降级、排除项状态 |
| `clean-npx-cache.ps1` | 清理 npx 缓存历史目录（默认演练，`-Apply` 才删） |
| `defender-exclusions.ps1` | Defender 排除路径管理（**只针对 `npm-cache\_npx` 一条**；`-Add` / `-Remove` / `-Status` / `-DryRun`，结果写 `run/defender-exclusions.log`） |
| `add-defender-exclusions.cmd` / `remove-defender-exclusions.cmd` | 上面脚本的双击入口（自动提权） |
| `install-autostart.cmd` / `uninstall-autostart.cmd` | 开机自启注册/注销（也可用托盘菜单勾选） |
| **文档** | |
| `README.md` / `.gitignore` | 本文件（唯一的权威文档）/ 版本控制忽略规则（运行时产物、工具数据） |

## 目录结构

```
dsh-web/
├── dsh-panel.cs / dsh-panel.exe / dsh-panel.cmd   源码 / 产物 / 启动器
├── dsh-workbench.cs                               工作台桥接（文件/终端/git/变更/diff/边车）
├── workbench/                                     工作台三件套
│   ├── ui.html                                    界面（构建时内嵌进 exe，运行时注入 DSH 页面）
│   ├── session-reader.mjs                         会话事件流边车（任务看板的数据来源）
│   └── smoke.mjs                                    CDP 冒烟（17 项，需临时开 debugport）
├── lib/                                           内嵌浏览器 SDK（WebView2，随仓库提交）
├── hooks/compile-cache.mjs                        可选 V8 编译缓存钩子
├── build-dsh-panel.cmd / apply-panel-update.cmd   构建 / 改码后重建并重启面板
├── start-service.ps1 / stop-dsh.cmd               服务启停脚本
├── check-dsh.ps1                                  依赖检测（文件系统优先）
├── defender-exclusions.ps1 (+ 两个 .cmd)          Defender 排除路径
├── clean-npx-cache.ps1                            npx 缓存清理
├── check-boot-perf.ps1                            启动耗时诊断
├── dsh-web.config                                 共享配置
├── README.md / .gitignore                         文档 / 忽略规则
├── logs/                                          运行时日志（自动创建）
│   ├── dsh-web.log         服务 stdout
│   ├── dsh-web.err.log     错误（启动失败时还会留 .failed1/.failed2 每次尝试的输出）
│   └── dsh-web.selftest.log 自检报告
└── run/                                           运行时状态（自动创建）
    ├── dsh-web.pid         服务进程 PID
    ├── boot-timing.log     启动分段耗时（面板 + start-service 共写）
    ├── node-compile-cache/ 编译缓存（仅 compilecache=1 时产生，可随时删除）
    ├── webview2-data/      内嵌浏览器用户数据（会话保持）
    └── workbench/          工作台状态：state.json（工作区）/ baseline/（基线快照）/ tmp/（临时脚本）
```

运行时产物与源码分离（v1.4 起）；旧版本留在根目录的日志/PID 会在面板启动时自动迁移。

## 构建

```bat
build-dsh-panel.cmd
```

要求：Windows 10/11 64 位（需 .NET Framework 4.x，系统自带）、Node.js（标准安装布局或可被 PATH/注册表探测到）、WebView2 Runtime。
`lib/` 已随仓库提交（WebView2 SDK 1.0.x，MIT），无需联网下载。

## 自检

```bat
dsh-panel.exe --selftest
```

全程无界面：停止现有服务 → 启动 → 等待端口就绪 → 停止 → 验证进程树清理与 err.log。
结果写入 `logs\dsh-web.selftest.log`。**注意：自检会先停止 3080 上正在运行的服务。**

## 测试

```bat
run-tests.cmd    # 门禁 + 编译 + 面板 GUI 冒烟（不触碰正在运行的服务）
```

工作台是「跑起来才知道对不对」的部分（界面注入 + 桥接 + 子进程），所以带了一个可重复的冒烟脚本：

```bat
:: 1) 先在 dsh-web.config 里临时打开 debugport=9222，重启面板
node workbench\smoke.mjs        :: 20 项：挂载 / 桥接 / 五页签 / 轮次卡 / 抽屉宽度 / 终端策略 / 编辑-应用-回滚 / Git
:: 2) 验完把 debugport 注释掉，再重启面板
```

它只读工作区，唯一写入的是临时文件 `wb-scratch-smoke.txt`（最后用 `changes.revert` 删掉）。
失败项会逐条打印出来；退出码 0/1 可直接接进 CI 或批处理。

## 配置

`dsh-web.config`（纯 ASCII，`#` 为注释）：

```
port=3080
# compilecache=1     # 可选：开启 V8 编译缓存（默认关闭，见下节）
```

修改端口后，面板、启动脚本、停止脚本都会读取该值，且 `start-service.ps1` 会把它作为
`--port` 显式传给 dsh（v3.2 起；早期版本只有 dsh 的默认值 3080 会真正生效）。

v3.5 起还认两个可选键（都不是必需）：

| 键 | 作用 |
|---|---|
| `workspace=<绝对路径>` | 工作台的工作区（默认 = 面板所在目录，也就是 DSH 的工作目录）。中文路径请用界面里的「📁 切换工作区」选，别写进这个文件（它必须保持纯 ASCII） |
| `debugport=9222` | 打开内嵌浏览器的 CDP 调试端口（**默认关闭，仅供开发/自动化巡检**；开着时本机任意进程都能连上并驱动工作台桥接） |

## 启动提速（v3.2 / v3.3）

冷启动（开机后第一次打开面板）慢的根因不是某处 bug，而是**冷态成本被串行叠加**：
PowerShell 启动 → 两次 `npm` 探测 → `npx`（等于再启动一遍 npm）→ node 加载上万个
JS 小文件（首次触碰还要过实时保护扫描）→ WebView2 冷启动 → 前端 1.2 MB + 38 个插件
bundle。

### 本机实测（2026-10-29，同一台机器，重启后各测一次）

| 阶段 | 改造前（v3.1.1） | 改造后（v3.3 + `_npx` 排除） |
|---|---|---|
| 双击 → 窗口出现 | ~1 s | **0.37 s** |
| → 找到 dsh 入口（文件系统探测） | 无此步 | 0.73 s |
| → WebView2 就绪 | ~1 s | 1.06 s |
| → 启动脚本开始跑（冷启动 PowerShell） | 5.1 s（串行等检测） | 2.51 s（与检测并行） |
| → 拉起 node | 0.88 s | 2.98 s |
| **→ 端口就绪（node 引导本身）** | **46.2 s** | **2.87 s** |
| **→ 页面开始加载（合计）** | **约 52 s** | **5.84 s** |

结论：那 46.2 秒里几乎全部是「首次打开 `_npx` 下 3.2 万个包文件时每个 open 都要过一遍
实时保护过滤驱动」，排除该路径后同一段降到 2.87 秒（16 倍）。剩下的 5.84 秒构成：
面板 0.37 s + 文件系统探测 0.37 s + WebView2 0.33 s + **冷启动 PowerShell 1.45 s** +
脚本到 spawn 0.47 s + node 引导 2.87 s。

本版本做的四件事（不动用户的 A 项「开机自启预热」，那项收益最大但由用户自行决定）：

1. **直连 node bin.js**：`start-service.ps1` 直接在 npx 缓存 / 全局 npm / node 旁定位
   `@deepseek-ai/dsh/lib/bin.js` 并启动，跳过 `npx`（实测热态 1.0 s → 0.12 s，冷态省更多，
   且不再依赖 registry）；找不到入口才回退 `npx --yes --prefer-offline`。
2. **启动与检测并行**：面板先用纯文件系统探测（`FindDshBinJs`，不起进程、毫秒级）判断
   dsh 是否就位，就位立刻起服务；`check-dsh.ps1` 只决定是否显示安装指引，不再压着启动
   （该脚本也从「npm 优先」改为「文件系统优先」，实测 1.2 s → 0.42 s）。
3. **过渡期快速轮询**：启动/停止期间端口轮询由 2 s 收紧到 300 ms，就绪后页面最多早 1.7 s
   出现；空闲态仍为 2 s。
4. **可观测**：`run/boot-timing.log` 记录 `panel-shown` / `dsh-entry-found` / `service-spawned`
   / `port-ready` / `webview2-ready` / `navigate` 及每一试次的结果，`start-service.ps1`
   写同一文件，慢在哪一段一眼可见。

### 快启动路径 `dsh-launch.mjs`（v3.4）

面板现在优先用 **node** 跑 `dsh-launch.mjs` 来启动服务，而不是先起 PowerShell：

| | PowerShell 路径（v3.2/v3.3） | 快启动路径（v3.4） |
|---|---|---|
| 面板 → 服务进程被拉起 | 1.92 s（含 PS 冷启动 1.45 s） | **0.12 s** |

启动器只做两件事：定位 `bin.js` 直连、按需挂上编译缓存钩子。**任何意外都同步转交
`start-service.ps1`**（找不到入口 / 配置端口被占 / 拉起后提前退出 / 任何异常），
所以「试次阶梯、端口顺延、npx 兜底、失败现场保留」始终只有一套实现，也绝不会重复拉起服务。
`ShellExecute` 失败时面板同样回退原来的 PowerShell 路径。

写这一层时踩到的两个 Windows 细节，记下来免得以后重踩：

1. **子进程必须 `detached: true`**。非 detached 时，启动器一退出服务就静默死亡——两者共用
   同一控制台/进程组，父进程退出触发控制台关闭事件，Node 把它映射成 SIGHUP，而 dsh 只处理
   SIGINT/SIGTERM，于是被默认动作杀掉（实测子进程死在启动器退出后 0.2 s 内，日志无任何输出）。
2. **PowerShell 不能在没有控制台的环境里起**。`detached: true` 或 `CREATE_NO_WINDOW`
   （`windowsHide`）都会让它静默不启动；`cmd /c start /b` 也会随父进程控制台销毁而失效。
   所以转交时用 `spawnSync` 让 PowerShell 以**子进程共享本进程控制台**的方式跑完
   （代价是罕见情况下多留一个隐藏 node 进程 1~2 分钟，换「不会静默失败」）。
3. 服务的 stdout/stderr 用**文件描述符**传递（`stdio: ['ignore', fd, fd]`），不是管道——
   启动器退出后服务仍能正常写日志；管道会在读端关闭后 EPIPE 把服务写死。

启动安全网（都是「宁可少优化，也不能起不来」）：

- `start-service.ps1` 把启动做成**试次阶梯**：`入口+编译缓存` → `入口` → `npx`，
  只有「进程已退出且端口从未就绪」才升级到下一试次；仍在运行的进程绝不重复拉起；
  每次失败尝试的 stdout/stderr 分别留存为 `dsh-web.err.log.failed1/.failed2`。
- 端口预检失败立即报错，不会先起进程再撞 `EADDRINUSE`。
- 编译缓存钩子（`compilecache=1`）先用 `node --import ... -e 0` 探测，探测失败自动降级；
  缓存在启动脚本侧只注入本进程，不污染子进程环境。
- **编译缓存默认关闭**：本机实测热态引导「开 1.89 s / 关 1.89 s」，收益为零，说明等待
  并不在 V8 编译上；保留开关供别的机器验证，默认路径不受它影响。

要再往下压冷启动，按收益排序：① 开机自启常驻（`install-autostart.cmd`，把冷启动挪到你
不等待的时候）；② `add-defender-exclusions.cmd`（管理员，需 UAC 确认）；③
`clean-npx-cache.ps1 -IncludeOldDsh -Apply`（减少扫描面）。

### 关于 Defender 排除：只做一条，以及为什么

实测冷态 46.2 秒、热态 1.4 秒，差距几乎全在「首次打开 `_npx` 下那 3.2 万个包文件、每次 open
都要过一遍实时保护过滤驱动」。所以只排除 **`%LOCALAPPDATA%\npm-cache\_npx`** 这一条；
原先清单里的另外三条（`C:\Program Files\nodejs`、`~/.dsh`、`dsh-web` 目录）实测收益≈0
或代价不划算，已从默认清单移除。

代价写清楚：恶意 npm 包（抢注/投毒/postinstall）正是先落在这个缓存里，排除后它被写入、
被执行都不再被扫描。对冲手段——这个目录本身是可丢弃的缓存：怀疑有问题就整个删掉，下次由
npm 重新下载（`npm cache verify` 可校验完整性）。排除项不改 ACL、不关实时保护，
SmartScreen / 防火墙 / ASR / 漏洞利用防护都不受影响；`remove-defender-exclusions.cmd` 可随时回滚，
应用过程与结果记录在 `run/defender-exclusions.log`。

## 启动诊断

```bat
powershell -NoProfile -ExecutionPolicy Bypass -File check-boot-perf.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File check-boot-perf.ps1 -BootTest
```

只读；`-BootTest` 会在 3099 端口真实引导一次并自动结束，不碰正在运行的服务。
结果与 `run/boot-timing.log` 对照即可判断瓶颈在哪一段。

## 可靠性（v3.3）

| 能力 | 行为 |
|---|---|
| 端口冲突 | 配置端口被别的程序占用时，服务自动顺延到 `port+1 … port+20` 中第一个空闲端口，真实端口写入 `run/dsh-web.port`；面板在校验「监听进程 PID == `run/dsh-web.pid`」后才采纳，残留文件绝不会把面板指向别人的服务。20 个都占满则快速失败并说明尝试过的范围 |
| 重复启动 | 启动前先看 `run/dsh-web.port`：该端口若正由自己的 dsh 提供，直接退出 0（这也避免了两个进程抢同一份 `dsh-web.err.log`） |
| 健康检查 | 每 5 秒 `GET /`（**绕过系统代理**），耗时与连续失败次数进日志与胶囊 tooltip。**只在端口关闭时**才判定崩溃——长任务期间 HTTP 短暂无应答属正常，绝不用它触发重启 |
| 崩溃自动恢复 | 默认关闭（托盘/设置页可开）。开启后按 5/15/30/60/120/300 秒指数退避重启，最多 6 次；只有「连续健康 120 秒」才把退避计数归零，避免「刚起来又挂」被误判为恢复成功 |
| 日志占用 | 启动时若 `dsh-web.log`/`dsh-web.err.log` 被其它进程占着，自动改用时间戳文件名，不会因此起不来 |
| 更新提示 | 启动 12 秒后在后台跑 `check-update.ps1`（结果按 24 小时节流写 `run/update-check.txt`）：有新版只**提示**并发托盘气泡，不自动升级；`dsh-web.config` 写 `updatecheck=0` 可关掉 |

`stop-dsh.cmd` 也会优先读 `run/dsh-web.port`，端口顺延后照样能停干净（停止时一并删除该文件）。

## 远程 / 手机访问（默认关闭，v3.3）

上游 `dsh` **故意**只绑 `127.0.0.1` 且没有鉴权（`--host 0.0.0.0` 会被它直接拒绝），因为一个无鉴权的
DSH 端点等于把本机的代码执行能力交出去。所以这里的做法是：**在回环服务前面加一道显式的、
带口令的门**，默认不开、不由面板自动启动。

```bat
start-remote.cmd                 :: 生成/复用口令，前台启动代理；关窗口即收回入口
start-remote.cmd -Port 3081      :: 换监听端口
powershell -NoProfile -ExecutionPolicy Bypass -File start-remote.ps1 -NewToken   :: 换口令
```

- 口令存在 `run/remote-token.txt`（首次自动生成 48 位十六进制，`run/` 不进版本库）。
- 手机在同一局域网打开 `http://<本机局域网 IP>:3081/?token=<口令>`：先兑换成 Cookie 再 302 回干净 URL，
  之后地址栏不再带口令；也可以直接粘贴到登录页。
- 代理同时透传 **HTTP 与 WebSocket**——DSH 浏览器端的 mux/host 事件流走 WebSocket，
  只代理 HTTP 会出现「页面能开、但永远收不到事件」。
- 转发时把 `Host`/`Origin`/`Referer` 改写成回环地址（服务的信任栅栏只认回环来源），
  并且**不转发你自己的 Cookie**（口令不会漏给上游）。
- 默认只接受内网来源地址（127/8、10/8、172.16/12、192.168/16、169.254/16、IPv6 本地），
  公网暴露必须显式 `-AllowPublic`。
- 口令校验用 SHA-256 摘要 + `timingSafeEqual`；连续失败会按 IP 递增延迟（上限 2 秒）压制爆破。

> ⚠ 持有口令 = 能在本机执行命令（agent 有 shell 权限）。只在可信网络用，用完关窗口。
> 「手机只读模式」**没有实现**：DSH 的 UI 一切都是 POST，代理层无法在不理解 RPC 语义的前提下
> 可靠地只放行读操作，做一个假的只读不如不做。

### 为什么不做成 Windows 服务（设计取舍，非能力缺失）
同类项目（WinSW / NSSM / node-windows）能把任意 exe 变成服务，但 dsh 的服务进程**必须活在当前
用户会话里**：它读写 `%USERPROFILE%\.dsh`（会话、插件树、凭据）、用用户的 `PATH` 找 node、
用用户的桌面目录做工作区。注册成 Session 0 的系统服务会变成一个**不同的实例**——用不上已有会话与
配置，还得为服务账号另配登录信息（`obj= / password`），对单用户本地开发场景是纯负担。

因此这里刻意选择「用户会话内托管 + 面板常驻托盘 + 面板退出服务不停」这条路：
- 面板的 Job Object **没有** `KILL_ON_JOB_CLOSE`，面板被关掉/杀掉时 node 服务照常运行；
- 想跨登录常驻，用 `install-autostart.cmd`（或用托盘/设置页勾「开机自启」）——
  这就是本项目对应的「服务化」答案，且不涉及权限与账号问题。
- 真需要开机即用（登录前就有页面）时，正确的做法是**登录触发的计划任务**；
  该项属于用户自行决定的 A 项，本版本不内置。

## 停止服务

```bat
stop-dsh.cmd            # 正常停止（pid 文件进程树 + 端口监听者兜底）
stop-dsh.cmd --dry-run  # 预览将要终止的进程，不实际执行
```

## 托盘功能

- 图标随状态变色：绿点=运行中 / 灰点=未运行 / 琥珀点=启动或停止中
- 菜单：打开面板 / 启动·停止服务 / 打开浏览器 / 崩溃自动恢复（默认关）/ 开机自启 / 退出并停止服务 / 退出面板 / 关于

## 面板功能要点

- **网页化主界面**：打开即检测 DeepSeek Harness 并自动启动服务，窗口最大化，WebView2 内嵌 DSH 界面铺满窗口- **DSH 缺失指引**：检测不到 @deepseek-ai/dsh 时显示安装指引页（手动安装命令 + 重新检测），就绪后自动进入；检测结果区分「未安装 / 检测超时」，超时明确提示检查 Node.js
- 顶栏薄工具条：品牌蓝 Logo + 标题 + URL 浅蓝 chip / 服务状态胶囊（浅底深字，点击启停，悬停显示端口与运行时长）/ 工作台（v3.5）/ 设置按钮——配色与圆角见「界面设计规范」
- **设置页**：常规（开机自启、崩溃自动恢复、启动时自动启动服务——胶囊拨动开关，偏好持久化到注册表）/ 服务（端口、DSH 状态与重新检测、打开日志窗口/日志目录/系统浏览器、清除浏览器缓存）/ 关于（版本、依赖说明、项目主页）
- 活动日志为独立弹窗：时间戳彩色事件流（面板操作 + 服务输出），清空 / 复制 / 打开目录 / 自动滚动，显示运行时长与日志大小
- 服务停止/启动中网页显示占位页，恢复后自动回到真实页面；WebView2 缺失时回退系统浏览器
- 窗口支持最大化/全屏（Win11 Snap Layouts）；关闭按钮最小化到托盘
- 端口探测 300ms 硬超时，日志增量读取，全部后台线程，UI 零负担
- 服务进程树绑定 Job Object，停止时一次整树终止（失败自动回退）
- 单实例互斥：重复启动会激活已有窗口
- 启动失败 / 停止超时会写 err.log 并弹托盘气泡

## 工作台（v3.5 起；v3.6 加轮次卡与可调宽度）

顶栏「工作台」按钮（或页面里按 **F2**）拉起一个右侧抽屉，五个页签。
**抽屉宽度可以拖左边沿自由调整**（双击把手复位，宽度记在 localStorage），
遮罩只做视觉提示、不拦点击——开着工作台也能继续操作 DSH 页面，关闭走 ✕ / Esc / F2。

| 页签 | 能干什么 | 实现要点 |
|---|---|---|
| 文件 | 懒加载目录树、文件名搜索、按内容搜索、文本/图片预览、复制路径、**@ 引用直接插进 DSH 输入框** | 全部走桥接，路径一律锁在工作区根目录内 |
| 终端 | PowerShell 5.1 / cmd 执行命令、实时输出、命令历史（↑↓）、停止、cwd 跟随 | **命令白名单 + 危险命令二次确认**在 C# 侧判定（不在 JS 里） |
| 任务 | 目标条 + 三列看板（待规划 / 进行中 / 已完成）+ 会话活动流；**每轮对话自动生成一张轮次卡**，Agent 自己维护的清单与排队消息叠在上面 | **直接读 DSH 会话事件流**里的 `todo/write`、`goal/change`、`tool/call` |
| 变更 | 基线快照对比：修改/新增/删除清单、行级 diff、接受 / 回滚 / 编辑并预览后 Apply / 重设基线 | 基线存在 `run/workbench/baseline/`，只收文本文件（≤512KB） |
| Git | 当前分支、HEAD、暂存/未暂存/未跟踪计数、最近提交、单文件 diff | 只读，不改仓库状态 |

### 任务看板为什么能「自动生成任务卡」

DSH 把每一次会话事件追加进
`%USERPROFILE%\.dsh\sessions\<编码后的cwd>\session-<id>\session.jsonl.zstd`——注意它是**一串独立的
zstd 帧**（每次追加一个新帧，Node 的 `zstdDecompressSync` 只解第一帧）。`workbench/session-reader.mjs` 按魔数
切帧、逐帧解压，把这几类记录翻成看板：

**为什么不再只认 `todo_write`**：v3.5 的看板只读 Agent 的清单，实测踩过——Agent 某一轮不写清单时
「待规划 / 进行中」永远是空的，看起来像看板坏了。v3.6 起骨架是**轮次卡**（一定会有的东西）：

| 会话记录 | 工作台怎么用 |
|---|---|
| `turn/start` / `turn/end` | 轮次卡：一轮一张。目标是你在 DSH 里发的那句话，卡上带「第几轮 / 工具数 / 用时 / 最近 3 个工具」；跑着的是「进行中」，结束的落「已完成」 |
| `todo/write` | Agent **自己维护**的清单（pending / in_progress / completed），叠在轮次卡之上；超过 2 分钟没更新会提示「清单已过期，以轮次卡为准」 |
| `agent/inbox/spliced` | 排队到下一轮的消息（插件注入的通知不算）落「待规划」 |
| `tool/call` / `tool/result` | 卡片里的工具条目 + 下方活动流：在跑什么命令、读改哪个文件、成没成 |
| `goal/change` / `session/title` | 没有轮次目标时的目标条 / 会话标题兜底 |

所以这里没有「猜」：轮次卡来自真实的 `turn/start|end`，卡里的工具来自真实的 `tool/call`。
边车由面板启动（`node workbench/session-reader.mjs --cwd <工作区>`），只用 Node 内置能力、零依赖；
面板退出时它的 stdin 关闭，边车自己退出。

### 终端策略（白名单 / 危险确认）

判定规则写在 `dsh-workbench.cs`（`TermPolicy`），不依赖界面：

1. 先匹配**危险模式**（`rm -rf`、`del /f`、`Remove-Item -Recurse`、`git reset --hard`、
   `format`、`diskpart`、`shutdown`、`reg delete`、`Set-ExecutionPolicy` …）→ 红字二次确认；
2. 再逐段（`;` `&&` `||` `|` 分隔）检查**首命令**是否在白名单（`git` `npm` `node` `Get-*`
   `Select-*` `dir` `type` …）→ 全段都白名单才直接执行；
3. 其余一律「需确认」，含输出重定向的也让人看一眼。

命令下发方式：PowerShell 用 `-EncodedCommand`（UTF-16LE Base64），彻底绕开引号/中文转义地狱；
cmd 写成临时 `.cmd`（`chcp 65001`）再执行。每条命令末尾打一个 `__WB_CWD__<路径>` 标记，
面板据此更新终端的 cwd（所以 `cd` 是有效的），标记本身不会漏到输出里。
输出上限 900KB（超出丢弃并提示），PowerShell 关掉进度条（否则重定向下会冒出 CLIXML 垃圾）。

### 变更审阅（Diff / Apply）的语义与边界

- 基线 = 「我上次认可的工作区状态」。第一次打开「变更」页会自动建立基线（`run/workbench/baseline/`：
  `manifest.json` + 每个文件一份内容副本），之后 Agent 改了文件就会出现在清单里。
- 单个文件可以看行级 diff，然后 **接受**（基线更新为该文件当前内容）或 **回滚**（写回基线内容；
  基线里没有的「新增文件」回滚 = 删掉）。
- 「编辑」按钮打开一个编辑器：改完先 **预览差异**，确认后 **应用**（`fs.save`：先写临时文件再
  `File.Replace`，不会留半截文件）。删掉大半个文件时会额外弹一次确认。
- **做不到「Agent 写入前拦住」**：DSH 没有 PreToolUse 这类钩子，`tool/call` 落进会话日志时文件
  已经写完了。所以这里是「写完立刻可审阅、可一键回滚」，不是写入闸门——别把它当审批流用。
- 只跟踪文本文件（≤512KB）、跳过 `.git`、`node_modules`、`bin`、`obj`、`dist` 等噪声目录，
  以及面板自己的 `run/`、`logs/`（它们每次运行都在变）。二进制/超大文件不进基线。

### 安全边界（本项目老约定的变更，写清楚原因）

v3.4 及以前的面板**自身不启动任何进程**（服务启动委托给 `start-service.ps1`）。v3.5 破了这条约定，
原因很直接：终端和 git 必须执行程序，C# 里只有 `Process.Start` 一条路。所以把口子收在一处：

- **只有 `dsh-workbench.cs` 会起进程**，且仅四类：固定脚本的会话边车（`workbench/session-reader.mjs`）、
  用户敲进终端的命令、`git` 只读命令、`explorer` 定位文件；
- 命令策略与危险模式在 C# 侧（界面只是显示），改 JS 绕不过去；
- 所有文件操作强制落在工作区根目录内（越界直接拒绝，见 `Resolve`/`IsUnder`）；
- 终端进程与面板同生命周期：面板退出时 Job/Dispose 一并清理。

> ⚠ 工作台 = 能在本机执行命令。和 `start-remote.cmd` 一样，别在不可信环境里开着它。

### 开发用：CDP 巡检开关

`dsh-web.config` 里取消注释 `debugport=9222` 可让内嵌浏览器开远程调试端口，之后就能用
CDP（`http://127.0.0.1:9222/json/list`）脚本化验证界面与桥接（本项目就是这样跑冒烟测试的）。
**默认关闭**：开着的时候本机任何进程都能连上这个 WebView，等于拿到工作台的桥接口（写文件、执行命令）。

## 界面设计规范（v3.7）

整套界面（WinForms 顶栏 / 设置页 / 日志窗口 / 托盘 + 工作台抽屉 + 占位页 + 应用图标）共用一套 token：
只有**蓝 + 白**一个主色系，形状一律圆角。改配色只改 `dsh-panel.cs` 的 `Theme` 段，
别在控件里写死颜色（v3.6 之前散落着 47 处写死的颜色，是这次统一的重点）。

### 调色板

| 角色 | 值 | 用在哪 |
|---|---|---|
| 品牌蓝 | `#2F6BFF`（hover `#1E57E7` / press `#1545C6`） | Logo、主按钮、开关、选中态 |
| 浅蓝底 | `#EAF1FF` | URL chip、选中按钮底、目标条 |
| 深蓝字 | `#173D8D` | 浅蓝底上的文字 |
| 页面底 | `#F7FAFF` | 窗口底、日志与设置页背景 |
| 卡片 | `#FFFFFF` | 卡片、顶栏、按钮常态底 |
| 线 | `#E2EBF7`（强调 `#CEDCF0`） | 1px 边框、分隔线、输入框 |
| 文字 | `#152238` / `#364863` / `#697B96` / `#93A3BB` | 强 / 正文 / 次 / 弱 |
| 状态 | 绿 `#109E60` · 琥珀 `#E68A0C` · 红 `#DB4444` · 灰 `#94A3B8` | 只用于状态点与状态胶囊 |

状态色不铺大面积：状态胶囊是「浅底 + 同色深字 + 实心点」（`Ui.Soft()` / `Ui.Ink()`），
所以顶栏在运行 / 停止时都不会出现一大块饱和色。

### 圆角与间距

| token | 值 | 用在哪 |
|---|---|---|
| `RadiusCard` | 16 | 卡片、看板列、目标条 |
| `RadiusButton` | 12 | 主按钮、次按钮、输入框、终端输入 |
| `RadiusSmall` | 9 | 顶栏小按钮、chip、列表行 |
| 胶囊 | 高的一半 | 状态胶囊、开关轨道、浮动入口 |
| `TopBarHeight` | 46 | 顶栏高度（原 40，加高一点更透气） |

所有可见形状都走 `Ui.FillRound` / `Ui.StrokeRound` / `Ui.DrawSoftCard` / `Ui.Chip`；
没有一处用矩形画按钮或卡片（只有 1px 分隔线用矩形——那是线不是面）。

### 图标与标记

- **应用图标**（`dsh-panel.ico`，9 个尺寸 16→256）、**Header Logo**、**托盘图标**、**工作台品牌标**是同一枚标记：
  蓝色圆角方块 + 白色 `>_` 提示符（终端与对话的合体）。全部圆头线段绘制，16px 下也不糊。
- 托盘图标在其上把右下角换成状态点（绿=运行 / 琥珀=切换中 / 灰=停），并加一圈白边，任何桌面底色都看得清。
- 按钮几何图标（启动 / 停止 / 浏览器）统一 1.6–2.8px 圆头描边；`Play` 用圆角描边把三角形磨圆，没有尖角。
- 重新生成图标：`powershell -NoProfile -ExecutionPolicy Bypass -File generate-icon.ps1`（改完记得重编 exe）。

### 想改主题时

1. 只改 `dsh-panel.cs` 的 `Theme`（颜色 / 圆角 / 间距），以及 `workbench/ui.html` 里 `--wb-*` 那组变量；
2. 图标改 `generate-icon.ps1` 并重跑（几何与 `Ui.DrawLogo` 保持一致）；
3. 跑 `build-dsh-panel.cmd` + `check-scripts.ps1`，工作台再跑 `node workbench/smoke.mjs`。
## 排障

1. 服务起不来 → 查看 `logs\dsh-web.err.log`（面板日志区也会显示）；
   启动脚本会为每次失败的尝试留一份 `dsh-web.err.log.failed1/.failed2`
2. 端口被占 → `netstat -ano | findstr ":3080 "` 找监听者，或 `stop-dsh.cmd --dry-run`
3. 面板异常 → `dsh-panel.exe --selftest` 跑一遍自检，看 `logs\dsh-web.selftest.log`
4. 日志过大 → 启动时自动轮转（>5MB 保留一代 `dsh-web.log.1`）
5. 启动慢 → 在 **cmd** 里直接跑 `show-timing.cmd`（不用 PowerShell），它会把最后一条
   `PORT READY`、面板各阶段打点、入口/降级、排除项状态一次列出来；需要更细的对照再跑
   `check-boot-perf.ps1`
6. 改过 `dsh-panel.cs` 后 exe 用不了 → 运行中无法覆盖自身镜像，
   用 `apply-panel-update.cmd`（关面板 → 重编译 → 重开，服务不停）
7. **脚本「看起来没问题但就是不起作用」** → 先跑 `powershell -NoProfile -ExecutionPolicy Bypass
   -File check-scripts.ps1`。三类都是编码问题，编译期看不出来：
   - `.ps1` 含中文但没 UTF-8 BOM：Windows PowerShell 5.1 按 ANSI 读，一条中文注释就能让整个
     脚本语法错误（本项目真的踩过：`start-service.ps1` 坏掉时表现为「服务静默起不来」，
     日志里一个字都没有，面板只弹一句启动失败）；
   - `.cmd` 里出现中文：cmd.exe 按 OEM 代码页解析，会报「xxx 不是内部或外部命令」，
     严重时把注释行当命令执行（本项目 `show-timing.cmd` 第一版就这样）；
   - `.mjs` 语法错误：启动器/代理会静默退出，表面上像是「没有反应」。

   **判定口诀**：`.ps1` 有中文 ⇒ 必须有 BOM；`.cmd` 一律只写 ASCII；`.mjs` 改完先
   `node --check`；`dsh-web.config` 一律只写 ASCII（两边解码方式不同）。

8. **工作台不出现 / 只有一个空抽屉** → 先看 `run/boot-timing.log` 里有没有 `wb-inject: N chars`：
   - 没有 → `workbench/ui.html` 读不到或三段标记坏了（门禁会报），面板活动日志里也会有 Warn；
   - 有但界面是空的 → `<!--@MARKUP@-->` 段被改坏（例如 JS 里出现未转义的 `</div>` 之类），
     用 `check-scripts.ps1` 复查；
   - 任务看板一直空 → 边车挂了：活动日志里搜「会话边车」，或手动
     `node workbench/session-reader.mjs --cwd <工作区>` 看它能不能找到会话文件（会话目录名是**编码后的 cwd**，
     工作区换过就可能对不上，改工作区时面板会重启边车）。
9. **设置页状态一直是「检测中…」** → v3.7.1 已修（旧版检测完不刷新标签）。若仍出现，看活动日志里有没有 `正在重新检测 DeepSeek Harness…` 且 30 秒内有没有 `DeepSeek Harness 已就绪`：没有的话说明 `check-dsh.ps1` 没跑完，手动执行 `powershell -NoProfile -File check-dsh.ps1` 看 `run/dsh-check.txt` 是否写入。
10. **终端点了没输出 / 停不下来** → 危险命令会停在「确认执行」那一步（红条），不点它不会执行；
   「停止」杀的是整棵进程树之外的单进程（PowerShell 起的子进程可能残留，重跑一次即可）。

## 版本历史

| 版本 | 要点 |
|---|---|
| v1.4 | 运行时产物目录化（logs/ run/）、run-tests、README 结构说明 |
| v1.5 | 活动日志面板：时间戳着色事件流 + 工具栏（清空/复制/打开目录/自动滚动）+ 运行信息行 |
| v2.0 | 内嵌 WebView2 网页视图：控制台/网页标签、服务就绪自动导航、占位页；修复 selftest 的 Job 绑定失效 |
| v3.0 | 网页化 UI：打开即自动启动服务并最大化显示内嵌网页；40px 顶栏（Logo/URL/导航/状态胶囊/日志）；日志独立弹窗；窗口恢复最大化 |
| v3.1 | 顶栏精简（去导航按钮）+ 设置页（偏好开关持久化、DSH 状态与重新检测、关于）+ 启动时 DSH 可用性检测与安装指引页 |
| v3.1.1 | 优化：检测不再读旧缓存、PID 上限放宽、stop-dsh 注释过滤、端口文案动态化；删除死代码（StatusLine 等）；开关重绘为胶囊拨动开关、清除浏览器缓存、检测超时提示、胶囊 tooltip、设置页浏览器端口检查；UI 圆角体系统一 |
| v3.2 | 启动提速：start-service 直连 node bin.js（npx 兜底）+ 启动分段打点 + 试次阶梯与失败现场保留；`--port` 显式传递（修掉 config 端口不生效的隐患）；面板启动与依赖检测并行、过渡期 300ms 轮询；可选 V8 编译缓存钩子（实测无收益，默认关）；新增 defender-exclusions / clean-npx-cache / check-boot-perf / apply-panel-update |
| v3.3 | 可靠性：端口冲突自动顺延 + 面板按 PID 校验采纳真实端口 + 重复启动直接复用；崩溃自动恢复改指数退避（5→300 秒）并以「连续健康 120 秒」复位；HTTP 健康检查（绕过代理，仅观测不重启）；日志被占用时自动改名；stop-dsh/check-boot-perf 跟随实际端口；新增更新提示（check-update.ps1，只提示不自动升级）与令牌鉴权的远程访问（start-remote.cmd + remote-proxy.mjs，HTTP+WebSocket 透传，默认关闭） |
| v3.4.1 | 端口采纳收紧：`SyncActivePort` 现在**必须**有可验证的 pid 才采纳 `run/dsh-web.port`（此前 pid 读不到时只要求端口在监听，「残留 port 文件 + 别的程序占了该端口」会让内嵌浏览器导航到别人的服务）；pid 文件失效时一并删除端口记录。新增 `check-scripts.ps1` 编码/语法门禁并接入 `run-tests.cmd` 第一步 |
| v3.7.1 | 修复设置页「重新检测」点了之后一直停在「检测中…」：检测本身 1 秒就返回（`run/dsh-check.txt` 写入 `installed`），但三处检测入口各写一遍、没有一处刷新设置页标签。现在统一走 `BeginDshCheck()`——防重入（连点不再起一堆 PowerShell）、检测中把按钮置灰并显示「检测中…」、结果回来经 `RefreshDshStatusLabels()` 刷新标签；已用 UI 自动化真点一次验证：`已就绪 → 检测中… → 已就绪` |
| v3.7 | **界面统一**：整套 UI 收敛到蓝 + 白单一色系与大圆角——`Theme` 重写（品牌蓝 #2F6BFF、浅蓝底/深蓝字、冷蓝灰线与四级文字），顶栏白底 + 蓝色品牌标 + URL 浅蓝 chip，状态胶囊改成「浅底 + 同色深字 + 实心点」，按钮/开关/卡片/输入框统一圆角与边框，占位页与工作台同步换色；应用图标、Header Logo、托盘图标、工作台品牌标统一成同一枚「›_」标记（圆头线段、无尖角）；新增「界面设计规范」一节 |
| v3.6 | **工作台二轮**：任务看板改成「轮次卡」骨架（每轮对话一张卡，目标取你的消息，带工具数/用时/最近工具），Agent 的 todo 清单与排队消息叠在上面，修掉「待规划 / 进行中永远为空」；清单超 2 分钟未更新时提示以轮次卡为准；抽屉**宽度可拖拽**（双击复位、localStorage 记忆）、遮罩不再拦点击；修掉页签徽章用错根节点导致一直不更新的问题；冒烟扩到 20 项 |
| v3.5 | **工作台**：顶栏「工作台」+ 抽屉界面（`workbench/ui.html`，Shadow DOM 注入 DSH 页面，F2）：文件树/预览/搜索 + **@ 引用直接插入 DSH 输入框**、内嵌终端（PowerShell/cmd，命令白名单 + 危险命令二次确认 + 输出上限 + cwd 跟随 + 停止）、**任务看板**（会话事件流边车 `session-reader.mjs` 解 zstd 帧读 `todo/write`/`goal/change`/`tool/call`，三列 + 活动流）、变更审阅（基线快照 + 行级 diff + 接受/回滚/预览后 Apply）、Git 视图（分支/提交/未提交变更/diff）。桥接集中在 `dsh-workbench.cs`（唯一允许起进程的地方，路径锁工作区、策略在 C# 侧）；构建内嵌 UI 资源；门禁新增 ui.html 分节标记/UTF-8 与 exe 内嵌资源检查；可选 `debugport` 供 CDP 巡检 |
| v3.4.2 | 门禁加固：`-Fast` 模式、空转保护（`-MinFiles`）、源码/exe 版本一致性、PS 5.1 缺失时的显式降级告警；`apply-panel-update.cmd` 在**关面板之前**先跑门禁；面板新增启动自检（纯 .NET 字节检查，`run/boot-timing.log` 记 `selfcheck:`）——即使没人跑门禁，编码/版本问题也会当场上报 |

> 版本号以程序集版本（dsh-panel.cs 顶部 `AssemblyVersion`）为准。
