# dsh-web 控制面板

DeepSeek Harness Web（http://127.0.0.1:3080）的 Windows 原生控制面板。
UI 零第三方框架：使用 .NET Framework 自带 `csc.exe` 编译 C#（WinForms 自绘界面）+ PowerShell 启动脚本。
v2.0 起内嵌 WebView2（Chromium），v3.0 起主界面完全网页化——打开即自动启动服务并全屏显示 DSH 界面，原控制功能全部收进顶栏与托盘。

## 快速开始

1. 双击 `dsh-panel.cmd`（首次会自动编译 `dsh-panel.exe`）
2. 打开即自动检测 DeepSeek Harness：已安装则自动启动服务并全屏内嵌显示 DSH 网页；
   未安装则显示安装指引页（`npm install -g @deepseek-ai/dsh`），安装完成后点击「重新检测」进入
3. 关闭窗口即最小化到系统托盘（服务继续运行）；托盘菜单可启停 / 开浏览器 / 退出

> 要求：Windows 10/11（需 WebView2 Runtime，随 Edge 自动更新；缺失时面板会提示，
> 并回退为打开系统浏览器）。

## 文件说明

| 文件 | 说明 |
|---|---|
| `dsh-panel.cs` | 面板主程序（C# 5.0 / WinForms，自绘界面 + 托盘 + 内嵌 WebView2） |
| `dsh-panel.exe` | 编译产物（须与 WebView2 三个 DLL 同目录：Core / WinForms / Loader） |
| `build-dsh-panel.cmd` | 编译脚本（csc /optimize+，引用 lib/ 的 WebView2 SDK，自动复制 loader） |
| `dsh-panel.cmd` | 双击启动器（缺 exe 时先编译） |
| `start-service.ps1` | 后台启动服务（node 直启、端口预检、日志轮转、node 自动探测） |
| `check-dsh.ps1` | 检测 @deepseek-ai/dsh 是否可用（npx 缓存 / 全局安装），结果写 run/dsh-check.txt |
| `stop-dsh.cmd` | 命令行停止服务，支持 `--dry-run` 预览 |
| `dsh-web.config` | 共享配置（当前仅 `port`，默认 3080） |
| `dsh-panel.manifest` | DPI 感知清单（PerMonitorV2） |
| `dsh-panel.ico` / `generate-icon.ps1` | 程序图标与生成脚本 |
| `install-autostart.cmd` / `uninstall-autostart.cmd` | 开机自启注册/注销（也可用托盘菜单勾选） |
| `run-tests.cmd` | 一键冒烟测试（编译 + 面板启动存活检查，不影响服务） |
| `lib/` | 内嵌浏览器 SDK（Microsoft.Web.WebView2，MIT）：Core/WinForms 托管程序集 + x64 原生 loader |
| `WebView2Loader.dll` | WebView2 原生代理（构建时从 lib/ 复制到 exe 旁） |
| `.gitignore` | 版本控制忽略规则（运行时产物、工具数据） |

## 目录结构

```
dsh-web/
├── dsh-panel.cs / dsh-panel.exe / dsh-panel.cmd   源码 / 产物 / 启动器
├── lib/                                           内嵌浏览器 SDK（WebView2，随仓库提交）
├── build-dsh-panel.cmd / run-tests.cmd            构建 / 测试
├── start-service.ps1 / stop-dsh.cmd               服务启停脚本
├── dsh-web.config                                 共享配置
├── README.md / .gitignore                         文档 / 忽略规则
├── logs/                                          运行时日志（自动创建）
│   ├── dsh-web.log         服务 stdout
│   ├── dsh-web.err.log     错误
│   └── dsh-web.selftest.log 自检报告
└── run/                                           运行时状态（自动创建）
    ├── dsh-web.pid         服务进程 PID
    └── webview2-data/      内嵌浏览器用户数据（会话保持）
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
run-tests.cmd    # 编译 + 面板 GUI 冒烟（不触碰正在运行的服务）
```

## 配置

`dsh-web.config`（纯 ASCII，`#` 为注释）：

```
port=3080
```

修改端口后，面板、启动脚本、停止脚本都会读取该值。

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
- 顶栏薄工具条：Logo+标题+URL / 服务状态胶囊（点击启停，悬停显示端口与运行时长）/ 设置按钮
- **设置页**：常规（开机自启、崩溃自动恢复、启动时自动启动服务——胶囊拨动开关，偏好持久化到注册表）/ 服务（端口、DSH 状态与重新检测、打开日志窗口/日志目录/系统浏览器、清除浏览器缓存）/ 关于（版本、依赖说明、项目主页）
- 活动日志为独立弹窗：时间戳彩色事件流（面板操作 + 服务输出），清空 / 复制 / 打开目录 / 自动滚动，显示运行时长与日志大小
- 服务停止/启动中网页显示占位页，恢复后自动回到真实页面；WebView2 缺失时回退系统浏览器
- 窗口支持最大化/全屏（Win11 Snap Layouts）；关闭按钮最小化到托盘
- 端口探测 300ms 硬超时，日志增量读取，全部后台线程，UI 零负担
- 服务进程树绑定 Job Object，停止时一次整树终止（失败自动回退）
- 单实例互斥：重复启动会激活已有窗口
- 启动失败 / 停止超时会写 err.log 并弹托盘气泡

## 排障

1. 服务起不来 → 查看 `logs\dsh-web.err.log`（面板日志区也会显示）
2. 端口被占 → `netstat -ano | findstr ":3080 "` 找监听者，或 `stop-dsh.cmd --dry-run`
3. 面板异常 → `dsh-panel.exe --selftest` 跑一遍自检，看 `logs\dsh-web.selftest.log`
4. 日志过大 → 启动时自动轮转（>5MB 保留一代 `dsh-web.log.1`）

## 版本历史

| 版本 | 要点 |
|---|---|
| v1.4 | 运行时产物目录化（logs/ run/）、run-tests、README 结构说明 |
| v1.5 | 活动日志面板：时间戳着色事件流 + 工具栏（清空/复制/打开目录/自动滚动）+ 运行信息行 |
| v2.0 | 内嵌 WebView2 网页视图：控制台/网页标签、服务就绪自动导航、占位页；修复 selftest 的 Job 绑定失效 |
| v3.0 | 网页化 UI：打开即自动启动服务并最大化显示内嵌网页；40px 顶栏（Logo/URL/导航/状态胶囊/日志）；日志独立弹窗；窗口恢复最大化 |
| v3.1 | 顶栏精简（去导航按钮）+ 设置页（偏好开关持久化、DSH 状态与重新检测、关于）+ 启动时 DSH 可用性检测与安装指引页 |
| v3.1.1 | 优化：检测不再读旧缓存、PID 上限放宽、stop-dsh 注释过滤、端口文案动态化；删除死代码（StatusLine 等）；开关重绘为胶囊拨动开关、清除浏览器缓存、检测超时提示、胶囊 tooltip、设置页浏览器端口检查；UI 圆角体系统一 |

> 版本号以程序集版本（dsh-panel.cs 顶部 `AssemblyVersion`）为准。
