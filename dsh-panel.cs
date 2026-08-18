// dsh-panel.cs - DeepSeek Harness Web 控制面板（原生 C# 版）
// 编译：build-dsh-panel.cmd（使用 Windows 自带 csc.exe，无需安装任何东西）
// 设计：单窗口常驻面板 + 系统托盘；服务后台隐藏启动，活动日志实时显示；
//       端口轮询与日志读取全部在后台线程，UI 线程零负担，拖动/缩放为原生速度。
// 安全：本程序自身不调用任何进程启动 API——
//       服务启动委托给同目录 start-service.ps1（参数全部为常量）；
//       端口查询用 P/Invoke（GetExtendedTcpTable），进程结束用 Job Object / Process.Kill，
//       打开浏览器用 ShellExecuteW。
// v1.1：Job Object 整树终止；单实例互斥；托盘常驻；崩溃自动恢复（默认关）；
//       日志增量读取；轮询防重入；按钮零阻塞；npx/node 自动探测；dsh-web.config；
//       陈旧 pid 清理；程序集元数据。
// v1.2（界面）：Theme 主题色系统；自绘渐变头部（Logo + URL + 状态胶囊徽章）；
//       状态指示灯（光晕 + 启动/停止脉冲动画）；按钮几何图标 + 颜色平滑过渡 + 按下下沉；
//       日志卡片轻投影；布局/间距/字体统一。
// v1.3（体验）：托盘图标随服务状态变色；托盘菜单（开机自启/退出并停止/关于）；
//       托盘右键零阻塞；启动失败与停止超时气泡告警；selftest 增强。
// v1.4（结构）：运行时产物目录化（logs/ run/）；.gitignore 与版本控制；run-tests.cmd。
// v1.5（活动日志）：日志区升级为活动事件流——面板操作/状态事件 + 服务输出，
//       每行带时间戳并按类型着色（事件蓝/绿/琥珀/红，stdout 深灰，stderr 暗红）；
//       工具栏：清空视图 / 复制 / 打开日志目录 / 自动滚动开关；
//       动态信息行：服务已运行时长 + 日志大小。
// v2.0（内嵌网页）：不再依赖外部浏览器——主窗口新增「控制台 | 网页」标签页，
//       内嵌 WebView2（Chromium）加载 DSH 界面；服务就绪自动切到网页并导航；
//       服务停止/启动中显示主题占位页，恢复后自动回到真实页面；
//       首次切到网页标签时窗口自动放大；WebView2 运行时缺失时回退打开系统浏览器。
//       依赖：lib/（Microsoft.Web.WebView2 SDK，MIT）+ WebView2 Evergreen Runtime。
//       selftest 修复：置 Starting 状态使 Job Object 绑定/pid 等待生效（v1.4 起失效）。
// v3.0（网页化 UI）：主界面重构为「纯网页」——打开即自动启动服务并全屏显示内嵌网页；
//       顶栏 40px 薄工具条（Logo+标题+URL / 后退前进刷新浏览器 / 状态胶囊 / 日志按钮）；
//       窗口恢复最大化/全屏（MaximizeBox）；活动日志移至独立弹窗（LogWindow）；
//       删除「打开网页」按钮与「控制台|网页」标签栏；托盘保留全部功能。
// v3.1（设置与引导）：顶栏精简为 Logo+URL / 状态胶囊（启停）/ 设置按钮；
//       新增设置页（SettingsWindow）：开机自启、崩溃自动恢复、启动时自动启动服务
//       （偏好持久化到 HKCU 注册表）、DSH 包状态与重新检测、关于（版本/依赖/链接）；
//       启动时检测 @deepseek-ai/dsh 是否可用——缺失则显示手动安装指引页，
//       安装完成后点「重新检测」进入主界面（不内置下载）。
// 注意：csc v4.0.30319 只支持 C# 5.0，勿使用字符串插值 / ?. / 表达式体等新语法。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("DeepSeek Harness Web Panel")]
[assembly: AssemblyProduct("dsh-panel")]
[assembly: AssemblyDescription("Control panel for DeepSeek Harness Web")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("3.1.0.0")]
[assembly: AssemblyFileVersion("3.1.0.0")]

namespace DshPanel
{
    // ---------- 日志行类型（决定着色） ----------

    enum LogKind { Info, Success, Warn, Error, ServiceOut, ServiceErr }

    // ---------- 主题（集中配色，便于统一调整） ----------

    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(245, 247, 250);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(229, 231, 235);
        public static readonly Color TextStrong = Color.FromArgb(31, 41, 55);
        public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
        public static readonly Color TextFaint = Color.FromArgb(156, 163, 175);

        public static readonly Color Indigo = Color.FromArgb(79, 70, 229);
        public static readonly Color Violet = Color.FromArgb(124, 58, 237);

        public static readonly Color Green = Color.FromArgb(16, 185, 129);
        public static readonly Color GreenHover = Color.FromArgb(5, 150, 105);
        public static readonly Color GreenPress = Color.FromArgb(4, 120, 87);

        public static readonly Color Red = Color.FromArgb(239, 68, 68);
        public static readonly Color RedHover = Color.FromArgb(220, 48, 48);
        public static readonly Color RedPress = Color.FromArgb(185, 28, 28);

        public static readonly Color Blue = Color.FromArgb(59, 130, 246);
        public static readonly Color BlueHover = Color.FromArgb(37, 99, 235);
        public static readonly Color BluePress = Color.FromArgb(29, 78, 216);

        public static readonly Color Amber = Color.FromArgb(245, 158, 11);

