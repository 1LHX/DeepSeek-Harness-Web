// dsh-panel.cs - DeepSeek Harness Web 控制面板（原生 C# 版）
// 编译：build-dsh-panel.cmd（使用 Windows 自带 csc.exe，无需安装任何东西）
// 设计：单窗口常驻面板 + 系统托盘；服务后台隐藏启动，输出实时显示在日志区；
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
// v1.3（体验）：托盘图标随服务状态变色（绿/灰/琥珀，动态生成）；
//       托盘菜单新增：开机自启勾选 / 退出并停止服务 / 关于；托盘右键零阻塞（复用轮询状态）；
//       启动失败与停止超时托盘气泡告警；selftest 增强（Job 绑定 / 进程树清理 / err.log 基线）。
// v1.4（结构）：运行时产物目录化——日志迁至 logs/、PID 迁至 run/（含旧布局自动迁移）；
//       新增 .gitignore 与版本控制；新增 run-tests.cmd 一键冒烟测试；README 目录结构章节。
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

[assembly: AssemblyTitle("DeepSeek Harness Web Panel")]
[assembly: AssemblyProduct("dsh-panel")]
[assembly: AssemblyDescription("Control panel for DeepSeek Harness Web")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyVersion("1.4.2.0")]
[assembly: AssemblyFileVersion("1.4.2.0")]

namespace DshPanel
{
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

