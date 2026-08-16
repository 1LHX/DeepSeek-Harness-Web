# dsh-web 控制面板

DeepSeek Harness Web（http://127.0.0.1:3080）的 Windows 原生控制面板。
零第三方依赖：使用 .NET Framework 自带 `csc.exe` 编译 C#（WinForms 自绘界面）+ PowerShell 启动脚本。

## 快速开始

1. 双击 `dsh-panel.cmd`（首次会自动编译 `dsh-panel.exe`）
2. 点击「启动服务」→ 服务就绪后自动打开浏览器
3. 关闭窗口即最小化到系统托盘（服务继续运行）；托盘菜单可启停 / 开浏览器 / 退出

## 文件说明

| 文件 | 说明 |
|---|---|
| `dsh-panel.cs` | 面板主程序（C# 5.0 / WinForms，自绘界面 + 托盘） |
| `dsh-panel.exe` | 编译产物 |
| `build-dsh-panel.cmd` | 编译脚本（csc /optimize+，自动生成缺失的图标） |
| `dsh-panel.cmd` | 双击启动器（缺 exe 时先编译） |
| `start-service.ps1` | 后台启动服务（node 直启、端口预检、日志轮转、node 自动探测） |
| `stop-dsh.cmd` | 命令行停止服务，支持 `--dry-run` 预览 |
| `dsh-web.config` | 共享配置（当前仅 `port`，默认 3080） |
| `dsh-panel.manifest` | DPI 感知清单（PerMonitorV2） |
| `dsh-panel.ico` / `generate-icon.ps1` | 程序图标与生成脚本 |
| `install-autostart.cmd` / `uninstall-autostart.cmd` | 开机自启注册/注销（也可用托盘菜单勾选） |
| `run-tests.cmd` | 一键冒烟测试（编译 + 面板启动存活检查，不影响服务） |
| `.gitignore` | 版本控制忽略规则（运行时产物、工具数据） |

## 目录结构

```
dsh-web/
├── dsh-panel.cs / dsh-panel.exe / dsh-panel.cmd   源码 / 产物 / 启动器
├── build-dsh-panel.cmd / run-tests.cmd            构建 / 测试
├── start-service.ps1 / stop-dsh.cmd               服务启停脚本
├── dsh-web.config                                 共享配置
├── README.md / .gitignore                         文档 / 忽略规则
├── logs/                                          运行时日志（自动创建）
│   ├── dsh-web.log         服务 stdout
│   ├── dsh-web.err.log     错误
│   └── dsh-web.selftest.log 自检报告
└── run/                                           运行时状态（自动创建）
    └── dsh-web.pid         服务进程 PID
```

运行时产物与源码分离（v1.4 起）；旧版本留在根目录的日志/PID 会在面板启动时自动迁移。

## 构建

```bat
build-dsh-panel.cmd
```

要求：Windows 7+（需 .NET Framework 4.x，系统自带）、Node.js（标准安装布局或可被 PATH/注册表探测到）。

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

- 端口探测 300ms 硬超时，日志增量读取，全部后台线程，UI 零负担
- 服务进程树绑定 Job Object，停止时一次整树终止（失败自动回退）
- 单实例互斥：重复启动会激活已有窗口
- 启动失败 / 停止超时会写 err.log 并弹托盘气泡

## 排障

1. 服务起不来 → 查看 `logs\dsh-web.err.log`（面板日志区也会显示）
2. 端口被占 → `netstat -ano | findstr ":3080 "` 找监听者，或 `stop-dsh.cmd --dry-run`
3. 面板异常 → `dsh-panel.exe --selftest` 跑一遍自检，看 `logs\dsh-web.selftest.log`
4. 日志过大 → 启动时自动轮转（>5MB 保留一代 `dsh-web.log.1`）