        // 圆角体系（大圆角少棱角，各控件统一）
        public const int RadiusCard = 14;      // 大卡片（设置分区 / 日志卡片）
        public const int RadiusButton = 11;    // 主按钮
        public const int RadiusSmall = 8;      // 工具栏小按钮
    }

    // ---------- 绘制工具 ----------

    enum ButtonIcon { None, Play, Stop, Globe }

    static class Ui
    {
        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Darken(Color c, float factor)
        {
            return Color.FromArgb(c.A,
                (int)Math.Max(0, Math.Min(255, c.R * factor)),
                (int)Math.Max(0, Math.Min(255, c.G * factor)),
                (int)Math.Max(0, Math.Min(255, c.B * factor)));
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static bool Near(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 &&
                   Math.Abs(a.B - b.B) <= 2 && Math.Abs(a.A - b.A) <= 2;
        }

        // 自绘控件先铺满父容器底色，避免圆角外残留黑块
        public static void ClearBackground(Graphics g, Control c)
        {
            using (SolidBrush b = new SolidBrush(c.Parent != null ? c.Parent.BackColor : Theme.Bg))
            {
                g.FillRectangle(b, c.ClientRectangle);
            }
        }

        // 应用 Logo：渐变圆角方块 + 白色粗体 D
        public static void DrawLogo(Graphics g, int x, int y, int size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(x, y, size, size);
            using (GraphicsPath path = RoundedRect(r, Math.Max(8, size / 3)))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                new RectangleF(x, y, size, size), Theme.Indigo, Theme.Violet, 45F))
            {
                g.FillPath(brush, path);
            }
            using (Font f = new Font("Segoe UI", size * 0.5F, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                TextRenderer.DrawText(g, "D", f, r, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // 按钮几何图标（不依赖字体，绘制清晰）
        public static void DrawButtonIcon(Graphics g, ButtonIcon icon, int cx, int cy, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (icon == ButtonIcon.Play)
            {
                PointF[] tri = { new PointF(cx - 4.5F, cy - 6F), new PointF(cx - 4.5F, cy + 6F), new PointF(cx + 6.5F, cy) };
                using (SolidBrush b = new SolidBrush(color)) g.FillPolygon(b, tri);
            }
            else if (icon == ButtonIcon.Stop)
            {
                using (SolidBrush b = new SolidBrush(color))
                    g.FillRectangle(b, cx - 4.5F, cy - 4.5F, 9F, 9F);
            }
            else if (icon == ButtonIcon.Globe)
            {
                using (Pen p = new Pen(color, 1.5F))
                {
                    g.DrawEllipse(p, cx - 6F, cy - 6F, 12F, 12F);
                    g.DrawEllipse(p, cx - 2.6F, cy - 6F, 5.2F, 12F);   // 经线
                    g.DrawLine(p, cx, cy - 6F, cx, cy + 6F);          // 竖轴
                    g.DrawLine(p, cx - 6F, cy, cx + 6F, cy);          // 赤道
                }
            }
        }
    }

    static class Program
    {
        // 由 dsh-web.config 覆盖（Config.Load）
        internal static int Port = 3080;
        internal static string Url = "http://127.0.0.1:3080";

        internal static string ScriptRoot;
        internal static string PidFile;
        internal static string OutLog;
        internal static string ErrLog;
        static string SelfTestLog;

        internal enum OpState { Idle, Starting, Stopping }
        internal static OpState opState = OpState.Idle;
        internal static bool autoOpenPending;
        internal static volatile bool lastRunning;     // 最近一次轮询的服务状态（按钮/托盘零阻塞）
        internal static bool everRan;                  // 本会话内服务曾成功运行
        internal static bool manualStop;               // 用户手动停止（抑制自动恢复）
        internal static bool autoRestartEnabled;       // 崩溃自动恢复开关（默认关，托盘菜单切换）
        internal static int autoRestartCount;
        internal static long lastRestartTick;
        internal static long serviceUpSince;           // 服务本次运行起始时刻（Environment.TickCount）
        static IntPtr serviceJob = IntPtr.Zero;        // 启动时绑定的 Job Object（停止时整树终止）
        static Mutex singleMutex;
        internal static MainForm Instance;
        internal static Action onStopped;               // 停止完成后回调（「退出并停止服务」用）

        // 活动日志事件（任何线程可调，转发到 UI 线程渲染）
        internal static void LogEvent(LogKind kind, string text)
        {
            if (Instance != null)
            {
                try { Instance.AppendEvent(kind, text); } catch { }
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            ScriptRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            // 运行时产物目录化（v1.4）：logs/ 日志、run/ pid
            string logsDir = Path.Combine(ScriptRoot, "logs");
            string runDir = Path.Combine(ScriptRoot, "run");
            try { Directory.CreateDirectory(logsDir); } catch { }
            try { Directory.CreateDirectory(runDir); } catch { }
            PidFile = Path.Combine(runDir, "dsh-web.pid");
            OutLog = Path.Combine(logsDir, "dsh-web.log");
            ErrLog = Path.Combine(logsDir, "dsh-web.err.log");
            SelfTestLog = Path.Combine(logsDir, "dsh-web.selftest.log");

            MigrateLegacyRuntimeFiles();

            Config.Load(ScriptRoot);
            Url = "http://127.0.0.1:" + Port;

            if (args.Length > 0 && args[0] == "--selftest")
            {
                return RunSelfTest();
            }

            // 单实例：重复启动时激活已有窗口并退出
            bool createdNew;
            singleMutex = new Mutex(true, "Local\\DshPanel.SingleInstance", out createdNew);
            if (!createdNew)
            {
                try
                {
                    IntPtr h = FindWindowW(null, "DeepSeek Harness Web");
                    if (h != IntPtr.Zero) SetForegroundWindow(h);
                }
                catch { }
                return 0;
            }

            CleanupStalePid();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        // 陈旧 pid 文件清理：记录的进程已不存在时删除，避免后续误操作
        static void CleanupStalePid()
        {
            if (!File.Exists(PidFile)) return;
            string pidText = "";
            try { pidText = File.ReadAllText(PidFile).Trim(); } catch { }
            int pid;
            if (int.TryParse(pidText, out pid) && pid > 0 && pid < 100000)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    if (!p.HasExited) return;   // 进程存活：pid 文件有效
                }
                catch { }
            }
            try { File.Delete(PidFile); } catch { }
        }

        // 旧版本（v1.3 及以前）把运行时产物放在根目录：迁移到 logs/ 与 run/。
        // 目标文件已存在时跳过；移动失败（如日志被占用）静默，下次启动再试。
        static void MigrateLegacyRuntimeFiles()
        {
            string logsDir = Path.Combine(ScriptRoot, "logs");
            string runDir = Path.Combine(ScriptRoot, "run");
            string[] logNames = { "dsh-web.log", "dsh-web.err.log", "dsh-web.selftest.log" };
            foreach (string name in logNames)
            {
                string src = Path.Combine(ScriptRoot, name);
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(logsDir, name);
                try
                {
                    if (!File.Exists(dst)) File.Move(src, dst);
                }
                catch { }
            }
            string srcPid = Path.Combine(ScriptRoot, "dsh-web.pid");
            if (File.Exists(srcPid))
            {
                string dstPid = Path.Combine(runDir, "dsh-web.pid");
                try
                {
                    if (!File.Exists(dstPid)) File.Move(srcPid, dstPid);
                }
                catch { }
            }
        }

        // ---------- 共享配置（dsh-web.config） ----------

        static class Config
        {
            public static void Load(string dir)
            {
                string file = Path.Combine(dir, "dsh-web.config");
                if (!File.Exists(file)) return;
                try
                {
                    foreach (string rawLine in File.ReadAllLines(file))
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string value = line.Substring(eq + 1).Trim();
                        if (key == "port")
                        {
                            int p;
                            if (int.TryParse(value, out p) && p > 0 && p < 65536) Program.Port = p;
                        }
                    }
                }
                catch { }
            }
        }

        // ---------- npx-cli 自动探测（默认路径 + 注册表 + x86） ----------

        static string FindNpxCli()
        {
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nodejs", "node_modules", "npm", "bin", "npx-cli.js"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "nodejs", "node_modules", "npm", "bin", "npx-cli.js"));

            string[] roots = { @"SOFTWARE\Node.js", @"SOFTWARE\WOW6432Node\Node.js" };
            RegistryKey[] hives = { Registry.LocalMachine, Registry.CurrentUser };
            foreach (RegistryKey hive in hives)
            {
                foreach (string root in roots)
                {
                    try
                    {
                        using (RegistryKey key = hive.OpenSubKey(root))
                        {
                            if (key == null) continue;
                            string install = key.GetValue("InstallPath") as string;
                            if (!string.IsNullOrEmpty(install))
                            {
                                candidates.Add(Path.Combine(install, "node_modules", "npm", "bin", "npx-cli.js"));
                            }
                        }
                    }
                    catch { }
                }
            }

            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return candidates[0];   // 找不到时返回默认路径，由调用方给出明确报错
        }

        // ---------- v3.1：偏好持久化（HKCU 注册表） ----------

        const string PrefsKey = @"Software\DshPanel\Preferences";

        internal static bool GetPref(string name, bool def)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PrefsKey))
                {
                    if (key == null) return def;
                    object v = key.GetValue(name);
                    if (v is int) return ((int)v) != 0;
                }
            }
            catch { }
            return def;
        }

        internal static void SetPref(string name, bool val)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(PrefsKey))
                {
                    if (key != null) key.SetValue(name, val ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        // ---------- v3.1：DeepSeek Harness 可用性检测与安装 ----------
        // 与服务启动同架构：面板不直接执行命令，全部委托给同目录
        // check-dsh.ps1 / install-dsh.ps1（参数全常量），结果经 run/ 结果文件回报。

        internal static bool autoStartService = true;   // 启动时自动启动服务（设置页可关）

        // 启动脚本并轮询结果文件（后台线程；超时返回 false）
        static bool RunScriptWaitResult(string script, string resultFile, int timeoutMs, out string result)
        {
            result = null;
            try
            {
                string helper = Path.Combine(ScriptRoot, script);
                if (!File.Exists(helper)) return false;
                string parameters = "-NoProfile -ExecutionPolicy Bypass -File \"" + helper + "\"";
                IntPtr r = ShellExecuteW(IntPtr.Zero, "open", "powershell.exe", parameters, ScriptRoot, 0);
                if (r.ToInt64() <= 32) return false;
                long start = Environment.TickCount;
                while (Environment.TickCount - start < timeoutMs)
                {
                    if (File.Exists(resultFile))
                    {
                        try { result = File.ReadAllText(resultFile).Trim(); } catch { }
                        if (!string.IsNullOrEmpty(result)) return true;
                    }
                    Thread.Sleep(500);
                }
                return false;
            }
            catch { return false; }
        }

        // 异步检测（结果回调在后台线程；UI 侧自行转发）
        internal static void CheckDshAsync(Action<bool> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result;
                bool ok = RunScriptWaitResult("check-dsh.ps1", Path.Combine(ScriptRoot, "run", "dsh-check.txt"), 30000, out result);
                bool installed = ok && result == "installed";
                if (done != null)
                {
                    try { done(installed); } catch { }
                }
            });
        }

        internal static bool IsJobAttached()
        {
            return serviceJob != IntPtr.Zero;
        }

        // 托盘气泡通知（任何线程可调，内部转发到 UI 线程）
        internal static void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            if (Instance != null)
            {
                try { Instance.ShowBalloonTip(title, text, icon); } catch { }
            }
        }

        // 读取 pid 文件内容（无效时返回 0）
        internal static int ReadPid()
        {
            if (!File.Exists(PidFile)) return 0;
            string pidText = "";
            try { pidText = File.ReadAllText(PidFile).Trim(); } catch { }
            int pid;
            if (int.TryParse(pidText, out pid) && pid > 0 && pid < 100000) return pid;
            return 0;
        }

        // 资源管理器中定位日志文件
        internal static void OpenLogFolder()
        {
            try { ShellExecuteW(IntPtr.Zero, "open", "explorer.exe", "/select,\"" + OutLog + "\"", null, 1); } catch { }
        }

        // ---------- 端口探测 ----------

        internal static bool IsPortOpen()
        {
            TcpClient client = new TcpClient();
            try
            {
                // 异步连接 + 300ms 硬超时：某些网络过滤（如代理 TUN 模式）会静默丢弃
                // 回环 SYN，导致同步 Connect 最长阻塞约 2 秒——UI 线程绝不允许这种情况
                IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(300))
                {
                    return false;
                }
                client.EndConnect(ar);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        // P/Invoke 读取监听中的 TCP 表，按端口找进程 PID（不产生子进程）
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int TableClass, int Reserved);

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_LISTENER = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        static int FindPidByPort(int port)
        {
            try
            {
                int size = 0;
                if (GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0 && size <= 0) return 0;
                IntPtr table = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetExtendedTcpTable(table, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return 0;
                    int count = Marshal.ReadInt32(table);
                    int rowSize = Marshal.SizeOf(typeof(MibTcpRowOwnerPid));
                    IntPtr rowPtr = new IntPtr(table.ToInt64() + 4);
                    for (int i = 0; i < count; i++)
                    {
                        MibTcpRowOwnerPid row = (MibTcpRowOwnerPid)Marshal.PtrToStructure(rowPtr, typeof(MibTcpRowOwnerPid));
                        int localPort = (int)((row.localPort >> 8) | ((row.localPort & 0xFF) << 8));
                        if (localPort == port)
                        {
                            int pid = (int)row.owningPid;
                            if (pid > 0) return pid;
                        }
                        rowPtr = new IntPtr(rowPtr.ToInt64() + rowSize);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(table);
                }
            }
            catch { }
            return 0;
        }

        // ---------- Job Object（进程树终止） ----------

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint PROCESS_ALL_ACCESS = 0x001F0FFF;

        // 服务启动后（后台线程）：等待 pid 文件出现，把真实 node 进程绑定进 Job Object。
        // 之后由它派生的子进程（npx 启动的 dsh）自动加入该 Job，停止时一次整树终止。
        static void AttachServiceToJob()
        {
            for (int i = 0; i < 120; i++)
            {
                if (opState != OpState.Starting) return;
                string pidText = "";
                try { pidText = File.ReadAllText(PidFile).Trim(); } catch { }
                int pid;
                if (int.TryParse(pidText, out pid) && pid > 0)
                {
                    AttachPidToJob(pid);
                    return;
                }
                Thread.Sleep(500);
            }
        }

        static void AttachPidToJob(int pid)
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;
            IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, (uint)pid);
            if (hProc == IntPtr.Zero)
            {
                CloseHandle(job);
                return;
            }
            bool ok = false;
            try { ok = AssignProcessToJobObject(job, hProc); } catch { }
            CloseHandle(hProc);
            if (!ok)
            {
                CloseHandle(job);
                return;
            }
            IntPtr old = Interlocked.Exchange(ref serviceJob, job);
            if (old != IntPtr.Zero) CloseHandle(old);
        }

        // 终止某 PID 的进程树：优先 Job Object，失败回退 Process.Kill
        static void KillProcessTree(int pid)
        {
            if (pid <= 0) return;
            bool killed = false;
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job != IntPtr.Zero)
            {
                IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, (uint)pid);
                if (hProc != IntPtr.Zero)
                {
                    bool ok = false;
                    try { ok = AssignProcessToJobObject(job, hProc); } catch { }
                    CloseHandle(hProc);
                    if (ok)
                    {
                        try { killed = TerminateJobObject(job, 1); } catch { }
                    }
                }
                CloseHandle(job);
            }
            if (!killed)
            {
                try { Process.GetProcessById(pid).Kill(); } catch { }
            }
        }

        // ---------- 服务管理 ----------

        internal static void LaunchService()
        {
            if (IsPortOpen()) return;

            string npxCli = FindNpxCli();
            if (!File.Exists(npxCli))
            {
                throw new Exception("未找到 npx-cli.js（探测路径：默认安装布局 / 注册表 / ProgramFiles(x86)，最近尝试：" + npxCli + "）。\n请确认 Node.js 已安装，或修改 dsh-web.config / 重新安装 Node.js 后重试。");
            }
            string helper = Path.Combine(ScriptRoot, "start-service.ps1");
            if (!File.Exists(helper))
            {
                throw new Exception("未找到启动脚本：" + helper);
            }

            // 服务启动由同目录 start-service.ps1 完成（脚本内参数全部为常量），
            // 以隐藏窗口方式调用 powershell；本程序自身不调用任何进程启动 API。
            string parameters = "-NoProfile -ExecutionPolicy Bypass -File \"" + helper + "\"";
            IntPtr result = ShellExecuteW(IntPtr.Zero, "open", "powershell.exe", parameters, ScriptRoot, 0);
            long val = result.ToInt64();
            if (val <= 32)
            {
                throw new Exception("启动服务失败（ShellExecute 错误码 " + val + "），请查看 dsh-web.err.log");
            }
        }

        internal static void StopService()
        {
            // 0) 若有启动时绑定的 Job Object：一次终止整棵服务进程树
            IntPtr job = Interlocked.Exchange(ref serviceJob, IntPtr.Zero);
            if (job != IntPtr.Zero)
            {
                try { TerminateJobObject(job, 1); } catch { }
                CloseHandle(job);
            }
            // 1) 按 pid 文件兜底（v2 脚本写入真实 node PID）
            if (File.Exists(PidFile))
            {
                string pidText = "";
                try { pidText = File.ReadAllText(PidFile).Trim(); } catch { }
                int pid;
                if (int.TryParse(pidText, out pid) && pid > 0 && pid < 100000)
                {
                    KillProcessTree(pid);
                }
            }
            // 2) 兜底：结束端口监听进程（dsh），父链 npx -> node 会随之自然退出
            int listenerPid = FindPidByPort(Port);
            if (listenerPid > 0)
            {
                KillProcessTree(listenerPid);
            }
            // 3) 等待端口释放（最多 8 秒）
            for (int i = 0; i < 16; i++)
            {
                if (!IsPortOpen()) break;
                Thread.Sleep(500);
            }
            // 4) 停止超时告警：端口仍未释放说明可能有残留进程
            if (IsPortOpen())
            {
                string msg = "端口 " + Port + " 在停止后仍被监听，可能有残留进程。可重试停止，或使用 stop-dsh.cmd。";
                try { File.AppendAllText(ErrLog, "[stop-service] " + msg + Environment.NewLine, new UTF8Encoding(false)); } catch { }
                ShowBalloon("停止服务异常", msg, ToolTipIcon.Warning);
                LogEvent(LogKind.Warn, msg);
            }
            try { File.Delete(PidFile); } catch { }
        }

        // 异步启动：立即返回 UI，ShellExecuteW 等耗时操作在后台执行
        internal static void StartServiceAsync()
        {
            if (opState != OpState.Idle) return;
            opState = OpState.Starting;
            autoOpenPending = true;
            manualStop = false;
            LogEvent(LogKind.Info, "启动服务…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool failed = false;
                try
                {
                    LaunchService();
                    AttachServiceToJob();
                }
                catch (Exception ex)
                {
                    failed = true;
                    try { File.AppendAllText(ErrLog, "[start-service] " + ex.Message + Environment.NewLine, new UTF8Encoding(false)); } catch { }
                    ShowBalloon("启动失败", ex.Message, ToolTipIcon.Error);
                    LogEvent(LogKind.Error, "启动失败：" + ex.Message);
                }
                finally
                {
                    // 仅启动失败时由后台线程归位；成功路径保持 Starting，
                    // 由 PollTick 按“端口就绪 / 启动进程已退出”判定 Idle——
                    // 否则服务尚未就绪时状态提前回到 Idle，
                    // 表现为按钮闪回“启动服务”、提示闪回“未运行”。
                    if (failed)
                    {
                        opState = OpState.Idle;
                        RefreshUi();
                    }
                }
            });
        }

        // 异步停止：立即返回 UI，进程结束与端口释放等待在后台执行
        internal static void StopServiceAsync()
        {
            if (opState != OpState.Idle) return;
            opState = OpState.Stopping;
            autoOpenPending = false;
            manualStop = true;
            LogEvent(LogKind.Info, "停止服务…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                StopService();
                opState = OpState.Idle;
                RefreshUi();
                LogEvent(LogKind.Info, "服务已停止");
                Action cb = onStopped;
                onStopped = null;
                if (cb != null)
                {
                    try { cb(); } catch { }
                }
            });
        }

        internal static void RefreshUi()
        {
            if (Instance != null)
            {
                try { Instance.ForceRefresh(); } catch { }
            }
        }

        internal static void OpenBrowser()
        {
            try { ShellExecuteW(IntPtr.Zero, "open", Url, null, null, 1); } catch { }
        }

        // 打开外部链接（仅 http/https 公网地址；拒绝 localhost/环回/私有/保留地址）
        internal static void OpenExternal(string url)
        {
            try
            {
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return;
                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return;
                string host = u.DnsSafeHost;
                if (string.IsNullOrEmpty(host)) return;
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return;
                IPAddress addr;
                if (IPAddress.TryParse(host, out addr))
                {
                    if (IPAddress.IsLoopback(addr)) return;
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        byte[] b = addr.GetAddressBytes();
                        if (b[0] == 10 || b[0] == 127) return;                                   // 私有/环回
                        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return;                    // 私有
                        if (b[0] == 192 && b[1] == 168) return;                                 // 私有
                        if (b[0] == 169 && b[1] == 254) return;                                 // link-local
                        if (b[0] == 0 || b[0] >= 224) return;                                   // 保留/组播
                    }
                    else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal) return;               // 链路/站点本地
                    }
                }
                else
                {
                    string h = host.ToLowerInvariant();
                    if (h.EndsWith(".local") || h.EndsWith(".lan") || h.EndsWith(".internal")) return;
                }
                ShellExecuteW(IntPtr.Zero, "open", url, null, null, 1);
            }
            catch { }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpOperation, string lpFile, string lpParameters, string lpDirectory, int nShowCmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // ---------- 自检模式（--selftest）：启动 -> 就绪 -> 停止，全程无界面 ----------
        // v1.3 增强：验证 Job Object 绑定、pid 文件、进程树清理、err.log 是否有意外新增内容。

        static int RunSelfTest()
        {
            try { File.Delete(SelfTestLog); } catch { }
            int rc = 0;
            // err.log 基线（自检结束时对比新增内容）——必须在 try 外声明，finally 才能引用
            string errBefore = File.Exists(ErrLog) ? ReadAllTextShared(ErrLog) : "";
            try
            {
                if (IsPortOpen())
                {
                    SelfLog("WARN: 端口 " + Port + " 已有服务在监听，自检将先停止它");
                }
                StopService();
                Thread.Sleep(1500);
                SelfLog(IsPortOpen() ? "FAIL: 自检开始前端口仍未释放" : "PASS: 自检开始前端口空闲");
                if (IsPortOpen()) { SelfLog("SELFTEST exit=1"); return 1; }

                LaunchService();
                // selftest 直连底层方法，需手动置 Starting 状态——
                // AttachServiceToJob 依赖它轮询 pid 文件并绑定 Job Object
                opState = OpState.Starting;
                AttachServiceToJob();
                opState = OpState.Idle;
                SelfLog(IsJobAttached() ? "PASS: Job Object 已绑定服务进程树" : "WARN: Job Object 绑定失败（停止时将回退到按 PID/端口终止）");
                int pid = ReadPid();
                SelfLog(pid > 0 ? "PASS: pid 文件已写入 (pid=" + pid + ")" : "FAIL: pid 文件未写入");
                if (pid <= 0) rc = 1;

                bool ok = false;
                for (int i = 0; i < 90; i++)
                {
                    Thread.Sleep(1000);
                    if (IsPortOpen()) { ok = true; break; }
                }
                if (!ok)
                {
                    SelfLog("FAIL: 90 秒内端口 " + Port + " 未就绪");
                    rc = 1;
                }
                else
                {
                    SelfLog("PASS: 端口 " + Port + " 已就绪");
                    Thread.Sleep(1500);
                    string content = File.Exists(OutLog) ? ReadAllTextShared(OutLog) : "";
                    SelfLog("stdout 日志: " + (content.Trim().Length > 0 ? content.Trim() : "(空)"));
                    if (content.IndexOf("dsh web", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        SelfLog("WARN: 日志中未找到 dsh web 横幅");
                    }
                }

            }
            catch (Exception ex)
            {
                SelfLog("EXCEPTION: " + ex);
                rc = 1;
            }
            finally
            {
                StopService();
                Thread.Sleep(1500);
                SelfLog(IsPortOpen() ? "FAIL: 停止后端口仍在监听" : "PASS: 停止后端口已释放");
                if (IsPortOpen()) rc = 1;
                int pid = ReadPid();
                if (pid > 0)
                {
                    bool gone = false;
                    try { Process.GetProcessById(pid); } catch { gone = true; }
                    SelfLog(gone ? "PASS: 服务进程 (pid=" + pid + ") 已退出" : "FAIL: 服务进程 (pid=" + pid + ") 仍然存活");
                    if (!gone) rc = 1;
                }
                else
                {
                    SelfLog("PASS: pid 文件已清理");
                }
                // err.log 基线对比
                string errAfter = File.Exists(ErrLog) ? ReadAllTextShared(ErrLog) : "";
                if (errAfter != errBefore)
                {
                    string added = errAfter.Length > errBefore.Length ? errAfter.Substring(errBefore.Length) : errAfter;
                    added = added.Trim();
                    if (added.Length > 0)
                    {
                        SelfLog("WARN: 自检期间 err.log 新增内容:");
                        string[] lines = added.Split(new string[] { Environment.NewLine }, StringSplitOptions.None);
                        foreach (string line in lines)
                        {
                            if (line.Trim().Length > 0) SelfLog("    " + line);
                        }
                    }
                }
                else
                {
                    SelfLog("PASS: err.log 无新增内容");
                }
            }
            SelfLog("SELFTEST exit=" + rc);
            return rc;
        }

        static void SelfLog(string message)
        {
            File.AppendAllText(SelfTestLog, message + Environment.NewLine, new UTF8Encoding(false));
        }

        // 读取文本文件（允许其他进程同时写入：日志场景必须用 FileShare.ReadWrite，
        // .NET 默认的 FileShare.Read 在文件被写入进程占用时会打开失败）
        internal static string ReadAllTextShared(string file)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(fs, new UTF8Encoding(false), true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch { return ""; }
        }
    }

    // ---------- 增量日志尾部读取 ----------
    // 只读取新增字节（记住偏移），返回自上次调用以来的新行；
    // 文件被轮转/截断（变小）时从头重读；读取起点回退 4 字节，
    // 仅在回退区（前 <=4 字符）内找换行丢弃，避免 UTF-8 多字节截断与内容误丢。

    class LogTail
    {
        long offset = -1;        // -1 = 尚未初始化（首次整读）
        string pending = "";     // 上次结尾的不完整行，等待下次拼接

        public List<string> UpdateNew(string file)
        {
            List<string> newLines = new List<string>();
            if (!File.Exists(file)) return newLines;
            long len;
            try { len = new FileInfo(file).Length; } catch { return newLines; }
            if (offset < 0) { offset = 0; pending = ""; }
            if (len == offset) return newLines;
            if (len < offset) { offset = 0; pending = ""; }   // 被轮转/截断：从头部重读

            long start = offset > 4 ? offset - 4 : 0;
            long offsetBefore = offset;
            string chunk = "";
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(start, SeekOrigin.Begin);
                    int total = (int)(len - start);
                    byte[] buf = new byte[total];
                    int read = 0;
                    while (read < total)
                    {
                        int n = fs.Read(buf, read, total - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    chunk = Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch { return newLines; }
            offset = len;

            // 回退区处理：仅当本次读取包含回退区（offset>4 的增量读取）时才需要丢弃。
            // 在回退区（解码后前 <=4 个字符）内从后向前找换行：找到则丢弃到它为止；
            // 找不到说明回退区在行中间，保留全部（最多开头出现少量碎片，罕见且无害）。
            string newText = chunk;
            if (offsetBefore > 4)
            {
                int limit = Math.Min(4, chunk.Length);
                int nlInBackoff = -1;
                for (int i = limit - 1; i >= 0; i--)
                {
                    if (chunk[i] == '\n') { nlInBackoff = i; break; }
                }
                if (nlInBackoff >= 0)
                {
                    newText = chunk.Substring(nlInBackoff + 1);
                }
            }

            string combined = pending + newText;
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < combined.Length; i++)
            {
                char c = combined[i];
                if (c == '\r') continue;
                if (c == '\n')
                {
                    newLines.Add(cur.ToString());
                    cur.Length = 0;
                }
                else
                {
                    cur.Append(c);
                }
            }
            pending = cur.ToString();
            return newLines;
        }
    }

    // ---------- 主界面 ----------

    class MainForm : Form
    {
        GradientHeader header;
        TableLayoutPanel rootLayout;
        System.Threading.Timer pollTimer;
        bool closed;
        int pollBusy;
        bool lastPollRunning;

        LogTail outTail = new LogTail();
        LogTail errTail = new LogTail();

        // v2.0：内嵌网页（WebView2）
        WebView2 webView;
        bool webViewInited;          // EnsureCoreWebView2Async 已完成
        bool webViewFailed;          // 初始化失败（无运行时等）
        bool webViewBusy;            // 初始化进行中（防重入）
        bool webShowsApp;            // 当前显示真实页面（非占位页）
        string webPlaceholderKind;   // 占位页状态："starting" / "down"

        // v3.0：网页化 UI——顶栏状态胶囊 + 独立日志窗口
        StatusCapsule statusCapsule; // 服务状态胶囊（点击启停）
        LogWindow logWindow;         // 独立活动日志窗口（隐藏创建，点按钮弹出）

        // v3.1：设置与 DSH 检测引导
        SmallButton btnSettings;     // 打开设置页
        SettingsWindow settingsWindow; // 设置页（隐藏创建）
        Panel setupPanel;            // DSH 缺失时的提示引导页（覆盖网页区）
        RoundedButton btnSetupRecheck;
        Label setupTitle;
        Label setupDesc;
        Label setupStatus;
        bool dshInstalled;           // 检测结果：dsh 包可用
        bool dshChecked;             // 检测已完成

        // 设置页读取的检测状态
        public bool DshInstalled { get { return dshInstalled; } }
        public bool DshChecked { get { return dshChecked; } }

        NotifyIcon notify;
        ContextMenuStrip trayMenu;
        ToolStripMenuItem miToggle;
        ToolStripMenuItem miAutoRestart;
        ToolStripMenuItem miAutoStart;
        Icon lastTrayIcon;          // 动态生成的状态图标（替换时释放）
        Color lastTrayDot = Color.Transparent;
        bool balloonShown;
        bool reallyExit;

        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "DshPanel";

        public MainForm()
        {
            Program.Instance = this;
            Text = "DeepSeek Harness Web";
            ClientSize = new Size(440, 432);
            MinimumSize = new Size(800, 560);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            BackColor = Theme.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;

            BuildUi();
            SetupTray();

            // v3.0：隐藏创建日志窗口（事件直接渲染不丢失），点「日志」按钮弹出
            logWindow = new LogWindow();
            // v3.1：设置页（隐藏创建）
            settingsWindow = new SettingsWindow(this);

            Shown += delegate
            {
                pollTimer = new System.Threading.Timer(PollTick, null, 0, 2000);
                Program.LogEvent(LogKind.Info, "面板已启动 · 端口 " + Program.Port + " · " + Program.Url);
                Program.autoStartService = Program.GetPref("AutoStartService", true);
                EnsureWebViewAsync();
                SyncWebView(Program.lastRunning);
                // v3.1：先检测 DeepSeek Harness 是否可用——缺失则进入下载引导页，
                // 载入完成（或已就绪）后才自动启动服务
                Program.CheckDshAsync(delegate(bool ok)
                {
                    TryBeginInvoke(delegate { OnDshCheckResult(ok); });
                });
            };
            FormClosing += OnFormClosing;
            FormClosed += delegate
            {
                closed = true;
                if (pollTimer != null) pollTimer.Dispose();
                if (notify != null)
                {
                    notify.Visible = false;
                    notify.Dispose();
                    notify = null;
                }
                if (lastTrayIcon != null)
                {
                    lastTrayIcon.Dispose();
                    lastTrayIcon = null;
                }
                if (webView != null)
                {
                    try { webView.Dispose(); } catch { }
                    webView = null;
                }
                if (logWindow != null)
                {
                    try { logWindow.Dispose(); } catch { }
                    logWindow = null;
                }
                if (settingsWindow != null)
                {
                    try { settingsWindow.Dispose(); } catch { }
                    settingsWindow = null;
                }
            };
        }

        // 关闭按钮 = 最小化到托盘（服务不受影响）；托盘菜单「退出面板」才真正退出
        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !reallyExit)
            {
                e.Cancel = true;
                Hide();
                if (notify != null && !balloonShown)
                {
                    balloonShown = true;
                    try { notify.ShowBalloonTip(2500, "dsh-panel", "已最小化到系统托盘，服务仍在后台运行。", ToolTipIcon.Info); } catch { }
                }
            }
        }

        // ---------- 活动日志（v3.0 起转发到独立日志窗口） ----------

        public void AppendEvent(LogKind kind, string text)
        {
            if (logWindow != null)
            {
                logWindow.AppendEvent(kind, text);
            }
        }

        // ---------- 系统托盘 ----------

        void SetupTray()
        {
            notify = new NotifyIcon();
            notify.Icon = LoadTrayIcon();
            notify.Text = "DeepSeek Harness Web";
            notify.Visible = true;
            notify.DoubleClick += delegate { ShowPanel(); };

            trayMenu = new ContextMenuStrip();

            ToolStripMenuItem miOpen = new ToolStripMenuItem("打开面板");
            miOpen.Click += delegate { ShowPanel(); };

            ToolStripMenuItem miEmbed = new ToolStripMenuItem("显示面板");
            miEmbed.Click += delegate { ShowPanel(); };

            miToggle = new ToolStripMenuItem("启动服务");
            miToggle.Click += delegate
            {
                try
                {
                    if (Program.opState == Program.OpState.Idle)
                    {
                        if (Program.IsPortOpen()) Program.StopServiceAsync(); else Program.StartServiceAsync();
                        Program.RefreshUi();
                    }
                }
                catch { }
            };

            ToolStripMenuItem miBrowser = new ToolStripMenuItem("打开浏览器");
            miBrowser.Click += delegate
            {
                if (Program.IsPortOpen()) Program.OpenBrowser();
            };

            miAutoRestart = new ToolStripMenuItem("崩溃自动恢复");
            miAutoRestart.CheckOnClick = true;
            miAutoRestart.Click += delegate { Program.autoRestartEnabled = miAutoRestart.Checked; };

            miAutoStart = new ToolStripMenuItem("开机自启");
            miAutoStart.CheckOnClick = true;
            miAutoStart.Click += delegate { SetAutoStart(miAutoStart.Checked); };

            ToolStripMenuItem miExitAll = new ToolStripMenuItem("退出并停止服务");
            miExitAll.Click += delegate
            {
                reallyExit = true;
                if (Program.lastRunning)
                {
                    // 先异步停止服务，停止完成后退出面板
                    Program.onStopped = delegate
                    {
                        TryBeginInvoke(delegate { Application.Exit(); });
                    };
                    Program.StopServiceAsync();
                }
                else
                {
                    if (notify != null)
                    {
                        notify.Visible = false;
                        notify.Dispose();
                        notify = null;
                    }
                    Application.Exit();
                }
            };

            ToolStripMenuItem miAbout = new ToolStripMenuItem("关于 dsh-panel");
            miAbout.Click += delegate { ShowAbout(); };

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出面板");
            miExit.Click += delegate
            {
                reallyExit = true;
                if (notify != null)
                {
                    notify.Visible = false;
                    notify.Dispose();
                    notify = null;
                }
                Application.Exit();
            };

            trayMenu.Items.Add(miOpen);
            trayMenu.Items.Add(miToggle);
            trayMenu.Items.Add(miBrowser);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miAutoRestart);
            trayMenu.Items.Add(miAutoStart);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miExitAll);
            trayMenu.Items.Add(miExit);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miAbout);

            trayMenu.Opening += delegate
            {
                // 零阻塞：复用轮询状态，不做同步端口探测
                bool running = Program.lastRunning;
                bool busy = Program.opState != Program.OpState.Idle;
                miToggle.Text = busy ? (Program.opState == Program.OpState.Starting ? "启动中…" : "停止中…")
                                     : (running ? "停止服务" : "启动服务");
                miToggle.Enabled = !busy;
                miAutoRestart.Checked = Program.autoRestartEnabled;
                miAutoStart.Checked = IsAutoStartEnabled();
            };
            notify.ContextMenuStrip = trayMenu;
        }

        // 开机自启（HKCU Run 键）读写
        public bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch { return false; }
        }

        void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (enable)
                    {
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch { }
        }

        // 关于对话框
        void ShowAbout()
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            string msg = "dsh-panel v" + v.ToString() + "\n\n" +
                "DeepSeek Harness Web 控制面板\n" +
                "服务地址: " + Program.Url + "\n" +
                "面板目录: " + Program.ScriptRoot + "\n\n" +
                "开机自启: " + (IsAutoStartEnabled() ? "已启用" : "未启用") + "\n" +
                "崩溃自动恢复: " + (Program.autoRestartEnabled ? "已开启" : "关闭") + "\n" +
                "自检: dsh-panel.exe --selftest（结果见 logs\\dsh-web.selftest.log）";
            MessageBox.Show(this, msg, "关于 dsh-panel", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 托盘气泡（UI 线程调用入口）
        public void ShowBalloonTip(string title, string text, ToolTipIcon icon)
        {
            TryBeginInvoke(delegate
            {
                try { if (notify != null) notify.ShowBalloonTip(4000, title, text, icon); } catch { }
            });
        }

        // 托盘状态图标：动态生成 16x16（品牌渐变方块 + 右下角状态点），
        // 绿=运行中 / 灰=未运行 / 琥珀=启动停止中
        void SetTrayStateIcon(Color dot)
        {
            if (notify == null || dot == lastTrayDot) return;
            lastTrayDot = dot;
            try
            {
                using (Bitmap bmp = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        using (GraphicsPath path = Ui.RoundedRect(new Rectangle(1, 1, 14, 14), 4))
                        using (LinearGradientBrush b = new LinearGradientBrush(
                            new RectangleF(1, 1, 14, 14), Theme.Indigo, Theme.Violet, 45F))
                        {
                            g.FillPath(b, path);
                        }
                        using (SolidBrush s = new SolidBrush(dot))
                        {
                            g.FillEllipse(s, 8.5F, 8.5F, 6.5F, 6.5F);
                        }
                        using (Pen p = new Pen(Color.White, 1F))
                        {
                            g.DrawEllipse(p, 8.5F, 8.5F, 6.5F, 6.5F);
                        }
                    }
                    // Clone：让图标拥有自己的句柄，交给 NotifyIcon 管理
                    Icon ic = (Icon)Icon.FromHandle(bmp.GetHicon()).Clone();
                    if (lastTrayIcon != null)
                    {
                        lastTrayIcon.Dispose();
                        lastTrayIcon = null;
                    }
                    lastTrayIcon = ic;
                    notify.Icon = ic;
                }
            }
            catch { }
        }

        void ShowPanel()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        static Icon LoadTrayIcon()
        {
            try
            {
                string ico = Path.Combine(Program.ScriptRoot, "dsh-panel.ico");
                if (File.Exists(ico)) return new Icon(ico);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        void BuildUi()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.Padding = new Padding(0);
            layout.BackColor = Theme.Bg;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));    // 0: 顶栏
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 1: 网页（铺满）
            rootLayout = layout;

            // ---- 顶栏：Header（左）+ 状态胶囊 + 设置（右） ----
            TableLayoutPanel topRow = new TableLayoutPanel();
            topRow.Dock = DockStyle.Fill;
            topRow.ColumnCount = 3;
            topRow.RowCount = 1;
            topRow.BackColor = Theme.Card;
            topRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118)); // 状态胶囊
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));  // 设置按钮

            // 左侧：Logo + 标题 + URL
            header = new GradientHeader();
            header.Dock = DockStyle.Fill;
            header.SetUrl(Program.Url);

            // 状态胶囊：绿=运行 / 红=未运行 / 琥珀=切换中，点击启停
            statusCapsule = new StatusCapsule();
            statusCapsule.Dock = DockStyle.Fill;
            statusCapsule.Margin = new Padding(4, 6, 2, 6);
            statusCapsule.SetStatus("检测中…", Theme.TextMuted);
            statusCapsule.Click += delegate
            {
                try
                {
                    if (Program.opState != Program.OpState.Idle) return; // 切换中忽略
                    if (Program.lastRunning) Program.StopServiceAsync(); else Program.StartServiceAsync();
                    TryBeginInvoke(delegate { PollTick(null); });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "操作失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            // 设置按钮：打开设置页（含关于、偏好开关、DSH 状态与重新检测）
            btnSettings = new SmallButton("设置");
            btnSettings.Dock = DockStyle.Fill;
            btnSettings.Margin = new Padding(2, 6, 12, 6);
            btnSettings.Click += delegate { ShowSettingsWindow(); };

            topRow.Controls.Add(header, 0, 0);
            topRow.Controls.Add(statusCapsule, 1, 0);
            topRow.Controls.Add(btnSettings, 2, 0);

            layout.Controls.Add(topRow, 0, 0);

            // ---- 网页：WebView2 直接铺满窗口（浏览器形态） ----
            webView = new WebView2();
            webView.Dock = DockStyle.Fill;
            webView.DefaultBackgroundColor = Theme.Card;

            rootLayout.Controls.Add(webView, 0, 1);

            // v3.1：DSH 缺失时的下载引导页（覆盖网页区，默认隐藏）
            BuildSetupPanel();
            rootLayout.Controls.Add(setupPanel, 0, 1);
            setupPanel.SendToBack();

            Controls.Add(rootLayout);
        }

        // ---------- v2.0：内嵌网页（WebView2） ----------

        // 打开独立日志窗口（首次显示时带 owner；之后仅激活）
        void ShowLogWindow()
        {
            ShowLogWindowPublic();
        }

        public void ShowLogWindowPublic()
        {
            if (logWindow == null) return;
            try
            {
                if (!logWindow.Visible) logWindow.Show(this);
                if (logWindow.WindowState == FormWindowState.Minimized) logWindow.WindowState = FormWindowState.Normal;
                logWindow.Activate();
            }
            catch (Exception ex)
            {
                Program.LogEvent(LogKind.Error, "打开日志窗口失败：" + ex.Message);
            }
        }

        // 打开设置页
        void ShowSettingsWindow()
        {
            if (settingsWindow == null) return;
            try
            {
                if (!settingsWindow.Visible) settingsWindow.Show(this);
                if (settingsWindow.WindowState == FormWindowState.Minimized) settingsWindow.WindowState = FormWindowState.Normal;
                settingsWindow.Activate();
            }
            catch (Exception ex)
            {
                Program.LogEvent(LogKind.Error, "打开设置失败：" + ex.Message);
            }
        }

        // ---------- v3.1：DSH 安装引导 ----------

        // 检测回调（UI 线程）：已就绪 → 关闭引导页并自动启动服务；缺失 → 引导页
        void OnDshCheckResult(bool ok)
        {
            dshChecked = true;
            dshInstalled = ok;
            if (ok)
            {
                Program.LogEvent(LogKind.Success, "DeepSeek Harness 已就绪");
                setupPanel.Visible = false;
                try { webView.Visible = true; } catch { }
                MaybeAutoStartService();
            }
            else
            {
                Program.LogEvent(LogKind.Warn, "未检测到 DeepSeek Harness，进入安装指引页");
                ShowSetupPanel();
            }
        }

        // 按偏好自动启动服务（仅在 dsh 可用后调用）
        void MaybeAutoStartService()
        {
            if (Program.autoStartService && !Program.IsPortOpen())
            {
                Program.LogEvent(LogKind.Info, "检测到服务未运行，自动启动…");
                Program.StartServiceAsync();
            }
        }

        // 覆盖网页区的下载引导页（dsh 缺失时）
        void BuildSetupPanel()
        {
            setupPanel = new Panel();
            setupPanel.Dock = DockStyle.Fill;
            setupPanel.BackColor = Theme.Bg;
            setupPanel.Visible = false;

            TableLayoutPanel center = new TableLayoutPanel();
            center.Dock = DockStyle.Fill;
            center.ColumnCount = 1;
            center.RowCount = 5;
            center.BackColor = Theme.Bg;
            center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            center.Padding = new Padding(60, 0, 60, 0);

            setupTitle = new Label();
            setupTitle.Text = "DeepSeek Harness 未安装";
            setupTitle.Dock = DockStyle.Fill;
            setupTitle.TextAlign = ContentAlignment.MiddleCenter;
            setupTitle.Font = new Font("Segoe UI", 20F, FontStyle.Bold, GraphicsUnit.Point);
            setupTitle.ForeColor = Theme.TextStrong;
            setupTitle.BackColor = Theme.Bg;

            setupDesc = new Label();
            setupDesc.Text = "未检测到 DeepSeek Harness（@deepseek-ai/dsh）。\r\n" +
                "请先手动安装，完成后点击「重新检测」即可进入控制面板：\r\n" +
                "   npm install -g @deepseek-ai/dsh\r\n" +
                "（或在命令行执行 npx @deepseek-ai/dsh --version 自动下载缓存）";
            setupDesc.Dock = DockStyle.Fill;
            setupDesc.TextAlign = ContentAlignment.MiddleCenter;
            setupDesc.Font = new Font("Microsoft YaHei UI", 10F);
            setupDesc.ForeColor = Theme.TextMuted;
            setupDesc.BackColor = Theme.Bg;

            btnSetupRecheck = new RoundedButton(Theme.Blue, Theme.BlueHover, Theme.BluePress);
            btnSetupRecheck.Text = "重新检测";
            btnSetupRecheck.Dock = DockStyle.Fill;
            btnSetupRecheck.Margin = new Padding(140, 6, 140, 6);
            btnSetupRecheck.Click += delegate
            {
                setupStatus.Text = "正在重新检测…";
                setupStatus.ForeColor = Theme.TextFaint;
                Program.LogEvent(LogKind.Info, "正在重新检测 DeepSeek Harness…");
                Program.CheckDshAsync(delegate(bool ok)
                {
                    TryBeginInvoke(delegate { OnDshCheckResult(ok); });
                });
            };

            setupStatus = new Label();
            setupStatus.Text = "";
            setupStatus.Dock = DockStyle.Fill;
            setupStatus.TextAlign = ContentAlignment.MiddleCenter;
            setupStatus.Font = new Font("Microsoft YaHei UI", 8.5F);
            setupStatus.ForeColor = Theme.TextFaint;
            setupStatus.BackColor = Theme.Bg;

            center.Controls.Add(setupTitle, 0, 0);
            center.Controls.Add(setupDesc, 0, 1);
            center.Controls.Add(btnSetupRecheck, 0, 2);
            center.Controls.Add(setupStatus, 0, 3);

            setupPanel.Controls.Add(center);
        }

        void ShowSetupPanel()
        {
            if (setupPanel == null) BuildSetupPanel();
            setupStatus.Text = "";
            setupStatus.ForeColor = Theme.TextFaint;
            // WebView2 是原生 HWND 窗口，可能浮在引导页上层——先隐藏
            try { webView.Visible = false; } catch { }
            setupPanel.Visible = true;
            setupPanel.BringToFront();
        }

        // 设置页回调：重新检测 dsh 可用性
        public void RecheckDsh()
        {
            Program.LogEvent(LogKind.Info, "正在重新检测 DeepSeek Harness…");
            Program.CheckDshAsync(delegate(bool ok)
            {
                TryBeginInvoke(delegate { OnDshCheckResult(ok); });
            });
        }

        // 设置页回调：开机自启写入（与托盘菜单联动）
        public void SetAutoStartEnabled(bool value)
        {
            SetAutoStart(value);
            if (miAutoStart != null) miAutoStart.Checked = value;
        }

        // 设置页回调：崩溃自动恢复读写（与托盘菜单联动）
        public bool IsAutoRestartEnabled()
        {
            return Program.autoRestartEnabled;
        }

        public void SetAutoRestartEnabled(bool value)
        {
            Program.autoRestartEnabled = value;
            if (miAutoRestart != null) miAutoRestart.Checked = value;
        }

        // 惰性初始化 WebView2（UI 线程调用；await 后续自动回到 UI 线程）
        async void EnsureWebViewAsync()
        {
            if (webViewInited || webViewFailed || webViewBusy) return;
            webViewBusy = true;
            try
            {
                // 用户数据目录放 run/（与运行时产物一致）；失败则退回默认目录
                string dataDir = Path.Combine(Program.ScriptRoot, "run", "webview2-data");
                CoreWebView2Environment env = null;
                try { env = await CoreWebView2Environment.CreateAsync(null, dataDir); } catch { }
                if (env == null) env = await CoreWebView2Environment.CreateAsync(null, null);
                await webView.EnsureCoreWebView2Async(env);
                webViewInited = true;
                Program.LogEvent(LogKind.Info, "内嵌浏览器已就绪（WebView2）");
                SyncWebView(Program.lastRunning);
            }
            catch (Exception ex)
            {
                webViewFailed = true;
                Program.LogEvent(LogKind.Warn, "内嵌浏览器不可用，将使用系统浏览器：" + ex.Message);
                ShowWebFallback();
            }
            finally
            {
                webViewBusy = false;
            }
        }

        // 占位页（服务未运行 / 启动中）：主题风格内联 HTML，无外部资源
        void ShowPlaceholder(string kind)
        {
            webPlaceholderKind = kind;
            bool starting = kind == "starting";
            string color = starting ? "#f59e0b" : "#9ca3af";
            string title = starting ? "服务启动中…" : "服务未运行";
            string sub = starting ? "端口就绪后自动加载 DSH 面板" : "点击「启动服务」后此处将显示 DSH 面板";
            string html =
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>" +
                "body{margin:0;height:100vh;display:flex;align-items:center;justify-content:center;" +
                "background:#f5f7fa;font-family:'Microsoft YaHei UI',sans-serif;color:#6b7280;}" +
                ".box{text-align:center}.dot{width:16px;height:16px;border-radius:50%;margin:0 auto 18px;" +
                "background:" + color + ";box-shadow:0 0 12px " + color + "55;}" +
                "h2{margin:0 0 10px;font-size:20px;color:#374151;font-weight:600}" +
                "p{margin:0;font-size:13px}</style></head><body>" +
                "<div class=\"box\"><div class=\"dot\"></div><h2>" + title + "</h2><p>" + sub + "</p></div>" +
                "</body></html>";
            try { webView.CoreWebView2.NavigateToString(html); } catch { }
        }

        // 服务状态 <-> 网页内容同步（UI 线程；PollTick 每 2s 驱动）
        void SyncWebView(bool running)
        {
            if (!webViewInited || webViewFailed) return;
            try
            {
                if (running)
                {
                    if (!webShowsApp)
                    {
                        webShowsApp = true;
                        webPlaceholderKind = null;
                        webView.CoreWebView2.Navigate(Program.Url);
                    }
                }
                else
                {
                    string kind = Program.opState == Program.OpState.Starting ? "starting" : "down";
                    if (webShowsApp || webPlaceholderKind != kind)
                    {
                        webShowsApp = false;
                        ShowPlaceholder(kind);
                    }
                }
            }
            catch { }
        }

        // WebView2 初始化失败：网页区显示提示（顶栏「浏览器」按钮仍可用）
        void ShowWebFallback()
        {
            try
            {
                webView.Visible = false;
                Label lbl = new Label();
                lbl.Dock = DockStyle.Fill;
                lbl.TextAlign = ContentAlignment.MiddleCenter;
                lbl.Font = new Font("Microsoft YaHei UI", 10F);
                lbl.ForeColor = Theme.TextMuted;
                lbl.Text = "未检测到 WebView2 运行时，无法内嵌显示网页。\r\n" +
                           "请安装 WebView2 Runtime（developer.microsoft.com/microsoft-edge/webview2/），\r\n" +
                           "或使用顶栏「浏览器」按钮在系统浏览器中打开。";
                rootLayout.Controls.Add(lbl, 0, 1);
            }
            catch { }
        }

        // 后台线程轮询：防重入；崩溃自动恢复检测；状态机；活动日志刷新
        void PollTick(object state)
        {
            if (closed) return;
            if (Interlocked.Exchange(ref pollBusy, 1) != 0) return;
            try
            {
                bool running = Program.IsPortOpen();
                Program.lastRunning = running;

                // 运行时长起点：首次检测到运行 / 停止后清零
                if (running)
                {
                    if (!Program.everRan)
                    {
                        Program.everRan = true;
                        Program.serviceUpSince = Environment.TickCount;
                        Program.LogEvent(LogKind.Info, "服务正在运行 · " + Program.Url);
                    }
                    else if (Program.serviceUpSince == 0)
                    {
                        Program.serviceUpSince = Environment.TickCount;
                    }
                }
                else
                {
                    Program.serviceUpSince = 0;
                }

                // ---- 崩溃自动恢复（默认关闭；托盘菜单开启） ----
                if (Program.opState == Program.OpState.Idle && Program.autoRestartEnabled && !Program.manualStop)
                {
                    if (Program.everRan && lastPollRunning && !running)
                    {
                        long now = Environment.TickCount;
                        if (now - Program.lastRestartTick >= 60000)
                        {
                            if (Program.autoRestartCount >= 3)
                            {
                                Program.autoRestartEnabled = false;
                                Program.autoRestartCount = 0;
                                Program.LogEvent(LogKind.Error, "服务多次自动恢复失败，自动恢复已停用");
                                TryBeginInvoke(delegate
                                {
                                    try { if (notify != null) notify.ShowBalloonTip(3000, "dsh-panel", "服务多次自动恢复失败，已停止自动恢复。请手动检查。", ToolTipIcon.Warning); } catch { }
                                });
                            }
                            else
                            {
                                Program.lastRestartTick = now;
                                Program.autoRestartCount++;
                                Program.LogEvent(LogKind.Warn, "检测到服务异常退出，正在自动恢复…");
                                Program.StartServiceAsync();
                            }
                        }
                    }
                }
                if (running && Program.autoRestartCount > 0 && Environment.TickCount - Program.lastRestartTick > 20000)
                {
                    Program.autoRestartCount = 0;
                }
                lastPollRunning = running;

                Program.OpState cur = Program.opState;
                // 启动中：端口已就绪或启动进程已退出 → 回到空闲
                if (cur == Program.OpState.Starting)
                {
                    if (running)
                    {
                        Program.opState = Program.OpState.Idle;
                        cur = Program.OpState.Idle;
                    }
                    else if (File.Exists(Program.PidFile))
                    {
                        string pidText = "";
                        try { pidText = File.ReadAllText(Program.PidFile).Trim(); } catch { }
                        int pid;
                        if (int.TryParse(pidText, out pid) && pid > 0)
                        {
                            try { if (Process.GetProcessById(pid).HasExited) { Program.opState = Program.OpState.Idle; cur = Program.OpState.Idle; } }
                            catch { Program.opState = Program.OpState.Idle; cur = Program.OpState.Idle; }
                        }
                    }
                }
                bool isStarting = cur == Program.OpState.Starting;
                bool isStopping = cur == Program.OpState.Stopping;

                string statusText;
                Color statusColor;
                if (isStarting)
                {
                    statusText = "启动中…";
                    statusColor = Theme.Amber;
                }
                else if (isStopping)
                {
                    statusText = "停止中…";
                    statusColor = Theme.Amber;
                }
                else
                {
                    statusText = running ? "运行中" : "未运行";
                    statusColor = running ? Theme.Green : Theme.Red;
                }
                bool pulse = isStarting || isStopping;

                // 本次启动成功后端口首次就绪：内嵌网页优先，运行时缺失时回退系统浏览器
                if (running && Program.autoOpenPending)
                {
                    Program.autoOpenPending = false;
                    Program.LogEvent(LogKind.Success, "服务已就绪 · " + Program.Url);
                    if (webViewFailed)
                    {
                        Program.LogEvent(LogKind.Info, "正在打开浏览器…");
                        TryBeginInvoke(delegate { Program.OpenBrowser(); });
                    }
                    else
                    {
                        TryBeginInvoke(delegate
                        {
                            EnsureWebViewAsync();
                            SyncWebView(true);
                        });
                    }
                }

                // 服务输出增量（err 在前、out 在后，与真实输出时间序一致）
                List<string> newErr = errTail.UpdateNew(Program.ErrLog);
                List<string> newOut = outTail.UpdateNew(Program.OutLog);

                // 动态信息行：运行时长 + 日志大小
                string infoText = BuildInfoText(running);

                // 内嵌网页随服务状态变化（占位页 <-> 真实页面）
                if (webViewInited || webViewBusy)
                {
                    bool r = running;
                    TryBeginInvoke(delegate { SyncWebView(r); });
                }

                TryBeginInvoke(delegate
                {
                    try
                    {
                        // 活动日志（转发到独立日志窗口）
                        foreach (string s in newErr) logWindow.AppendEvent(LogKind.ServiceErr, s);
                        foreach (string s in newOut) logWindow.AppendEvent(LogKind.ServiceOut, s);
                        logWindow.SetInfo(infoText);

                        // 状态胶囊与托盘图标（内部去重，零开销）
                        string st = statusText; Color sc = statusColor;
                        bool run = running; bool pu = pulse;
                        statusCapsule.SetStatus(st, sc);
                        // 托盘状态图标：绿=运行 / 灰=未运行 / 琥珀=启动停止中
                        Color dotColor = pu ? Theme.Amber : (run ? Theme.Green : Color.FromArgb(148, 163, 184));
                        SetTrayStateIcon(dotColor);
                    }
                    catch { }
                });
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref pollBusy, 0);
            }
        }

        // 信息行文本：运行时长 + 日志大小
        string BuildInfoText(bool running)
        {
            string s;
            if (running && Program.serviceUpSince > 0)
            {
                TimeSpan up = TimeSpan.FromMilliseconds(Environment.TickCount - Program.serviceUpSince);
                s = "已运行 " + string.Format("{0:00}:{1:00}:{2:00}", (int)up.TotalHours, up.Minutes, up.Seconds);
            }
            else
            {
                s = "服务未运行";
            }
            s += " · 日志 " + FormatSize(GetLogBytes());
            return s;
        }

        long GetLogBytes()
        {
            long total = 0;
            try
            {
                if (File.Exists(Program.OutLog)) total += new FileInfo(Program.OutLog).Length;
                if (File.Exists(Program.ErrLog)) total += new FileInfo(Program.ErrLog).Length;
            }
            catch { }
            return total;
        }

        static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return string.Format("{0:0.0} KB", bytes / 1024.0);
            return string.Format("{0:0.0} MB", bytes / 1048576.0);
        }

        void TryBeginInvoke(Action action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(action);
                }
            }
            catch { }
        }

        public void ForceRefresh()
        {
            TryBeginInvoke(delegate { PollTick(null); });
        }
    }

    // ---------- v3.0 独立活动日志窗口（主界面网页化后，日志移入弹窗） ----------

    class LogWindow : Form
    {
        RichTextBox logBox;
        Label lblLogInfo;
        SmallButton btnLogClear;
        SmallButton btnLogCopy;
        SmallButton btnLogDir;
        SmallButton btnLogScroll;
        bool autoScroll = true;
        int logLineCount;           // 日志区当前行数（用于截断）
        const int LogMaxLines = 500;
        const int LogTrimTo = 200;

        public LogWindow()
        {
            Text = "活动日志 · DeepSeek Harness Web";
            Size = new Size(760, 500);
            MinimumSize = new Size(480, 320);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            BuildUi();
            // 关闭 = 隐藏（窗口对象由主窗体持有，随时可再次弹出）
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        void BuildUi()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.BackColor = Theme.Bg;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // 0: 标题
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));    // 1: 工具栏
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 2: 日志卡片

            // ---- 标题行 ----
            Label lblTitle = new Label();
            lblTitle.Text = "活动日志";
            lblTitle.Dock = DockStyle.Fill;
            lblTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblTitle.Padding = new Padding(20, 0, 0, 0);
            lblTitle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lblTitle.ForeColor = Theme.TextMuted;
            lblTitle.BackColor = Theme.Bg;

            // ---- 工具栏：信息 + 清空 / 复制 / 目录 / 自动滚动 ----
            TableLayoutPanel toolbarRow = new TableLayoutPanel();
            toolbarRow.Dock = DockStyle.Fill;
            toolbarRow.Margin = new Padding(14, 0, 14, 4);
            toolbarRow.BackColor = Theme.Bg;
            toolbarRow.ColumnCount = 5;
            toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            toolbarRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

            lblLogInfo = new Label();
            lblLogInfo.Text = "";
            lblLogInfo.Dock = DockStyle.Fill;
            lblLogInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblLogInfo.Font = new Font("Microsoft YaHei UI", 8F);
            lblLogInfo.ForeColor = Theme.TextFaint;
            lblLogInfo.BackColor = Theme.Bg;

            btnLogClear = new SmallButton("清空");
            btnLogClear.Dock = DockStyle.Fill;
            btnLogClear.Margin = new Padding(2, 3, 2, 3);
            btnLogClear.Click += delegate
            {
                logBox.Clear();
                logLineCount = 0;
                Program.LogEvent(LogKind.Info, "日志视图已清空");
            };

            btnLogCopy = new SmallButton("复制");
            btnLogCopy.Dock = DockStyle.Fill;
            btnLogCopy.Margin = new Padding(2, 3, 2, 3);
            btnLogCopy.Click += delegate
            {
                try
                {
                    if (logBox.TextLength > 0)
                    {
                        Clipboard.SetText(logBox.Text);
                        Program.LogEvent(LogKind.Info, "日志已复制到剪贴板");
                    }
                }
                catch { }
            };

            btnLogDir = new SmallButton("目录");
            btnLogDir.Dock = DockStyle.Fill;
            btnLogDir.Margin = new Padding(2, 3, 2, 3);
            btnLogDir.Click += delegate
            {
                Program.LogEvent(LogKind.Info, "打开日志目录…");
                Program.OpenLogFolder();
            };

            btnLogScroll = new SmallButton("自动滚动");
            btnLogScroll.Dock = DockStyle.Fill;
            btnLogScroll.Margin = new Padding(2, 3, 2, 3);
            btnLogScroll.Click += delegate
            {
                autoScroll = !autoScroll;
                btnLogScroll.IsOn = autoScroll;
                if (autoScroll)
                {
                    logBox.SelectionStart = logBox.TextLength;
                    logBox.ScrollToCaret();
                }
                Program.LogEvent(LogKind.Info, autoScroll ? "自动滚动已开启" : "自动滚动已关闭");
            };
            btnLogScroll.IsOn = true;

            toolbarRow.Controls.Add(lblLogInfo, 0, 0);
            toolbarRow.Controls.Add(btnLogClear, 1, 0);
            toolbarRow.Controls.Add(btnLogCopy, 2, 0);
            toolbarRow.Controls.Add(btnLogDir, 3, 0);
            toolbarRow.Controls.Add(btnLogScroll, 4, 0);

            // ---- 日志卡片 ----
            CardPanel logCard = new CardPanel();
            logCard.Dock = DockStyle.Fill;
            logCard.Margin = new Padding(14, 0, 14, 14);

            logBox = new RichTextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.Dock = DockStyle.Fill;
            logBox.BorderStyle = BorderStyle.None;
            logBox.ScrollBars = RichTextBoxScrollBars.Vertical;
            logBox.Font = new Font("Consolas", 9F);
            logBox.BackColor = Theme.Card;
            logBox.ForeColor = Theme.TextStrong;
            logBox.DetectUrls = false;
            logBox.TabStop = false;
            logCard.Controls.Add(logBox);

            layout.Controls.Add(lblTitle, 0, 0);
            layout.Controls.Add(toolbarRow, 0, 1);
            layout.Controls.Add(logCard, 0, 2);
            Controls.Add(layout);
        }

        // 任意线程可调用；窗口隐藏时也正常渲染（RichTextBox 无需可见）
        public void AppendEvent(LogKind kind, string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke((Action)delegate { AppendLine(kind, text); });
                    return;
                }
                AppendLine(kind, text);
            }
            catch { }
        }

        void AppendLine(LogKind kind, string text)
        {
            if (text == null || text.Length == 0) return;
            // 超长截断：删除最旧的 TrimTo 行
            if (logLineCount >= LogMaxLines + LogTrimTo)
            {
                int idx = logBox.GetFirstCharIndexFromLine(LogTrimTo);
                if (idx > 0)
                {
                    logBox.Select(0, idx);
                    logBox.SelectedText = "";
                    logLineCount -= LogTrimTo;
                }
            }
            logLineCount++;
            string ts = DateTime.Now.ToString("HH:mm:ss");
            logBox.SelectionStart = logBox.TextLength;
            logBox.SelectionLength = 0;
            logBox.SelectionColor = Theme.TextFaint;          // 时间戳：浅灰
            logBox.AppendText("[" + ts + "] ");
            logBox.SelectionColor = LogKindColor(kind);       // 内容：按类型着色
            logBox.AppendText(text);
            logBox.AppendText(Environment.NewLine);
            if (autoScroll)
            {
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            }
        }

        static Color LogKindColor(LogKind kind)
        {
            switch (kind)
            {
                case LogKind.Info: return Color.FromArgb(37, 99, 235);        // 蓝
                case LogKind.Success: return Color.FromArgb(5, 150, 105);     // 绿
                case LogKind.Warn: return Color.FromArgb(180, 83, 9);         // 琥珀
                case LogKind.Error: return Color.FromArgb(220, 38, 38);       // 红
                case LogKind.ServiceOut: return Color.FromArgb(55, 65, 81);   // 深灰
                case LogKind.ServiceErr: return Color.FromArgb(185, 28, 28);  // 暗红
            }
            return Theme.TextStrong;
        }

        // 动态信息行（由主窗体 PollTick 转发）
        public void SetInfo(string text)
        {
            try
            {
                if (lblLogInfo != null) lblLogInfo.Text = text;
            }
            catch { }
        }
    }

    // ---------- v3.1 设置页（常规开关 / 服务 / 关于） ----------

    class SettingsWindow : Form
    {
        MainForm owner;
        Label lblDshStatus;
        SmallButton btnRecheck;

        public SettingsWindow(MainForm ownerForm)
        {
            owner = ownerForm;
            Text = "设置 · DeepSeek Harness Web";
            Size = new Size(640, 560);
            MinimumSize = new Size(520, 420);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            BuildUi();
            // 关闭 = 隐藏（窗口对象由主窗体持有）
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
        }

        // 每次显示时刷新 DSH 状态（数据来自主窗体检测结果）
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshDshStatus();
        }

        public void RefreshDshStatus()
        {
            if (lblDshStatus == null) return;
            try
            {
                if (!owner.DshChecked)
                {
                    lblDshStatus.Text = "DeepSeek Harness：检测中…";
                    lblDshStatus.ForeColor = Theme.TextMuted;
                }
                else if (owner.DshInstalled)
                {
                    lblDshStatus.Text = "DeepSeek Harness：已就绪";
                    lblDshStatus.ForeColor = Theme.Green;
                }
                else
                {
                    lblDshStatus.Text = "DeepSeek Harness：未安装（主界面已显示下载引导页）";
                    lblDshStatus.ForeColor = Theme.Red;
                }
            }
            catch { }
        }

        void BuildUi()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.ColumnCount = 1;
            layout.RowCount = 3;
            layout.BackColor = Theme.Bg;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));

            // 标题
            Label lblTitle = new Label();
            lblTitle.Text = "设置";
            lblTitle.Dock = DockStyle.Fill;
            lblTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblTitle.Padding = new Padding(20, 0, 0, 0);
            lblTitle.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            lblTitle.ForeColor = Theme.TextStrong;
            lblTitle.BackColor = Theme.Bg;

            // 内容（可滚动，窗口缩小时不丢控件）
            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Theme.Bg;

            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Top;
            body.AutoSize = true;
            body.ColumnCount = 1;
            body.BackColor = Theme.Bg;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            body.Padding = new Padding(16, 4, 16, 16);

            body.Controls.Add(MakeSection("常规", new Control[] {
                MakeToggleRow("开机自启", "登录 Windows 后自动启动本面板",
                    owner.IsAutoStartEnabled(), delegate(bool v) { owner.SetAutoStartEnabled(v); }),
                MakeToggleRow("崩溃自动恢复", "服务异常退出时自动重新启动",
                    owner.IsAutoRestartEnabled(), delegate(bool v) { owner.SetAutoRestartEnabled(v); }),
                MakeToggleRow("启动时自动启动服务", "面板启动后自动运行 DSH 服务",
                    Program.GetPref("AutoStartService", true), delegate(bool v)
                    {
                        Program.autoStartService = v;
                        Program.SetPref("AutoStartService", v);
                    })
            }));
            body.Controls.Add(MakeSection("服务", new Control[] {
                MakeInfoRow("监听端口", "3080（dsh-web.config）"),
                MakeDshStatusRow(),
                MakeButtonRow(new string[] { "打开日志窗口", "打开日志目录", "打开系统浏览器" })
            }));
            body.Controls.Add(MakeSection("关于", new Control[] {
                MakeAboutRow(),
                MakeLinkRow("DeepSeek Harness 项目主页", "https://github.com/deepseek-ai/deepseek-harness")
            }));

            scroll.Controls.Add(body);
            layout.Controls.Add(lblTitle, 0, 0);
            layout.Controls.Add(scroll, 0, 1);
            Controls.Add(layout);
        }

        // 分区卡片：标题 + 内容行
        Control MakeSection(string title, Control[] rows)
        {
            CardPanel card = new CardPanel();
            card.Dock = DockStyle.Top;
            card.AutoSize = true;
            card.Margin = new Padding(0, 0, 0, 10);

            TableLayoutPanel inner = new TableLayoutPanel();
            inner.Dock = DockStyle.Top;
            inner.AutoSize = true;
            inner.ColumnCount = 1;
            inner.Padding = new Padding(18, 10, 18, 14);
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label lbl = new Label();
            lbl.Text = title;
            lbl.AutoSize = true;
            lbl.Margin = new Padding(0, 0, 0, 6);
            lbl.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lbl.ForeColor = Theme.TextMuted;
            inner.Controls.Add(lbl);

            foreach (Control r in rows)
            {
                r.Margin = new Padding(0, 3, 0, 3);
                inner.Controls.Add(r);
            }
            card.Controls.Add(inner);
            return card;
        }

        // 开关按钮
        SmallButton MakeToggle(bool initial, Action<bool> onChange)
        {
            SmallButton b = new SmallButton(initial ? "开" : "关");
            b.IsOn = initial;
            b.Size = new Size(56, 26);
            b.Margin = new Padding(10, 4, 0, 4);
            b.Click += delegate
            {
                bool v = !b.IsOn;
                b.IsOn = v;
                b.Text = v ? "开" : "关";
                if (onChange != null)
                {
                    try { onChange(v); } catch { }
                }
            };
            return b;
        }

        // 开关行：名称+说明（左） / 开关（右）
        Control MakeToggleRow(string name, string desc, bool initial, Action<bool> onChange)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

            Label lbl = new Label();
            lbl.AutoSize = true;
            lbl.Dock = DockStyle.Fill;
            lbl.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            lbl.ForeColor = Theme.TextStrong;
            lbl.Text = name;
            lbl.TextAlign = ContentAlignment.MiddleLeft;

            Label descLbl = new Label();
            descLbl.Text = desc;
            descLbl.AutoSize = true;
            descLbl.Dock = DockStyle.Fill;
            descLbl.Font = new Font("Microsoft YaHei UI", 8F);
            descLbl.ForeColor = Theme.TextFaint;
            descLbl.TextAlign = ContentAlignment.MiddleLeft;

            // 名称与说明同列纵向排布
            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Top;
            left.AutoSize = true;
            left.ColumnCount = 1;
            left.RowCount = 2;
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            left.Controls.Add(lbl, 0, 0);
            left.Controls.Add(descLbl, 0, 1);

            row.Controls.Add(left, 0, 0);
            row.Controls.Add(MakeToggle(initial, onChange), 1, 0);
            return row;
        }

        // 只读信息行
        Control MakeInfoRow(string name, string value)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            Label n = new Label();
            n.Text = name;
            n.AutoSize = true;
            n.Font = new Font("Microsoft YaHei UI", 9F);
            n.ForeColor = Theme.TextMuted;
            n.TextAlign = ContentAlignment.MiddleLeft;

            Label v = new Label();
            v.Text = value;
            v.AutoSize = true;
            v.Dock = DockStyle.Fill;
            v.Font = new Font("Microsoft YaHei UI", 9F);
            v.ForeColor = Theme.TextStrong;
            v.TextAlign = ContentAlignment.MiddleLeft;

            row.Controls.Add(n, 0, 0);
            row.Controls.Add(v, 1, 0);
            return row;
        }

        // DSH 状态 + 重新检测
        Control MakeDshStatusRow()
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.ColumnCount = 2;
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

            lblDshStatus = new Label();
            lblDshStatus.Text = "DeepSeek Harness：检测中…";
            lblDshStatus.AutoSize = true;
            lblDshStatus.Dock = DockStyle.Fill;
            lblDshStatus.Font = new Font("Microsoft YaHei UI", 9F);
            lblDshStatus.ForeColor = Theme.TextMuted;
            lblDshStatus.TextAlign = ContentAlignment.MiddleLeft;

            btnRecheck = new SmallButton("重新检测");
            btnRecheck.Size = new Size(84, 26);
            btnRecheck.Margin = new Padding(10, 4, 0, 4);
            btnRecheck.Click += delegate
            {
                lblDshStatus.Text = "DeepSeek Harness：检测中…";
                lblDshStatus.ForeColor = Theme.TextMuted;
                if (owner != null) owner.RecheckDsh();
            };

            row.Controls.Add(lblDshStatus, 0, 0);
            row.Controls.Add(btnRecheck, 1, 0);
            return row;
        }

        // 工具按钮行
        Control MakeButtonRow(string[] labels)
        {
            TableLayoutPanel row = new TableLayoutPanel();
            row.Dock = DockStyle.Top;
            row.AutoSize = true;
            row.ColumnCount = labels.Length;
            for (int i = 0; i < labels.Length; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            string[] actions = { "log", "folder", "browser" };
            for (int i = 0; i < labels.Length; i++)
            {
                string act = actions[i];
                SmallButton b = new SmallButton(labels[i]);
                b.AutoSize = true;
                b.Margin = new Padding(0, 4, 8, 4);
                b.Click += delegate
                {
                    if (owner == null) return;
                    if (act == "log") owner.ShowLogWindowPublic();
                    else if (act == "folder") Program.OpenLogFolder();
                    else Program.OpenBrowser();
                };
                row.Controls.Add(b, i, 0);
            }
            return row;
        }

        // 关于文本
        Control MakeAboutRow()
        {
            Label lbl = new Label();
            lbl.Dock = DockStyle.Top;
            lbl.AutoSize = true;
            lbl.Font = new Font("Microsoft YaHei UI", 9F);
            lbl.ForeColor = Theme.TextMuted;
            lbl.Text = "dsh-web 控制面板  v" + Assembly.GetExecutingAssembly().GetName().Version + "\r\n" +
                "DeepSeek Harness Web（@deepseek-ai/dsh）的 Windows 原生控制面板。\r\n" +
                "自绘界面基于 .NET Framework 与 C# 5.0（零第三方 UI 依赖）；\r\n" +
                "内嵌浏览器使用 WebView2（Chromium，MIT 许可）；服务管理使用 PowerShell 脚本。";
            return lbl;
        }

        // 外部链接行
        Control MakeLinkRow(string text, string url)
        {
            LinkLabel link = new LinkLabel();
            link.Text = text;
            link.AutoSize = true;
            link.LinkColor = Theme.Blue;
            link.Font = new Font("Microsoft YaHei UI", 9F);
            link.Click += delegate { Program.OpenExternal(url); };
            return link;
        }
    }

    // ---------- v3.0 顶栏（Logo + 标题 + URL，浅色薄条） ----------

    class GradientHeader : Panel
    {
        string url = "";

        public GradientHeader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetUrl(string value)
        {
            url = value;
            Invalidate();
        }

        // 注意：本控件设置了 AllPaintingInWmPaint，系统不会调用 OnPaintBackground，
        // 全部绘制必须在 OnPaint 中完成，否则顶栏整块不绘制（残留黑块）
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 先铺满底色（自绘控件不自动清背景，缺省会残留黑块）
            using (SolidBrush bgBrush = new SolidBrush(Theme.Card))
            {
                g.FillRectangle(bgBrush, 0, 0, Width, Height);
            }

            // 底部 1px 分隔线（柔和浅灰）
            using (SolidBrush line = new SolidBrush(Color.FromArgb(238, 241, 245)))
            {
                g.FillRectangle(line, 0, Height - 1, Width, 1);
            }

            // Logo
            Ui.DrawLogo(g, 14, (Height - 26) / 2, 26);

            // 标题（与 URL 同行，URL 灰色小字紧随）
            using (Font titleFont = new Font("Segoe UI", 11F, FontStyle.Bold, GraphicsUnit.Point))
            using (Font urlFont = new Font("Consolas", 8.5F, GraphicsUnit.Point))
            {
                TextRenderer.DrawText(g, "DeepSeek Harness Web", titleFont,
                    new Rectangle(50, 0, Width - 200, Height), Color.FromArgb(31, 41, 55),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                int tw = TextRenderer.MeasureText(g, "DeepSeek Harness Web", titleFont).Width;
                TextRenderer.DrawText(g, url, urlFont,
                    new Rectangle(50 + tw + 16, 0, Width - 200 - tw - 16, Height), Theme.TextFaint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // ---------- 服务状态胶囊（顶栏：圆角填充 + 白字，点击启停） ----------

    class StatusCapsule : Button
    {
        string statusText = "未运行";
        Color statusColor = Theme.Red;
        bool hovering;
        bool pressed;

        public string StatusText { get { return statusText; } }
        public Color StatusColor { get { return statusColor; } }

        public StatusCapsule()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetStatus(string text, Color color)
        {
            statusText = text;
            statusColor = color;
            // 同步 Text 属性：UI 自动化（UIA Name）与辅助功能依赖它
            try { Text = text; } catch { }
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { pressed = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 先铺满父容器底色，圆角外不残留黑块
            Ui.ClearBackground(g, this);

            Color fill = statusColor;
            if (pressed) fill = Ui.Darken(fill, 0.85F);
            else if (hovering) fill = Ui.Darken(fill, 0.93F);

            using (GraphicsPath path = Ui.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }
            // 状态白点 + 白字
            int shift = pressed ? 1 : 0;
            using (SolidBrush dot = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                g.FillEllipse(dot, 12, Height / 2 - 3 + shift, 6, 6);
            }
            TextRenderer.DrawText(g, statusText, Font,
                new Rectangle(24, shift, Width - 28, Height), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ---------- 状态行（光晕指示灯 + 状态文字，启动/停止时脉冲） ----------

    class StatusLine : Control
    {
        string statusText = "检测中…";
        Color statusColor = Theme.TextMuted;
        bool pulse;
        float phase;

        public string StatusText { get { return statusText; } }
        public Color StatusColor { get { return statusColor; } }
        public bool Pulse { get { return pulse; } }

        public StatusLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void SetStatus(string text, Color color, bool pulsing)
        {
            statusText = text;
            statusColor = color;
            pulse = pulsing;
            if (!pulsing) phase = 0f;
            Invalidate();
        }

        public void StopPulse()
        {
            pulse = false;
            phase = 0f;
            Invalidate();
        }

        // 返回 true = 继续动画
        public bool StepPulse()
        {
            if (!pulse) return false;
            phase += 0.22F;
            if (phase > (float)Math.PI * 2F) phase -= (float)Math.PI * 2F;
            Invalidate();
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (Font f = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold, GraphicsUnit.Point))
            {
                int tw = TextRenderer.MeasureText(g, statusText, f).Width;
                int total = 26 + 12 + tw;
                int startX = (Width - total) / 2;
                int cx = startX + 13;
                int cy = Height / 2;
                int tx = startX + 26 + 12;

                // 光晕（脉冲时呼吸）
                float glow = pulse ? 0.65F + 0.35F * (float)Math.Sin(phase) : 0.85F;
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(38 * glow), statusColor)))
                {
                    g.FillEllipse(b, cx - 26, cy - 26, 52, 52);
                }
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(62 * glow), statusColor)))
                {
                    g.FillEllipse(b, cx - 16, cy - 16, 32, 32);
                }
                // 实心点 + 白描边
                using (SolidBrush dot = new SolidBrush(statusColor))
                {
                    g.FillEllipse(dot, cx - 7, cy - 7, 14, 14);
                }
                using (Pen pen = new Pen(Color.White, 2F))
                {
                    g.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
                }

                TextRenderer.DrawText(g, statusText, f,
                    new Rectangle(tx, 0, tw + 4, Height), Theme.TextStrong,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    // ---------- 圆角按钮：几何图标 + 颜色平滑过渡 + 按下下沉 ----------

    class RoundedButton : Button
    {
        Color normalColor;
        Color hoverColor;
        Color pressColor;
        Color currentColor;
        ButtonIcon icon;
        bool hovering;
        bool pressed;
        System.Windows.Forms.Timer animTimer;

        public ButtonIcon Icon { get { return icon; } }

        public RoundedButton(Color normal, Color hover, Color press)
        {
            normalColor = normal;
            hoverColor = hover;
            pressColor = press;
            currentColor = normal;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            animTimer = new System.Windows.Forms.Timer();
            animTimer.Interval = 30;
            animTimer.Tick += delegate
            {
                if (!StepColor()) animTimer.Stop();
            };
        }

        public void SetIcon(ButtonIcon value)
        {
            icon = value;
            Invalidate();
        }

        public void SetColors(Color normal, Color hover, Color press)
        {
            normalColor = normal;
            hoverColor = hover;
            pressColor = press;
            StartAnim();
        }

        void StartAnim()
        {
            if (!animTimer.Enabled) animTimer.Start();
        }

        Color TargetColor()
        {
            if (pressed) return pressColor;
            if (hovering) return hoverColor;
            return normalColor;
        }

        // 颜色插值一步；返回 true = 仍需继续
        bool StepColor()
        {
            Color target = TargetColor();
            if (Ui.Near(currentColor, target))
            {
                currentColor = target;
                return false;
            }
            currentColor = Ui.Blend(currentColor, target, 0.28F);
            Invalidate();
            return true;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
            StartAnim();
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; StartAnim(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressed = false; StartAnim(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { pressed = true; StartAnim(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; StartAnim(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.ClearBackground(g, this);

            Color fill = Enabled ? currentColor : Ui.Darken(currentColor, 0.72F);
            using (GraphicsPath path = Ui.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusButton))
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            int shift = pressed ? 1 : 0;

            // 图标 + 文字整体水平居中
            using (Font f = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold, GraphicsUnit.Point))
            {
                int tw = TextRenderer.MeasureText(g, Text, f).Width;
                int iconW = icon != ButtonIcon.None ? 18 : 0;
                int gap = icon != ButtonIcon.None ? 7 : 0;
                int total = iconW + gap + tw;
                int startX = (Width - total) / 2;
                int cy = Height / 2 + shift;

                if (icon != ButtonIcon.None)
                {
                    Ui.DrawButtonIcon(g, icon, startX + iconW / 2, cy, Color.White);
                }
                TextRenderer.DrawText(g, Text, f,
                    new Rectangle(startX + iconW + gap, shift, tw + 4, Height), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            if (!Enabled)
            {
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                {
                    g.FillRectangle(dim, ClientRectangle);
                }
            }
        }
    }

    // ---------- 小号扁平按钮（日志工具栏） ----------

    class SmallButton : Button
    {
        bool hovering;
        bool pressed;
        bool isOn;

        public bool IsOn
        {
            get { return isOn; }
            set { isOn = value; Invalidate(); }
        }

        public SmallButton(string text)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font("Microsoft YaHei UI", 8.5F, GraphicsUnit.Point);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { pressed = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { pressed = false; Invalidate(); base.OnMouseUp(mevent); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.ClearBackground(g, this);

            Color bg;
            Color fg;
            if (isOn)
            {
                bg = Color.FromArgb(219, 234, 254);    // 淡蓝底 = 开关开启
                fg = Color.FromArgb(30, 64, 175);
            }
            else
            {
                bg = pressed ? Color.FromArgb(203, 213, 225)
                     : hovering ? Color.FromArgb(226, 232, 240)
                     : Color.FromArgb(238, 242, 247);
                fg = Color.FromArgb(71, 85, 105);
            }

            using (GraphicsPath path = Ui.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Theme.RadiusSmall))
            using (SolidBrush brush = new SolidBrush(bg))
            {
                g.FillPath(brush, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // 圆角白色卡片（日志区容器，带轻投影）
    class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.ClearBackground(g, this);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            // 底部轻投影（两圈半透明）
            using (GraphicsPath shadow = Ui.RoundedRect(new Rectangle(1, 2, Width - 3, Height - 3), Theme.RadiusCard))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(10, 15, 23, 42)))
            {
                g.FillPath(sb, shadow);
            }
            using (GraphicsPath shadow = Ui.RoundedRect(new Rectangle(0, 1, Width - 1, Height - 1), Theme.RadiusCard))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(7, 15, 23, 42)))
            {
                g.FillPath(sb, shadow);
            }

            using (GraphicsPath path = Ui.RoundedRect(r, Theme.RadiusCard))
            using (SolidBrush brush = new SolidBrush(Theme.Card))
            using (Pen pen = new Pen(Theme.Border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }
    }
}