        // 应用 Logo：渐变圆角方块 + 白色粗体 D
        public static void DrawLogo(Graphics g, int x, int y, int size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(x, y, size, size);
            using (GraphicsPath path = RoundedRect(r, Math.Max(6, size / 4)))
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
        static IntPtr serviceJob = IntPtr.Zero;        // 启动时绑定的 Job Object（停止时整树终止）
        static Mutex singleMutex;
        internal static MainForm Instance;
        internal static Action onStopped;               // 停止完成后回调（「退出并停止服务」用）

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
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    LaunchService();
                    AttachServiceToJob();
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(ErrLog, "[start-service] " + ex.Message + Environment.NewLine, new UTF8Encoding(false)); } catch { }
                    ShowBalloon("启动失败", ex.Message, ToolTipIcon.Error);
                }
                finally
                {
                    opState = OpState.Idle;
                    RefreshUi();
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
            ThreadPool.QueueUserWorkItem(delegate
            {
                StopService();
                opState = OpState.Idle;
                RefreshUi();
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
                AttachServiceToJob();
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
    // 只读取新增字节（记住偏移）；文件被轮转/截断（变小）时从头重读；
    // 读取起点回退 4 字节并按最后一个换行丢弃，避免 UTF-8 多字节字符被截断产生乱码。

    class LogTail
    {
        int maxLines;
        long offset = -1;        // -1 = 尚未初始化（首次整读）
        string pending = "";     // 上次结尾的不完整行，等待下次拼接
        string cached = "";      // 当前展示的尾部文本

        public LogTail(int maxLines)
        {
            this.maxLines = maxLines;
        }

        public string Update(string file)
        {
            if (!File.Exists(file)) return cached;
            long len;
            try { len = new FileInfo(file).Length; } catch { return cached; }
            if (offset < 0) { offset = 0; pending = ""; }
            if (len == offset) return cached;
            if (len < offset) { offset = 0; pending = ""; cached = ""; }   // 被轮转/截断：旧内容失效，清空缓存

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
            catch { return cached; }
            offset = len;

            // 回退区处理：仅当本次读取包含回退区（offset>4 的增量读取）时才需要丢弃。
            // 回退区 = 上次偏移前 4 字节（用于避免 UTF-8 多字节字符被截断）；
            // 在回退区（解码后前 <=4 个字符）内从后向前找换行：找到则丢弃到它为止；
            // 找不到说明回退区在行中间，保留全部（最多开头出现少量碎片，罕见且无害）。
            // 首次读取（offset==0）与轮转后整读不存在截断问题，直接全取——
            // 旧实现用 LastIndexOf('\n') 无条件丢弃，当整块新内容以换行结尾时
            // 会把全部内容误当回退区丢弃，导致日志区永远显示为空。
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
            List<string> lines = new List<string>();
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < combined.Length; i++)
            {
                char c = combined[i];
                if (c == '\r') continue;
                if (c == '\n')
                {
                    lines.Add(cur.ToString());
                    cur.Length = 0;
                }
                else
                {
                    cur.Append(c);
                }
            }
            pending = cur.ToString();

            List<string> all = new List<string>();
            if (cached.Length > 0)
            {
                all.AddRange(cached.Split(new string[] { "\n" }, StringSplitOptions.None));
            }
            all.AddRange(lines);
            while (all.Count > maxLines) all.RemoveAt(0);
            cached = string.Join("\n", all.ToArray());
            return cached;
        }
    }

    // ---------- 主界面 ----------

    class MainForm : Form
    {
        GradientHeader header;
        StatusLine statusLine;
        Label lblHint;
        RoundedButton btnAction;
        RoundedButton btnBrowser;
        TextBox logBox;
        System.Threading.Timer pollTimer;
        System.Windows.Forms.Timer pulseTimer;
        bool closed;
        int pollBusy;
        bool lastPollRunning;

        LogTail outTail = new LogTail(15);
        LogTail errTail = new LogTail(5);
        string lastLogShown = "";

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
            ClientSize = new Size(440, 400);
            MinimumSize = new Size(380, 330);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;

            BuildUi();
            SetupTray();

            Shown += delegate
            {
                pollTimer = new System.Threading.Timer(PollTick, null, 0, 2000);
            };
            FormClosing += OnFormClosing;
            FormClosed += delegate
            {
                closed = true;
                if (pollTimer != null) pollTimer.Dispose();
                if (pulseTimer != null) pulseTimer.Dispose();
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
        bool IsAutoStartEnabled()
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
                        using (GraphicsPath path = Ui.RoundedRect(new Rectangle(1, 1, 14, 14), 3))
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
            layout.RowCount = 6;
            layout.Padding = new Padding(0);
            layout.BackColor = Theme.Bg;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));    // 0: 渐变头部
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));    // 1: 状态行
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));    // 2: 提示
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));    // 3: 按钮
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));    // 4: 日志标题
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 5: 日志卡片

            // ---- 渐变头部：Logo + 标题 + URL + 状态徽章 ----
            header = new GradientHeader();
            header.Dock = DockStyle.Fill;
            header.SetUrl(Program.Url);

            // ---- 状态行：指示灯 + 状态文字 ----
            statusLine = new StatusLine();
            statusLine.Dock = DockStyle.Fill;
            statusLine.SetStatus("检测中…", Theme.TextMuted, false);

            // ---- 提示 ----
            lblHint = new Label();
            lblHint.Text = "自动检测端口 · 启动服务后自动打开浏览器 · 关闭窗口即最小化到托盘";
            lblHint.Dock = DockStyle.Fill;
            lblHint.TextAlign = ContentAlignment.MiddleCenter;
            lblHint.Font = new Font("Microsoft YaHei UI", 8F);
            lblHint.ForeColor = Theme.TextFaint;
            lblHint.BackColor = Theme.Bg;

            // ---- 按钮 ----
            btnAction = new RoundedButton(Theme.Green, Theme.GreenHover, Theme.GreenPress);
            btnAction.Text = "启动服务";
            btnAction.SetIcon(ButtonIcon.Play);
            btnAction.Dock = DockStyle.Fill;
            btnAction.Margin = new Padding(0);
            btnAction.Click += delegate
            {
                try
                {
                    if (Program.opState != Program.OpState.Idle)
                    {
                        return; // 启动/停止中：忽略重复点击（按钮本身已禁用）
                    }
                    // 用最近一次轮询结果决定动作，点击零阻塞（不做同步端口探测）
                    if (Program.lastRunning) Program.StopServiceAsync(); else Program.StartServiceAsync();
                    TryBeginInvoke(delegate { PollTick(null); });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "操作失败：" + ex.Message, "错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            btnBrowser = new RoundedButton(Theme.Blue, Theme.BlueHover, Theme.BluePress);
            btnBrowser.Text = "打开浏览器";
            btnBrowser.SetIcon(ButtonIcon.Globe);
            btnBrowser.Dock = DockStyle.Fill;
            btnBrowser.Margin = new Padding(0);
            btnBrowser.Click += delegate
            {
                if (Program.IsPortOpen())
                {
                    Program.OpenBrowser();
                }
                else
                {
                    MessageBox.Show(this, "服务未运行，请先点击「启动服务」。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            TableLayoutPanel btnRow = new TableLayoutPanel();
            btnRow.Dock = DockStyle.Fill;
            btnRow.Margin = new Padding(22, 4, 22, 4);
            btnRow.BackColor = Theme.Bg;
            btnRow.ColumnCount = 5;
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12F));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 8F));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12F));
            btnRow.Controls.Add(btnAction, 1, 0);
            btnRow.Controls.Add(btnBrowser, 3, 0);

            // ---- 日志标题行 ----
            TableLayoutPanel logTitleRow = new TableLayoutPanel();
            logTitleRow.Dock = DockStyle.Fill;
            logTitleRow.ColumnCount = 2;
            logTitleRow.BackColor = Theme.Bg;
            logTitleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            logTitleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label lblLogTitle = new Label();
            lblLogTitle.Text = "运行日志";
            lblLogTitle.Dock = DockStyle.Fill;
            lblLogTitle.TextAlign = ContentAlignment.MiddleLeft;
            lblLogTitle.Padding = new Padding(24, 0, 0, 0);
            lblLogTitle.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            lblLogTitle.ForeColor = Theme.TextMuted;
            lblLogTitle.BackColor = Theme.Bg;

            Label lblLogHint = new Label();
            lblLogHint.Text = "每 2 秒自动刷新";
            lblLogHint.Dock = DockStyle.Fill;
            lblLogHint.TextAlign = ContentAlignment.MiddleRight;
            lblLogHint.Padding = new Padding(0, 0, 24, 0);
            lblLogHint.Font = new Font("Microsoft YaHei UI", 8F);
            lblLogHint.ForeColor = Theme.TextFaint;
            lblLogHint.BackColor = Theme.Bg;

            logTitleRow.Controls.Add(lblLogTitle, 0, 0);
            logTitleRow.Controls.Add(lblLogHint, 1, 0);

            // ---- 日志卡片 ----
            CardPanel logCard = new CardPanel();
            logCard.Dock = DockStyle.Fill;
            logCard.Margin = new Padding(14, 2, 14, 14);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.Dock = DockStyle.Fill;
            logBox.BorderStyle = BorderStyle.None;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Font = new Font("Consolas", 9F);
            logBox.BackColor = Theme.Card;
            logBox.ForeColor = Theme.TextStrong;
            logBox.TabStop = false;
            logCard.Controls.Add(logBox);

            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(statusLine, 0, 1);
            layout.Controls.Add(lblHint, 0, 2);
            layout.Controls.Add(btnRow, 0, 3);
            layout.Controls.Add(logTitleRow, 0, 4);
            layout.Controls.Add(logCard, 0, 5);
            Controls.Add(layout);
        }

        // 后台线程轮询：仅在实际变化时才刷新 UI；防重入；附带崩溃自动恢复检测
        void PollTick(object state)
        {
            if (closed) return;
            if (Interlocked.Exchange(ref pollBusy, 1) != 0) return;
            try
            {
                bool running = Program.IsPortOpen();
                Program.lastRunning = running;

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
                                TryBeginInvoke(delegate
                                {
                                    try { if (notify != null) notify.ShowBalloonTip(3000, "dsh-panel", "服务多次自动恢复失败，已停止自动恢复。请手动检查。", ToolTipIcon.Warning); } catch { }
                                });
                            }
                            else
                            {
                                Program.lastRestartTick = now;
                                Program.autoRestartCount++;
                                Program.StartServiceAsync();
                            }
                        }
                    }
                }
                if (running)
                {
                    Program.everRan = true;
                    if (Program.autoRestartCount > 0 && Environment.TickCount - Program.lastRestartTick > 20000)
                    {
                        Program.autoRestartCount = 0;
                    }
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
                string btnText;
                Color btnNormal;
                Color btnHover;
                Color btnPress;
                ButtonIcon btnIcon;
                if (isStarting)
                {
                    statusText = "启动中…";
                    statusColor = Theme.Amber;
                    btnText = "启动中…";
                    btnNormal = Color.FromArgb(148, 163, 184);
                    btnHover = Color.FromArgb(148, 163, 184);
                    btnPress = Color.FromArgb(148, 163, 184);
                    btnIcon = ButtonIcon.Play;
                }
                else if (isStopping)
                {
                    statusText = "停止中…";
                    statusColor = Theme.Amber;
                    btnText = "停止中…";
                    btnNormal = Color.FromArgb(148, 163, 184);
                    btnHover = Color.FromArgb(148, 163, 184);
                    btnPress = Color.FromArgb(148, 163, 184);
                    btnIcon = ButtonIcon.Stop;
                }
                else
                {
                    statusText = running ? "运行中" : "未运行";
                    statusColor = running ? Theme.Green : Theme.Red;
                    btnText = running ? "停止服务" : "启动服务";
                    btnIcon = running ? ButtonIcon.Stop : ButtonIcon.Play;
                    if (running)
                    {
                        btnNormal = Theme.Red; btnHover = Theme.RedHover; btnPress = Theme.RedPress;
                    }
                    else
                    {
                        btnNormal = Theme.Green; btnHover = Theme.GreenHover; btnPress = Theme.GreenPress;
                    }
                }
                bool btnEnabled = !(isStarting || isStopping);
                bool pulse = isStarting || isStopping;
                string logText = GetLogTail();

                // 本次启动成功后端口首次就绪时自动打开浏览器一次
                if (running && Program.autoOpenPending)
                {
                    Program.autoOpenPending = false;
                    TryBeginInvoke(delegate { Program.OpenBrowser(); });
                }

                if (statusLine.StatusText != statusText ||
                    statusLine.StatusColor != statusColor ||
                    statusLine.Pulse != pulse ||
                    header.BadgeText != statusText ||
                    header.BadgeColor != statusColor ||
                    btnAction.Text != btnText ||
                    btnAction.Icon != btnIcon ||
                    btnAction.Enabled != btnEnabled ||
                    logText != lastLogShown)
                {
                    string st = statusText; Color sc = statusColor;
                    string bt = btnText; Color bn = btnNormal; Color bh = btnHover; Color bp = btnPress;
                    ButtonIcon bi = btnIcon;
                    bool be = btnEnabled;
                    bool pu = pulse;
                    bool run = running;
                    string lt = logText;
                    TryBeginInvoke(delegate
                    {
                        try
                        {
                            statusLine.SetStatus(st, sc, pu);
                            header.SetBadge(st, sc);
                            btnAction.Text = bt;
                            btnAction.SetColors(bn, bh, bp);
                            btnAction.SetIcon(bi);
                            btnAction.Enabled = be;
                            // 托盘状态图标：绿=运行 / 灰=未运行 / 琥珀=启动停止中
                            Color dotColor = pu ? Theme.Amber : (run ? Theme.Green : Color.FromArgb(148, 163, 184));
                            SetTrayStateIcon(dotColor);
                            if (pu)
                            {
                                if (pulseTimer == null)
                                {
                                    pulseTimer = new System.Windows.Forms.Timer();
                                    pulseTimer.Interval = 50;
                                    pulseTimer.Tick += delegate
                                    {
                                        if (!statusLine.StepPulse()) pulseTimer.Stop();
                                    };
                                }
                                if (!pulseTimer.Enabled) pulseTimer.Start();
                            }
                            else
                            {
                                if (pulseTimer != null && pulseTimer.Enabled) pulseTimer.Stop();
                                statusLine.StopPulse();
                            }
                            if (lt != lastLogShown)
                            {
                                logBox.Text = lt;
                                lastLogShown = lt;
                                logBox.SelectionStart = logBox.TextLength;
                                logBox.ScrollToCaret();
                            }
                        }
                        catch { }
                    });
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref pollBusy, 0);
            }
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

        // 日志读取：增量读取（只读新增部分），out 保留 15 行、err 保留 5 行
        string GetLogTail()
        {
            string o = outTail.Update(Program.OutLog);
            string e = errTail.Update(Program.ErrLog);
            if (string.IsNullOrEmpty(o) && string.IsNullOrEmpty(e))
            {
                return "（暂无日志：点击「启动服务」后，这里会实时显示服务输出）";
            }
            string joined = o;
            if (!string.IsNullOrEmpty(e))
            {
                joined = joined.Length > 0 ? joined + Environment.NewLine + e : e;
            }
            return joined;
        }
    }

    // ---------- 渐变头部（Logo + 标题 + URL + 状态胶囊徽章） ----------

    class GradientHeader : Panel
    {
        string url = "";
        string badgeText = "未运行";
        Color badgeColor = Theme.Red;

        public string BadgeText { get { return badgeText; } }
        public Color BadgeColor { get { return badgeColor; } }

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

        public void SetBadge(string text, Color color)
        {
            badgeText = text;
            badgeColor = color;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 主渐变
            using (LinearGradientBrush brush = new LinearGradientBrush(ClientRectangle,
                Theme.Indigo, Theme.Violet, 45F))
            {
                g.FillRectangle(brush, ClientRectangle);
            }
            // 右上装饰圆（微弱）
            using (SolidBrush deco = new SolidBrush(Color.FromArgb(24, 255, 255, 255)))
            {
                g.FillEllipse(deco, Width - 64, -34, 108, 108);
            }
            // 底部 1px 深色分隔线
            using (SolidBrush line = new SolidBrush(Color.FromArgb(46, 15, 23, 42)))
            {
                g.FillRectangle(line, 0, Height - 1, Width, 1);
            }

            // Logo
            Ui.DrawLogo(g, 20, (Height - 38) / 2, 38);

            // 标题
            using (Font f = new Font("Segoe UI", 13.5F, FontStyle.Bold, GraphicsUnit.Point))
            {
                TextRenderer.DrawText(g, "DeepSeek Harness Web", f,
                    new Rectangle(70, 11, Width - 180, 26), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
            // URL
            using (Font f = new Font("Consolas", 9.5F, GraphicsUnit.Point))
            {
                TextRenderer.DrawText(g, url, f,
                    new Rectangle(71, 40, Width - 180, 20), Color.FromArgb(224, 231, 255),
                    TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }

            // 右侧状态胶囊徽章
            DrawBadge(g);
        }

        void DrawBadge(Graphics g)
        {
            using (Font f = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point))
            {
                int tw = TextRenderer.MeasureText(g, badgeText, f).Width;
                int bw = tw + 30;
                int bx = Width - bw - 16;
                int by = (Height - 24) / 2;
                Rectangle cap = new Rectangle(bx, by, bw, 24);

                using (GraphicsPath path = Ui.RoundedRect(cap, 12))
                using (SolidBrush fill = new SolidBrush(badgeColor))
                {
                    g.FillPath(fill, path);
                }
                // 白色小圆点
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                {
                    g.FillEllipse(dot, bx + 10, by + 9, 6, 6);
                }
                TextRenderer.DrawText(g, badgeText, f,
                    new Rectangle(bx + 18, by, bw - 20, 24), Color.White,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
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

            Color fill = Enabled ? currentColor : Ui.Darken(currentColor, 0.72F);
            using (GraphicsPath path = Ui.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 9))
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

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            // 底部轻投影（两圈半透明）
            using (GraphicsPath shadow = Ui.RoundedRect(new Rectangle(1, 2, Width - 3, Height - 3), 10))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(10, 15, 23, 42)))
            {
                g.FillPath(sb, shadow);
            }
            using (GraphicsPath shadow = Ui.RoundedRect(new Rectangle(0, 1, Width - 1, Height - 1), 10))
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(7, 15, 23, 42)))
            {
                g.FillPath(sb, shadow);
            }

            using (GraphicsPath path = Ui.RoundedRect(r, 10))
            using (SolidBrush brush = new SolidBrush(Theme.Card))
            using (Pen pen = new Pen(Theme.Border))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }
    }
}
