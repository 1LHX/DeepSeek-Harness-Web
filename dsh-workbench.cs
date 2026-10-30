// dsh-workbench.cs - 工作台（v3.5）：把「控制面板」升级成「能干活的工作台」
//
// 面板原本只负责启动/停止/配置服务；这个文件补上真正干活需要的四件事：
//   1) 文件浏览与预览（+ @ 引用到 DSH 输入框）
//   2) 内嵌终端（命令白名单 / 危险命令二次确认 / 每次执行留痕）
//   3) 任务看板（直接读 DSH 会话事件流里的 todo/write 与 goal/change——
//      不是猜，也不是截图，是 Agent 真实写下的清单）
//   4) 变更审阅（工作区基线快照 + 行级 diff + 接受/回滚/预览后 Apply）与 Git 视图
//
// 界面在 workbench/ui.html，由 MainForm 通过 AddScriptToExecuteOnDocumentCreated
// 注入到 DSH 页面里（Shadow DOM，不碰 DSH 自己的 DOM/CSS）；两边用 postMessage 通信。
//
// 安全边界（改动本项目「面板自身不启动进程」这条老约定，写清楚原因）：
//   * 终端与 git 必须执行用户/自己给的程序，C# 里只有 Process.Start 一条路，
//     所以本文件是**唯一**允许起进程的地方，命令白名单与危险命令确权也在这里（不在 JS 里）；
//   * 会话边车只跑固定的 workbench/session-reader.mjs，参数只有工作区路径；
//   * 文件操作一律锁在工作区根目录内（IsUnder 校验），越界直接拒绝；
//   * 写文件先写临时文件再 Replace，避免半截文件。
//
// 注意：csc v4.0.30319 只支持 C# 5.0，勿使用字符串插值 / ?. / 表达式体等新语法。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshPanel
{
internal static class Wb
{
    internal const string Version = "3.7.1";

    // ---------- 上限（都要有，否则一次误操作就能把面板拖死） ----------
    const int MaxTextRead = 2 * 1024 * 1024;        // 文本预览上限
    const int MaxImageRead = 12 * 1024 * 1024;      // 图片预览上限
    const int MaxBaseFile = 512 * 1024;             // 纳入基线的单个文件上限
    const int MaxBaselineFiles = 4000;              // 基线文件数上限
    const int MaxTermBytes = 900 * 1024;            // 单次命令输出上限（超出丢弃并提示）
    const int MaxAgentEvents = 400;                 // 活动流保留条数
    const int DiffCap = 6000;                       // 超过这么多行只做「整块替换」diff

    // ---------- 状态 ----------
    internal static string Root = "";               // 工作区（绝对路径）
    internal static string DataDir = "";            // run/workbench
    internal static string BaseDir = "";            // run/workbench/baseline
    static string StateFile = "";
    static MainForm host;
    static WebView2 web;
    static JavaScriptSerializer js;
    static bool inited;
    static System.Windows.Forms.Timer flushTimer;

    // ---------- 工具函数：JSON ----------
    static JavaScriptSerializer Js
    {
        get
        {
            if (js == null)
            {
                js = new JavaScriptSerializer();
                js.MaxJsonLength = 32 * 1024 * 1024;
                js.RecursionLimit = 200;
            }
            return js;
        }
    }

    static Dictionary<string, object> Obj()
    {
        return new Dictionary<string, object>();
    }

    static string S(Dictionary<string, object> d, string k)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return "";
        return v as string ?? v.ToString();
    }

    static int I(Dictionary<string, object> d, string k, int def)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
        try
        {
            if (v is int) return (int)v;
            if (v is long) return (int)(long)v;
            if (v is double) return (int)(double)v;
            int r;
            if (int.TryParse(v.ToString(), out r)) return r;
        }
        catch { }
        return def;
    }

    static bool B(Dictionary<string, object> d, string k, bool def)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
        if (v is bool) return (bool)v;
        string s = v.ToString().Trim().ToLowerInvariant();
        return s == "1" || s == "true" || s == "yes" || s == "on";
    }

    static string[] A(Dictionary<string, object> d, string k)
    {
        object v;
        if (d == null || !d.TryGetValue(k, out v) || v == null) return new string[0];
        object[] arr = v as object[];
        if (arr == null) return new string[0];
        List<string> list = new List<string>();
        for (int i = 0; i < arr.Length; i++) if (arr[i] != null) list.Add(arr[i].ToString());
        return list.ToArray();
    }

    static string Json(object o)
    {
        try { return Js.Serialize(o); }
        catch (Exception ex) { return "{\"ok\":false,\"error\":\"序列化失败：" + Escape(ex.Message) + "\"}"; }
    }

    static string Escape(string s)
    {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' || c == '\\') { sb.Append('\\'); sb.Append(c); }
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ---------- 与页面通信 ----------
    static void Push(Dictionary<string, object> msg)
    {
        if (web == null) return;
        string json = Json(msg);
        try
        {
            if (web.InvokeRequired) web.BeginInvoke((MethodInvoker)delegate { Post(json); });
            else Post(json);
        }
        catch { }
    }

    static void Post(string json)
    {
        try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json); }
        catch { }
    }

    static void PushEvent(string kind, Dictionary<string, object> data)
    {
        Dictionary<string, object> m = Obj();
        m["push"] = kind;
        if (data != null) foreach (KeyValuePair<string, object> kv in data) m[kv.Key] = kv.Value;
        Push(m);
    }

    static Dictionary<string, object> Ok(Dictionary<string, object> data)
    {
        Dictionary<string, object> m = Obj();
        m["ok"] = true;
        m["data"] = data ?? Obj();
        return m;
    }

    static Dictionary<string, object> Fail(string text)
    {
        Dictionary<string, object> m = Obj();
        m["ok"] = false;
        m["error"] = text;
        return m;
    }

    // ---------- 初始化 / 注入 ----------
    // 注意：AddScriptToExecuteOnDocumentCreatedAsync 只对**之后**创建的文档生效，
    // 而且自己是异步的——所以这里 await 它，再加一次 ExecuteScript 覆盖当前文档，
    // 否则「面板先有页面、后注册脚本」时会一个界面都不出现（v3.5 踩过）。
    internal static async System.Threading.Tasks.Task AttachAsync(MainForm h, WebView2 w)
    {
        host = h;
        web = w;
        if (inited)
        {
            await InjectAsync();
            return;
        }
        inited = true;

        DataDir = Path.Combine(Program.ScriptRoot, "run", "workbench");
        BaseDir = Path.Combine(DataDir, "baseline");
        StateFile = Path.Combine(DataDir, "state.json");
        try { Directory.CreateDirectory(DataDir); Directory.CreateDirectory(BaseDir); } catch { }

        LoadState();
        if (string.IsNullOrEmpty(Root) || !Directory.Exists(Root)) Root = Program.ScriptRoot;

        try
        {
            web.CoreWebView2.WebMessageReceived += OnWebMessage;
            web.CoreWebView2.NavigationCompleted += delegate { ReInject(); };
        }
        catch (Exception ex) { Program.LogEvent(LogKind.Warn, "工作台桥接注册失败：" + ex.Message); }

        flushTimer = new System.Windows.Forms.Timer();
        flushTimer.Interval = 120;
        flushTimer.Tick += delegate { FlushOutput(); };
        flushTimer.Start();

        EnsureBaselineLoaded();
        StartSidecar();
        Program.LogEvent(LogKind.Info, "工作台已就绪（工作区：" + Root + "）");
        await InjectAsync();
    }

    internal static void Shutdown()
    {
        try { if (flushTimer != null) flushTimer.Stop(); } catch { }
        StopSidecar();
        foreach (string k in new List<string>(runs.Keys)) KillRun(k);
    }

    // 注入：把 workbench/ui.html 拆成 style / markup / script 三段再拼成一段脚本。
    // 用 Shadow DOM + <style> 元素注入，不碰 DSH 页面自身的 DOM 与样式。
    static string injectionCache;

    static string BuildInjection()
    {
        if (injectionCache != null) return injectionCache;
        string text = LoadUiSource();
        if (text == null) return "";
        string style = Section(text, "STYLE");
        string markup = Section(text, "MARKUP");
        string script = Section(text, "SCRIPT");
        StringBuilder sb = new StringBuilder(style.Length + markup.Length + script.Length + 512);
        sb.Append("(function(){try{\n");
        sb.Append("if(location.protocol!=='http:'||location.hostname!=='127.0.0.1'&&location.hostname!=='localhost'){return;}\n");
        sb.Append("if(window.__DSH_WORKBENCH__){return;}\n");
        sb.Append("var __WB_CSS__=").Append(JsonString(style)).Append(";\n");
        sb.Append("var __WB_HTML__=").Append(JsonString(markup)).Append(";\n");
        sb.Append("var __WB_VER__=").Append(JsonString(Version)).Append(";\n");
        sb.Append(script);
        sb.Append("\n}catch(e){try{console.error('[workbench]',e);}catch(_){}}})();\n");
        injectionCache = sb.ToString();
        return injectionCache;
    }

    static string JsonString(string s)
    {
        if (s == null) return "\"\"";
        StringBuilder sb = new StringBuilder(s.Length + 32);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            // 把 "</" 转义成 "\x3c/"（防止 payload 被塞进 HTML 上下文时提前闭合 script）。
            // 注意必须把 "/" 一起吃掉——v3.5 这里漏了 i++，于是 "</div>" 变成 "<//div>"，
            // 注入后整个工作台界面被 HTML 解析器当成注释吞掉，只剩几个空 div。
            else if (c == '<' && i + 1 < s.Length && s[i + 1] == '/') { sb.Append("\\x3c\\/"); i++; }
            else if (c < ' ') sb.Append(" ");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    // 源文件优先（改完 html 重启面板即生效），找不到退回 exe 内嵌资源
    static string LoadUiSource()
    {
        try
        {
            string disk = Path.Combine(Program.ScriptRoot, "workbench", "ui.html");
            if (File.Exists(disk))
            {
                string t = File.ReadAllText(disk, Encoding.UTF8);
                if (t.IndexOf("id=\"wb-root\"", StringComparison.Ordinal) >= 0 || t.IndexOf("<!--@SCRIPT@-->", StringComparison.Ordinal) >= 0) return t;
            }
        }
        catch { }
        try
        {
            System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("dsh-workbench-ui.html"))
            {
                if (s == null) return null;
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    return r.ReadToEnd();
                }
            }
        }
        catch { return null; }
    }

    // 按 <!--@STYLE@--> / <!--@MARKUP@--> / <!--@SCRIPT@--> 切分
    static string Section(string text, string name)
    {
        string begin = "<!--@" + name + "@-->";
        string end = "<!--@/" + name + "@-->";
        int a = text.IndexOf(begin, StringComparison.Ordinal);
        if (a < 0) return "";
        a += begin.Length;
        int b = text.IndexOf(end, a, StringComparison.Ordinal);
        if (b < 0) b = text.Length;
        return text.Substring(a, b - a);
    }

    // NavigationCompleted 是 void 委托，这里把 Task 丢掉（失败在 InjectAsync 内部已记录）
    static void ReInject()
    {
        System.Threading.Tasks.Task ignored = InjectAsync();
        GC.KeepAlive(ignored);
    }

    internal static async System.Threading.Tasks.Task InjectAsync()
    {
        string payload = BuildInjection();
        if (payload.Length == 0)
        {
            Program.LogEvent(LogKind.Warn, "工作台界面为空：workbench/ui.html 缺失或分节标记被改坏");
            return;
        }
        try
        {
            if (web == null || web.CoreWebView2 == null) return;
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(payload);
            try { await web.CoreWebView2.ExecuteScriptAsync(payload); }
            catch (Exception ex2) { Program.BootMark("wb-inject-current-failed: " + ex2.Message); }
            Program.BootMark("wb-inject: " + payload.Length + " chars");
        }
        catch (Exception ex) { Program.LogEvent(LogKind.Warn, "工作台注入失败：" + ex.Message); }
    }

    // 顶栏按钮 / 快捷键：在已加载页面里切出工作台抽屉
    internal static void ToggleUi()
    {
        try
        {
            if (web == null || web.CoreWebView2 == null) return;
            web.CoreWebView2.ExecuteScriptAsync("window.__DSH_WORKBENCH__ && window.__DSH_WORKBENCH__.toggle();");
        }
        catch { }
    }

    internal static void OpenUi(string tab)
    {
        try
        {
            if (web == null || web.CoreWebView2 == null) return;
            string arg = string.IsNullOrEmpty(tab) ? "" : ", " + JsonString(tab);
            web.CoreWebView2.ExecuteScriptAsync("window.__DSH_WORKBENCH__ && window.__DSH_WORKBENCH__.open(" + arg.TrimStart(' ', ',') + ");");
        }
        catch { }
    }

    // ---------- 消息分发 ----------
    static void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = null;
        try { raw = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrEmpty(raw)) return;
        Dictionary<string, object> msg = null;
        try { msg = Js.DeserializeObject(raw) as Dictionary<string, object>; }
        catch (Exception ex) { Program.LogEvent(LogKind.Warn, "工作台消息解析失败：" + ex.Message); return; }
        if (msg == null) return;

        string op = S(msg, "op");
        string id = S(msg, "id");

        // 少数必须留在 UI 线程的操作（弹窗、WebView2 控制）
        if (op == "ws.pick")
        {
            Reply(id, PickWorkspace());
            return;
        }
        if (op == "ui.toast")
        {
            Program.LogEvent(LogKind.Info, "工作台：" + S(msg, "text"));
            Reply(id, Ok(null));
            return;
        }

        ThreadPool.QueueUserWorkItem(delegate
        {
            Dictionary<string, object> res;
            try { res = Dispatch(op, msg); }
            catch (Exception ex) { res = Fail(ex.Message); }
            res["id"] = id;
            Push(res);
        });
    }

    static void Reply(string id, Dictionary<string, object> res)
    {
        res["id"] = id;
        Push(res);
    }

    static Dictionary<string, object> Dispatch(string op, Dictionary<string, object> msg)
    {
        switch (op)
        {
            case "hello": return Ok(Hello());
            case "ws.set": return SetWorkspace(msg);
            case "fs.list": return Ok(FsList(msg));
            case "fs.read": return Ok(FsRead(msg));
            case "fs.save": return FsSave(msg);
            case "fs.preview": return Ok(FsPreview(msg));
            case "fs.search": return Ok(FsSearch(msg));
            case "fs.reveal": return FsReveal(msg);
            case "term.policy": return Ok(TermPolicy(S(msg, "shell"), S(msg, "command")));
            case "term.run": return TermRun(msg);
            case "term.kill": return TermKill(msg);
            case "git.info": return Ok(GitInfo());
            case "git.diff": return Ok(GitDiff(msg));
            case "changes.list": return Ok(ChangesList());
            case "changes.diff": return Ok(ChangesDiff(msg));
            case "changes.accept": return ChangesAccept(msg);
            case "changes.revert": return ChangesRevert(msg);
            case "changes.snapshot": return ChangesSnapshot();
            case "agent.state": return Ok(AgentState());
            default: return Fail("未知操作：" + op);
        }
    }

    // ---------- 工作区 ----------
    static Dictionary<string, object> Hello()
    {
        Dictionary<string, object> d = Obj();
        d["version"] = Version;
        d["workspace"] = Root;
        d["workspaceName"] = Path.GetFileName(Root.TrimEnd('\\'));
        d["hasNode"] = !string.IsNullOrEmpty(FindNodeExeCached);
        d["hasGit"] = !string.IsNullOrEmpty(FindGit());
        d["shells"] = ShellList();
        d["agent"] = AgentState();
        d["git"] = SafeGitInfo();
        d["changes"] = SafeChangesSummary();
        return d;
    }

    static Dictionary<string, object> SetWorkspace(Dictionary<string, object> msg)
    {
        string p = S(msg, "path");
        if (string.IsNullOrEmpty(p)) return Fail("路径为空");
        try { p = Path.GetFullPath(p); }
        catch { return Fail("路径无效：" + p); }
        if (!Directory.Exists(p)) return Fail("目录不存在：" + p);
        Root = p;
        SaveState();
        ResetBaseline();
        StartSidecar();
        Dictionary<string, object> d = Hello();
        return Ok(d);
    }

    static Dictionary<string, object> PickWorkspace()
    {
        try
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择工作区目录（DSH 的工作目录）";
                dlg.ShowNewFolderButton = false;
                dlg.SelectedPath = Root;
                if (dlg.ShowDialog(host) != DialogResult.OK) return Fail("已取消");
                return SetWorkspace(new Dictionary<string, object> { { "path", dlg.SelectedPath } });
            }
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    static void LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            Dictionary<string, object> d = Js.DeserializeObject(File.ReadAllText(StateFile, Encoding.UTF8)) as Dictionary<string, object>;
            if (d == null) return;
            string ws = S(d, "workspace");
            if (!string.IsNullOrEmpty(ws) && Directory.Exists(ws)) Root = ws;
        }
        catch { }
    }

    static void SaveState()
    {
        try
        {
            Dictionary<string, object> d = Obj();
            d["workspace"] = Root;
            File.WriteAllText(StateFile, Json(d), new UTF8Encoding(false));
        }
        catch { }
    }

    // ---------- 路径安全 ----------
    static bool IsUnder(string root, string p)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(p)) return false;
        string r = Path.GetFullPath(root).TrimEnd('\\', '/');
        string q = Path.GetFullPath(p);
        if (string.Equals(r, q, StringComparison.OrdinalIgnoreCase)) return true;
        return q.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string Resolve(string rel)
    {
        string p;
        if (string.IsNullOrEmpty(rel)) p = Root;
        else if (Path.IsPathRooted(rel)) p = rel;
        else p = Path.Combine(Root, rel);
        p = Path.GetFullPath(p);
        if (!IsUnder(Root, p)) throw new Exception("路径超出工作区：" + rel);
        return p;
    }

    static string Rel(string p)
    {
        try
        {
            string root = Path.GetFullPath(Root).TrimEnd('\\', '/');
            string full = Path.GetFullPath(p);
            if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return ".";
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length + 1).Replace('\\', '/');
            return full;
        }
        catch { return p; }
    }

    // ---------- 文件浏览 ----------
    static readonly string[] SkipDirs = { ".git", "node_modules", ".vs", "bin", "obj", "dist", "target", "__pycache__", ".next", ".venv", "venv", ".dsh-cache" };
    static readonly string[] SkipTopDirs = { ".mimosa", ".zcode" };

    static bool SkipDir(string name, bool isTop)
    {
        string n = name.ToLowerInvariant();
        for (int i = 0; i < SkipDirs.Length; i++) if (n == SkipDirs[i]) return true;
        if (isTop) for (int i = 0; i < SkipTopDirs.Length; i++) if (n == SkipTopDirs[i]) return true;
        return false;
    }

    static Dictionary<string, object> FsList(Dictionary<string, object> msg)
    {
        string dir = Resolve(S(msg, "path"));
        bool all = B(msg, "all", false);
        bool isTop = string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        List<object> items = new List<object>();
        Dictionary<string, object> d = Obj();
        d["path"] = Rel(dir);
        if (!Directory.Exists(dir)) { d["items"] = items; d["error"] = "目录不存在"; return d; }
        try
        {
            DirectoryInfo di = new DirectoryInfo(dir);
            List<DirectoryInfo> dirs = new List<DirectoryInfo>();
            List<FileInfo> files = new List<FileInfo>();
            foreach (DirectoryInfo sub in di.GetDirectories())
            {
                if ((sub.Attributes & FileAttributes.Hidden) != 0 && !all) continue;
                if (!all && SkipDir(sub.Name, isTop)) continue;
                dirs.Add(sub);
            }
            foreach (FileInfo f in di.GetFiles())
            {
                if ((f.Attributes & FileAttributes.Hidden) != 0 && !all) continue;
                files.Add(f);
            }
            dirs.Sort(delegate(DirectoryInfo a, DirectoryInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            files.Sort(delegate(FileInfo a, FileInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            foreach (DirectoryInfo sub in dirs)
            {
                Dictionary<string, object> it = Obj();
                it["name"] = sub.Name;
                it["path"] = Rel(sub.FullName);
                it["dir"] = true;
                it["mtime"] = sub.LastWriteTimeUtc.Ticks / 10000;
                items.Add(it);
            }
            foreach (FileInfo f in files)
            {
                Dictionary<string, object> it = Obj();
                it["name"] = f.Name;
                it["path"] = Rel(f.FullName);
                it["dir"] = false;
                it["size"] = f.Length;
                it["ext"] = f.Extension.ToLowerInvariant().TrimStart('.');
                it["mtime"] = f.LastWriteTimeUtc.Ticks / 10000;
                items.Add(it);
            }
        }
        catch (Exception ex) { d["error"] = ex.Message; }
        d["items"] = items;
        return d;
    }

    static readonly string[] ImageExt = { "png", "jpg", "jpeg", "gif", "webp", "bmp", "ico", "svg" };

    static bool IsImage(string path)
    {
        string e = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        for (int i = 0; i < ImageExt.Length; i++) if (e == ImageExt[i]) return true;
        return false;
    }

    static bool LooksBinary(byte[] buf, int len)
    {
        int n = Math.Min(len, 8192);
        for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
        return false;
    }

    static Dictionary<string, object> FsRead(Dictionary<string, object> msg)
    {
        string path = Resolve(S(msg, "path"));
        Dictionary<string, object> d = Obj();
        d["path"] = Rel(path);
        if (Directory.Exists(path)) { d["kind"] = "dir"; return d; }
        if (!File.Exists(path)) { d["kind"] = "missing"; d["error"] = "文件不存在"; return d; }
        FileInfo fi = new FileInfo(path);
        d["size"] = fi.Length;
        d["mtime"] = fi.LastWriteTimeUtc.Ticks / 10000;
        try
        {
            if (IsImage(path))
            {
                if (fi.Length > MaxImageRead) { d["kind"] = "big"; d["error"] = "图片过大（>12MB）"; return d; }
                byte[] raw = File.ReadAllBytes(path);
                string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                string mime = ext == "svg" ? "image/svg+xml" : "image/" + (ext == "jpg" ? "jpeg" : ext);
                d["kind"] = "image";
                d["dataUrl"] = "data:" + mime + ";base64," + Convert.ToBase64String(raw);
                return d;
            }
            if (fi.Length > MaxTextRead) { d["kind"] = "big"; d["error"] = "文件过大（>2MB），不做预览"; return d; }
            byte[] bytes = File.ReadAllBytes(path);
            if (LooksBinary(bytes, bytes.Length)) { d["kind"] = "binary"; d["error"] = "二进制文件（" + bytes.Length + " 字节）"; return d; }
            string text = DecodeText(bytes);
            d["kind"] = "text";
            d["text"] = text;
            d["lang"] = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return d;
        }
        catch (Exception ex) { d["kind"] = "error"; d["error"] = ex.Message; return d; }
    }

    // UTF-8（带/不带 BOM）优先；解不出来按系统 ANSI 兜底
    static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        try
        {
            UTF8Encoding strict = new UTF8Encoding(false, true);
            return strict.GetString(bytes);
        }
        catch { return Encoding.Default.GetString(bytes); }
    }

    static Dictionary<string, object> FsSave(Dictionary<string, object> msg)
    {
        string path = Resolve(S(msg, "path"));
        string text = S(msg, "text");
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".wb-tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            Dictionary<string, object> d = Obj();
            d["path"] = Rel(path);
            d["size"] = new FileInfo(path).Length;
            return Ok(d);
        }
        catch (Exception ex) { return Fail("写入失败：" + ex.Message); }
    }

    // 预览：proposed text 与磁盘现状的差异
    static Dictionary<string, object> FsPreview(Dictionary<string, object> msg)
    {
        string path = Resolve(S(msg, "path"));
        string proposed = S(msg, "text");
        string oldText = "";
        try { if (File.Exists(path)) oldText = DecodeText(File.ReadAllBytes(path)); }
        catch (Exception ex) { return Fail(ex.Message); }
        Dictionary<string, object> d = DiffResult(oldText, proposed);
        d["path"] = Rel(path);
        return d;
    }

    // ---------- 文件搜索 ----------
    static Dictionary<string, object> FsSearch(Dictionary<string, object> msg)
    {
        string q = S(msg, "query").Trim();
        string mode = S(msg, "mode");
        List<object> hits = new List<object>();
        Dictionary<string, object> d = Obj();
        d["query"] = q;
        if (q.Length == 0) { d["hits"] = hits; return d; }
        bool content = mode == "content";
        int cap = content ? 120 : 300;
        List<string> files = new List<string>();
        Walk(Root, files, 20000, true);
        for (int i = 0; i < files.Count && hits.Count < cap; i++)
        {
            string rel = Rel(files[i]);
            if (!content)
            {
                if (rel.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Dictionary<string, object> h = Obj();
                    h["path"] = rel;
                    h["dir"] = false;
                    hits.Add(h);
                }
                continue;
            }
            // 内容搜索：只搜文本文件，命中的行一起返回
            try
            {
                FileInfo fi = new FileInfo(files[i]);
                if (fi.Length > MaxBaseFile) continue;
                string text = DecodeText(File.ReadAllBytes(files[i]));
                string[] lines = text.Replace("\r\n", "\n").Split('\n');
                for (int ln = 0; ln < lines.Length && hits.Count < cap; ln++)
                {
                    int at = lines[ln].IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) continue;
                    Dictionary<string, object> h = Obj();
                    h["path"] = rel;
                    h["line"] = ln + 1;
                    h["text"] = lines[ln].Trim();
                    hits.Add(h);
                }
            }
            catch { }
        }
        d["hits"] = hits;
        d["truncated"] = hits.Count >= cap;
        return d;
    }

    // 递归收集文件（跳过噪声目录）；textOnly 时过滤二进制
    static void Walk(string dir, List<string> outFiles, int cap, bool textOnly)
    {
        bool isTop = string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(Root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        List<string> subdirs = new List<string>();
        try
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (SkipDir(name, isTop)) continue;
                if ((File.GetAttributes(sub) & FileAttributes.Hidden) != 0) continue;
                // 面板自己的运行时产物（日志、基线快照、webview 数据、临时脚本）不参与：
                // 它们每次运行都在变，混进「变更」列表只会把真正的代码改动埋掉。
                if (IsUnder(Path.Combine(Program.ScriptRoot, "run"), sub)) continue;
                if (IsUnder(Path.Combine(Program.ScriptRoot, "logs"), sub)) continue;
                subdirs.Add(sub);
            }
            foreach (string f in Directory.GetFiles(dir))
            {
                if (outFiles.Count >= cap) break;
                string name = Path.GetFileName(f);
                if (name.EndsWith(".wb-tmp", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if ((File.GetAttributes(f) & FileAttributes.Hidden) != 0) continue;
                    long len = new FileInfo(f).Length;
                    if (len > MaxBaseFile) continue;
                    if (textOnly)
                    {
                        byte[] head = new byte[Math.Min(len, 512)];
                        using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            int n = fs.Read(head, 0, head.Length);
                            if (LooksBinary(head, n)) continue;
                        }
                    }
                    outFiles.Add(f);
                }
                catch { }
            }
        }
        catch { }
        for (int i = 0; i < subdirs.Count && outFiles.Count < cap; i++) Walk(subdirs[i], outFiles, cap, textOnly);
    }

    static Dictionary<string, object> FsReveal(Dictionary<string, object> msg)
    {
        string path = Resolve(S(msg, "path"));
        try
        {
            if (File.Exists(path))
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            else if (Directory.Exists(path))
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "\"" + path + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            else return Fail("路径不存在");
            return Ok(null);
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    // ---------- 内嵌终端 ----------
    internal class Run
    {
        public string id;
        public Process proc;
        public string cwd;
        public string command;
        public string shell;
        public StringBuilder outBuf = new StringBuilder();
        public StringBuilder errBuf = new StringBuilder();
        public long sent;
        public bool exited;
        public int code;
        public string newCwd;
        public bool truncated;
        public System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
    }

    static readonly Dictionary<string, Run> runs = new Dictionary<string, Run>();
    static int runSeq;

    // 白名单：读/看/查/构建一类的命令直接放行（逐段校验，段的分隔符见 SplitSegments）
    static readonly string[] AllowTokens = {
        "dir","ls","ll","cd","pwd","type","cat","echo","head","tail","find","findstr","where","which","tree","more",
        "node","npm","npx","pnpm","yarn","git","dotnet","python","py","pip","go","cargo","rustc","java","javac","mvn","gradle",
        "tsc","eslint","prettier","vitest","jest","mocha","pytest","curl","wget","sort","uniq","wc","measure-object",
        "get-childitem","get-content","get-location","get-process","get-date","get-command","get-item","get-itemproperty",
        "get-module","get-service","get-help","get-psdrive","get-host","get-culture","get-member","get-unique","get-random",
        "select-object","select-string","select-string","where-object","foreach-object","sort-object","measure-object",
        "format-table","format-list","format-wide","out-string","out-host","out-file","convertto-json","convertfrom-json",
        "test-path","resolve-path","split-path","join-path","write-output","write-host","compare-object","group-object",
        "start-sleep","git","code","notepad","grep","rg","sed","awk","jq","diff","patch","make","cmake","ninja",
        "docker","kubectl","gh","ssh","ping","ipconfig","tasklist","netstat","systeminfo","whoami","hostname","ver",
        "get-command","invoke-expression","import-module","powershell","cmd","shell"
    };

    // 危险模式：命中即红字二次确认（比白名单优先）
    static readonly string[] DangerPatterns = {
        "rm -rf","rm -fr","rm -r /","del /f","del /s","del /q","erase /","rmdir /s","rd /s","format ",
        "diskpart","mkfs","shutdown","restart-computer","stop-computer","bcdedit","bootrec","cipher /w",
        "vssadmin","wbadmin","manage-bde","chkdsk","net user","net localgroup","sc delete","reg delete","reg add",
        "takeown","icacls","attrib -r","git reset --hard","git clean -f","git push --force","git push -f",
        "remove-item -recurse","remove-item -force","remove-item -r","clear-content","stop-process -force",
        "taskkill /f","set-executionpolicy","wevtutil cl","wmic","invoke-webrequest","iex ","invoke-expression",
        "start-process -verb runas","schtasks /delete","icacls ","attrib -s -h","net stop","net start",
        "reg export","reg import","setx /m","netsh","osp","::(){","curl -o","curl -fssl","set-mppreference",
        "new-item -itemtype","remove-item","add-mppreference","disable-defender","set-mppreference"
    };

    static List<string> SplitSegments(string cmd)
    {
        List<string> segs = new List<string>();
        StringBuilder cur = new StringBuilder();
        for (int i = 0; i < cmd.Length; i++)
        {
            char c = cmd[i];
            if (c == ';' || c == '\n' || c == '\r')
            {
                if (cur.Length > 0) { segs.Add(cur.ToString()); cur.Length = 0; }
                continue;
            }
            if (c == '&' && i + 1 < cmd.Length && cmd[i + 1] == '&') { if (cur.Length > 0) { segs.Add(cur.ToString()); cur.Length = 0; } i++; continue; }
            if (c == '|' && i + 1 < cmd.Length && cmd[i + 1] == '|') { if (cur.Length > 0) { segs.Add(cur.ToString()); cur.Length = 0; } i++; continue; }
            if (c == '|') { if (cur.Length > 0) { segs.Add(cur.ToString()); cur.Length = 0; } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) segs.Add(cur.ToString());
        return segs;
    }

    static Dictionary<string, object> TermPolicy(string shell, string command)
    {
        Dictionary<string, object> d = Obj();
        string cmd = (command ?? "").Trim();
        d["command"] = cmd;
        d["level"] = "ask";
        d["reason"] = "未知命令：确认后执行";
        if (cmd.Length == 0) { d["level"] = "ask"; d["reason"] = "命令为空"; return d; }

        string lower = cmd.ToLowerInvariant();
        for (int i = 0; i < DangerPatterns.Length; i++)
        {
            if (lower.IndexOf(DangerPatterns[i], StringComparison.Ordinal) >= 0)
            {
                d["level"] = "danger";
                d["reason"] = "命中危险模式「" + DangerPatterns[i] + "」";
                return d;
            }
        }
        if (lower.IndexOf(">") >= 0 && lower.IndexOf("nul") < 0 && lower.IndexOf(">nul") < 0)
        {
            // 重定向写文件：不算危险，但要人看一眼
            d["level"] = "ask";
            d["reason"] = "含输出重定向，确认后执行";
            return d;
        }

        List<string> segs = SplitSegments(cmd);
        bool allAllow = segs.Count > 0;
        for (int i = 0; i < segs.Count; i++)
        {
            string seg = segs[i].Trim();
            if (seg.Length == 0) continue;
            // 去掉前导赋值/管道前缀
            string[] parts = seg.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            string head = parts[0].ToLowerInvariant().Trim('"', '\'');
            if (head.EndsWith(".exe") || head.EndsWith(".cmd") || head.EndsWith(".bat")) head = head.Substring(0, head.LastIndexOf('.'));
            bool ok = false;
            for (int k = 0; k < AllowTokens.Length; k++) if (AllowTokens[k] == head) { ok = true; break; }
            if (!ok) { allAllow = false; break; }
        }
        if (allAllow)
        {
            d["level"] = "allow";
            d["reason"] = "白名单命令（" + segs.Count + " 段）";
        }
        return d;
    }

    static Dictionary<string, object> TermRun(Dictionary<string, object> msg)
    {
        string cmd = S(msg, "command").Trim();
        if (cmd.Length == 0) return Fail("命令为空");
        string shell = S(msg, "shell");
        if (shell != "cmd" && shell != "pwsh") shell = "powershell";
        Dictionary<string, object> pol = TermPolicy(shell, cmd);
        if (S(pol, "level") != "allow" && !B(msg, "force", false))
        {
            Dictionary<string, object> o = Obj();
            o["needConfirm"] = true;
            o["level"] = S(pol, "level");
            o["reason"] = S(pol, "reason");
            return Ok(o);
        }

        string cwd = Root;
        try
        {
            string want = S(msg, "cwd");
            if (!string.IsNullOrEmpty(want) && want != ".")
            {
                string resolved = Resolve(want);
                if (Directory.Exists(resolved)) cwd = resolved;
            }
        }
        catch { cwd = Root; }

        string nodeExe = FindNodeExeCached;
        Run r = new Run();
        r.id = "run-" + Interlocked.Increment(ref runSeq);
        r.cwd = cwd;
        r.command = cmd;
        r.shell = shell;

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.WorkingDirectory = cwd;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        try
        {
            psi.EnvironmentVariables["DSH_WORKBENCH"] = "1";
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            psi.EnvironmentVariables["TERM"] = "dumb";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        }
        catch { }

        if (shell == "cmd")
        {
            string tmpDir = Path.Combine(DataDir, "tmp");
            try { Directory.CreateDirectory(tmpDir); } catch { }
            string tmp = Path.Combine(tmpDir, "cmd-" + runSeq + ".cmd");
            StringBuilder sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("chcp 65001 >nul\r\n");
            sb.Append(cmd).Append("\r\n");
            sb.Append("echo __WB_CWD__%CD%\r\n");
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            psi.Arguments = "/D /Q /S /C \"\"" + tmp + "\"\"";
        }
        else
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[Console]::OutputEncoding=[Text.Encoding]::UTF8;");
            sb.Append("$OutputEncoding=[Text.Encoding]::UTF8;");
            // 重定向环境下 PS 会把进度记录序列化成 CLIXML（#< CLIXML ...）塞进 stderr，
            // 看起来像乱码噪声——直接关掉进度。
            sb.Append("$ProgressPreference='SilentlyContinue';");
            sb.Append("try {\r\n").Append(cmd).Append("\r\n} finally {\r\n");
            sb.Append("  Write-Output ('__WB_CWD__' + (Get-Location).Path)\r\n");
            sb.Append("}\r\n");
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
            string ps;
            if (shell == "pwsh")
            {
                ps = FindPwsh();
                if (string.IsNullOrEmpty(ps)) ps = "pwsh.exe";
            }
            else
            {
                ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(ps)) ps = "powershell.exe";
            }
            psi.FileName = ps;
            psi.Arguments = "-NoProfile -NoLogo -NonInteractive -OutputFormat Text -ExecutionPolicy Bypass -EncodedCommand " + b64;
        }

        try
        {
            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs ev) { OnRunOutput(r, ev.Data, false); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs ev) { OnRunOutput(r, ev.Data, true); };
            p.Exited += delegate(object s, EventArgs ev) { OnRunExit(r); };
            r.proc = p;
            p.Start();
            r.clock.Start();
            try { p.StandardInput.Close(); } catch { }
            lock (runs) runs[r.id] = r;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception ex) { return Fail("启动失败：" + ex.Message); }

        Dictionary<string, object> outD = Obj();
        outD["id"] = r.id;
        outD["cwd"] = Rel(cwd);
        outD["shell"] = shell;
        outD["policy"] = S(pol, "level");
        Program.LogEvent(LogKind.Info, "工作台终端执行（" + shell + "）：" + (cmd.Length > 120 ? cmd.Substring(0, 120) + "…" : cmd));
        return Ok(outD);
    }

    static void OnRunOutput(Run r, string line, bool isErr)
    {
        if (line == null) return;
        lock (r)
        {
            if (line.StartsWith("__WB_CWD__", StringComparison.Ordinal))
            {
                r.newCwd = line.Substring("__WB_CWD__".Length).Trim();
                return;
            }
            StringBuilder buf = isErr ? r.errBuf : r.outBuf;
            if (buf.Length > 4 * 1024 * 1024) return;   // 极端输出：丢弃，避免吃光内存
            buf.Append(line).Append('\n');
        }
    }

    static void OnRunExit(Run r)
    {
        try { if (r.proc != null) r.proc.WaitForExit(); }
        catch { }
        r.exited = true;
        try { r.code = r.proc == null ? -1 : r.proc.ExitCode; } catch { r.code = -1; }
        r.clock.Stop();
        FlushRun(r);
        Dictionary<string, object> d = Obj();
        d["id"] = r.id;
        d["code"] = r.code;
        d["cwd"] = r.newCwd == null ? Rel(r.cwd) : r.newCwd;
        d["ms"] = r.clock.ElapsedMilliseconds;
        PushEvent("term-exit", d);
        lock (runs)
        {
            if (runs.ContainsKey(r.id) && runs.Count > 40) runs.Remove(r.id);
        }
    }

    // 把累积的输出批量推给界面（120ms 一次，避免每行一次 postMessage）
    static void FlushOutput()
    {
        List<Run> snapshot;
        lock (runs) snapshot = new List<Run>(runs.Values);
        for (int i = 0; i < snapshot.Count; i++) FlushRun(snapshot[i]);
    }

    static void FlushRun(Run r)
    {
        string outText = null;
        string errText = null;
        lock (r)
        {
            if (r.sent >= MaxTermBytes)
            {
                if (r.outBuf.Length > 0 || r.errBuf.Length > 0)
                {
                    if (!r.truncated)
                    {
                        r.truncated = true;
                        outText = "\n[工作台] 输出超过 900KB，后续内容已丢弃\n";
                    }
                    r.outBuf.Length = 0;
                    r.errBuf.Length = 0;
                }
            }
            else
            {
                if (r.outBuf.Length > 0) { outText = r.outBuf.ToString(); r.sent += outText.Length; r.outBuf.Length = 0; }
                if (r.errBuf.Length > 0) { errText = r.errBuf.ToString(); r.sent += errText.Length; r.errBuf.Length = 0; }
            }
        }
        if (outText == null && errText == null) return;
        Dictionary<string, object> d = Obj();
        d["id"] = r.id;
        if (outText != null) d["out"] = outText;
        if (errText != null) d["err"] = errText;
        PushEvent("term", d);
    }

    static Dictionary<string, object> TermKill(Dictionary<string, object> msg)
    {
        // 注意：消息信封里的 "id" 是**请求 id**（用于配对响应），运行 id 走 "runId"。
        // v3.5 一开始两者同名，结果停止命令能杀掉进程、但响应永远回不到界面（停止按钮看起来没反应）。
        string id = S(msg, "runId");
        if (string.IsNullOrEmpty(id)) id = S(msg, "id");
        return KillRun(id) ? Ok(null) : Fail("进程不存在或已结束");
    }

    static bool KillRun(string id)
    {
        Run r = null;
        lock (runs) { if (id != null) runs.TryGetValue(id, out r); }
        if (r == null || r.proc == null) return false;
        try
        {
            if (!r.proc.HasExited) r.proc.Kill();
            return true;
        }
        catch { return false; }
    }

    // ---------- Git ----------
    static string gitExe;
    static bool gitProbed;

    internal static string FindGit()
    {
        if (gitProbed) return gitExe;
        gitProbed = true;
        try
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path))
            {
                foreach (string raw in path.Split(';'))
                {
                    string dir = raw.Trim().Trim('"');
                    if (dir.Length == 0) continue;
                    try
                    {
                        string cand = Path.Combine(dir, "git.exe");
                        if (File.Exists(cand)) { gitExe = cand; return gitExe; }
                    }
                    catch { }
                }
            }
            string[] guesses = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe")
            };
            for (int i = 0; i < guesses.Length; i++) if (File.Exists(guesses[i])) { gitExe = guesses[i]; return gitExe; }
        }
        catch { }
        return gitExe;
    }

    static string nodeCached;

    static string FindNodeExeCached
    {
        get
        {
            if (nodeCached == null)
            {
                try { nodeCached = Program.FindNodeExe(); }
                catch { nodeCached = ""; }
            }
            return nodeCached;
        }
    }

    // 同步跑一个短命令并抓输出（git 用；带超时，绝不挂死 UI 线程——本方法只在后台线程调用）
    static string RunCaptured(string exe, string args, string cwd, int timeoutMs, out string err, out int code)
    {
        err = "";
        code = -1;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = cwd;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["GIT_PAGER"] = "cat";
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.Start();
                System.Threading.Tasks.Task<string> so = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> se = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    err = "命令超时（>" + timeoutMs + "ms）";
                    return "";
                }
                code = p.ExitCode;
                string outText = so.Result;
                err = se.Result;
                return outText;
            }
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return "";
        }
    }

    static Dictionary<string, object> SafeGitInfo()
    {
        try { return GitInfo(); } catch { return null; }
    }

    static Dictionary<string, object> GitInfo()
    {
        Dictionary<string, object> d = Obj();
        string git = FindGit();
        d["available"] = !string.IsNullOrEmpty(git);
        if (string.IsNullOrEmpty(git)) { d["repo"] = false; d["reason"] = "未找到 git.exe"; return d; }
        if (!Directory.Exists(Path.Combine(Root, ".git"))) { d["repo"] = false; d["reason"] = "工作区不是 git 仓库"; return d; }

        string err;
        int code;
        string inside = RunCaptured(git, "rev-parse --is-inside-work-tree", Root, 8000, out err, out code);
        if (code != 0 || inside.Trim() != "true") { d["repo"] = false; d["reason"] = err.Length > 0 ? err : "不是 git 仓库"; return d; }
        d["repo"] = true;

        d["branch"] = RunCaptured(git, "rev-parse --abbrev-ref HEAD", Root, 8000, out err, out code).Trim();
        d["head"] = RunCaptured(git, "rev-parse --short HEAD", Root, 8000, out err, out code).Trim();
        string remote = RunCaptured(git, "remote -v", Root, 8000, out err, out code);
        d["remote"] = remote.Length > 0 ? remote.Split('\n')[0].Trim() : "";

        string status = RunCaptured(git, "status --porcelain=v1 --untracked-files=normal --ignored=no", Root, 15000, out err, out code);
        List<object> changes = new List<object>();
        int staged = 0, unstaged = 0, untracked = 0;
        foreach (string raw in status.Replace("\r", "").Split('\n'))
        {
            if (raw.Length < 4) continue;
            string xy = raw.Substring(0, 2);
            string p = raw.Substring(3).Trim();
            int arrow = p.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0) p = p.Substring(arrow + 4);
            Dictionary<string, object> c = Obj();
            c["path"] = p;
            c["xy"] = xy;
            c["staged"] = xy[0] != ' ' && xy[0] != '?';
            c["untracked"] = xy == "??";
            if (xy == "??") untracked++;
            else if (c["staged"] != null && (bool)c["staged"]) staged++;
            else unstaged++;
            changes.Add(c);
        }
        d["changes"] = changes;
        d["stagedCount"] = staged;
        d["unstagedCount"] = unstaged;
        d["untrackedCount"] = untracked;

        // 注意：--date=format:... 里不能带空格——ProcessStartInfo.Arguments 是单一字符串，
        // 空格会被拆成独立参数，git 直接把整条命令判错（表现为「提交历史永远是空的」）。
        string log = RunCaptured(git, "log -n 25 --date=format:%Y-%m-%dT%H:%M --pretty=format:%h%x1f%ad%x1f%an%x1f%s", Root, 15000, out err, out code);
        List<object> commits = new List<object>();
        foreach (string raw in log.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            string[] parts = raw.Split('\u001f');
            if (parts.Length < 4) continue;
            Dictionary<string, object> c = Obj();
            c["hash"] = parts[0];
            c["date"] = parts[1];
            c["author"] = parts[2];
            c["subject"] = parts[3];
            commits.Add(c);
        }
        d["commits"] = commits;
        return d;
    }

    static Dictionary<string, object> GitDiff(Dictionary<string, object> msg)
    {
        Dictionary<string, object> d = Obj();
        string git = FindGit();
        if (string.IsNullOrEmpty(git)) return d;
        string rel = S(msg, "path");
        string mode = S(msg, "mode");
        string err;
        int code;
        string args = "diff --no-color --unified=3";
        if (mode == "staged") args += " --cached";
        if (!string.IsNullOrEmpty(rel)) args += " -- \"" + rel.Replace("\"", "") + "\"";
        string text = RunCaptured(git, args, Root, 20000, out err, out code);
        if (text.Length == 0 && mode == "staged")
        {
            d["note"] = "暂存区没有差异";
        }
        if (text.Length == 0 && !string.IsNullOrEmpty(rel))
        {
            // 未跟踪文件：整份当新增
            try
            {
                string full = Resolve(rel);
                if (File.Exists(full))
                {
                    string content = DecodeText(File.ReadAllBytes(full));
                    Dictionary<string, object> dd = DiffResult("", content);
                    dd["untracked"] = true;
                    dd["path"] = rel;
                    return dd;
                }
            }
            catch { }
        }
        d["text"] = text;
        d["err"] = err;
        d["code"] = code;
        return d;
    }

    // ---------- 行级 diff ----------
    static string[] SplitLines(string text)
    {
        if (text == null) return new string[0];
        string s = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (s.Length == 0) return new string[0];
        string[] arr = s.Split('\n');
        if (arr.Length > 0 && arr[arr.Length - 1].Length == 0)
        {
            Array.Resize(ref arr, arr.Length - 1);
        }
        return arr;
    }

    // 返回 [type, oldIdx, newIdx]：type 0=相同 1=删除 2=新增
    static List<int[]> DiffOps(string[] a, string[] b)
    {
        List<int[]> ops = new List<int[]>();
        int n = a.Length, m = b.Length;
        int pre = 0;
        while (pre < n && pre < m && string.Equals(a[pre], b[pre], StringComparison.Ordinal)) pre++;
        int suf = 0;
        while (suf < n - pre && suf < m - pre && string.Equals(a[n - 1 - suf], b[m - 1 - suf], StringComparison.Ordinal)) suf++;
        for (int i = 0; i < pre; i++) ops.Add(new int[] { 0, i, i });
        int n2 = n - pre - suf, m2 = m - pre - suf;
        if (n2 > 0 || m2 > 0)
        {
            if ((long)n2 * (long)m2 <= 4000000L)
            {
                short[,] dp = new short[n2 + 1, m2 + 1];
                for (int i = n2 - 1; i >= 0; i--)
                {
                    for (int j = m2 - 1; j >= 0; j--)
                    {
                        if (string.Equals(a[pre + i], b[pre + j], StringComparison.Ordinal)) dp[i, j] = (short)(dp[i + 1, j + 1] + 1);
                        else dp[i, j] = dp[i + 1, j] >= dp[i, j + 1] ? dp[i + 1, j] : dp[i, j + 1];
                    }
                }
                int x = 0, y = 0;
                while (x < n2 && y < m2)
                {
                    if (string.Equals(a[pre + x], b[pre + y], StringComparison.Ordinal))
                    {
                        ops.Add(new int[] { 0, pre + x, pre + y });
                        x++; y++;
                    }
                    else if (dp[x + 1, y] >= dp[x, y + 1])
                    {
                        ops.Add(new int[] { 1, pre + x, -1 });
                        x++;
                    }
                    else
                    {
                        ops.Add(new int[] { 2, -1, pre + y });
                        y++;
                    }
                }
                while (x < n2) { ops.Add(new int[] { 1, pre + x, -1 }); x++; }
                while (y < m2) { ops.Add(new int[] { 2, -1, pre + y }); y++; }
            }
            else
            {
                for (int i = 0; i < n2; i++) ops.Add(new int[] { 1, pre + i, -1 });
                for (int j = 0; j < m2; j++) ops.Add(new int[] { 2, -1, pre + j });
            }
        }
        for (int i = 0; i < suf; i++) ops.Add(new int[] { 0, n - suf + i, m - suf + i });
        return ops;
    }

    static Dictionary<string, object> DiffResult(string oldText, string newText)
    {
        Dictionary<string, object> d = Obj();
        string[] a = SplitLines(oldText);
        string[] b = SplitLines(newText);
        List<int[]> ops;
        try { ops = DiffOps(a, b); }
        catch (Exception ex) { d["error"] = ex.Message; ops = new List<int[]>(); }
        int adds = 0, dels = 0;
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i][0] == 1) dels++;
            else if (ops[i][0] == 2) adds++;
        }
        d["adds"] = adds;
        d["dels"] = dels;
        d["oldLines"] = a.Length;
        d["newLines"] = b.Length;
        bool truncated = ops.Count > DiffCap;
        List<object> rows = new List<object>(Math.Min(ops.Count, DiffCap));
        int limit = Math.Min(ops.Count, DiffCap);
        for (int i = 0; i < limit; i++)
        {
            int[] o = ops[i];
            Dictionary<string, object> row = Obj();
            if (o[0] == 0) { row["t"] = "eq"; row["o"] = o[1] + 1; row["n"] = o[2] + 1; row["x"] = a[o[1]]; }
            else if (o[0] == 1) { row["t"] = "del"; row["o"] = o[1] + 1; row["x"] = a[o[1]]; }
            else { row["t"] = "ins"; row["n"] = o[2] + 1; row["x"] = b[o[2]]; }
            rows.Add(row);
        }
        d["rows"] = rows;
        d["truncated"] = truncated;
        return d;
    }

    // ---------- 变更审阅（基线快照 + diff + 接受/回滚） ----------
    internal class BaseEntry
    {
        public long size;
        public long mtime;
        public string hash;
    }

    // 基线格式版本：Walk 的排除规则变了就加一，老基线会被丢弃重建
    // （否则改过排除规则后，一堆早已不扫描的路径会一直显示成 deleted）
    const int BaselineVersion = 2;

    static Dictionary<string, BaseEntry> baseline = new Dictionary<string, BaseEntry>(StringComparer.OrdinalIgnoreCase);
    static bool baselineLoaded;
    static readonly Dictionary<string, string> hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static long baselineAt;

    static string CopyPath(string rel)
    {
        byte[] raw = Encoding.UTF8.GetBytes(rel.ToLowerInvariant());
        System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create();
        byte[] h = sha.ComputeHash(raw);
        StringBuilder sb = new StringBuilder(h.Length * 2);
        for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
        return Path.Combine(BaseDir, "files", sb.ToString() + ".bin");
    }

    static string HashOf(string fullPath, long size, long mtime)
    {
        string key = fullPath + "|" + size + "|" + mtime;
        string cached;
        if (hashCache.TryGetValue(key, out cached)) return cached;
        try
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create();
            byte[] h = sha.ComputeHash(bytes);
            StringBuilder sb = new StringBuilder(h.Length * 2);
            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
            string hex = sb.ToString();
            if (hashCache.Count > 6000) hashCache.Clear();
            hashCache[key] = hex;
            return hex;
        }
        catch { return ""; }
    }

    static void EnsureBaselineLoaded()
    {
        if (baselineLoaded) return;
        baselineLoaded = true;
        baseline = new Dictionary<string, BaseEntry>(StringComparer.OrdinalIgnoreCase);
        string file = Path.Combine(BaseDir, "manifest.json");
        try
        {
            if (!File.Exists(file)) return;
            Dictionary<string, object> d = Js.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
            if (d == null) return;
            object ver; int baseVer = 0;
            if (d.TryGetValue("v", out ver)) { try { baseVer = Convert.ToInt32(ver, CultureInfo.InvariantCulture); } catch { } }
            if (baseVer != BaselineVersion)
            {
                // 老格式基线：整份丢弃，交给 ChangesList 重建
                baseline.Clear();
                try { if (Directory.Exists(BaseDir)) Directory.Delete(BaseDir, true); } catch { }
                try { Directory.CreateDirectory(Path.Combine(BaseDir, "files")); } catch { }
                return;
            }
            object at; if (d.TryGetValue("at", out at)) { try { baselineAt = Convert.ToInt64(at, CultureInfo.InvariantCulture); } catch { } }
            object files;
            if (!d.TryGetValue("files", out files)) return;
            Dictionary<string, object> map = files as Dictionary<string, object>;
            if (map == null) return;
            foreach (KeyValuePair<string, object> kv in map)
            {
                Dictionary<string, object> v = kv.Value as Dictionary<string, object>;
                if (v == null) continue;
                BaseEntry e = new BaseEntry();
                object o;
                if (v.TryGetValue("size", out o)) { try { e.size = Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { } }
                if (v.TryGetValue("mtime", out o)) { try { e.mtime = Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { } }
                if (v.TryGetValue("hash", out o)) e.hash = o as string;
                baseline[kv.Key] = e;
            }
        }
        catch { }
    }

    static void SaveBaseline()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            Dictionary<string, object> files = Obj();
            foreach (KeyValuePair<string, BaseEntry> kv in baseline)
            {
                Dictionary<string, object> v = Obj();
                v["size"] = kv.Value.size;
                v["mtime"] = kv.Value.mtime;
                v["hash"] = kv.Value.hash;
                files[kv.Key] = v;
            }
            Dictionary<string, object> root = Obj();
            root["v"] = BaselineVersion;
            root["at"] = baselineAt;
            root["files"] = files;
            File.WriteAllText(Path.Combine(BaseDir, "manifest.json"), Json(root), new UTF8Encoding(false));
        }
        catch { }
    }

    static void ResetBaseline()
    {
        baselineLoaded = true;
        baseline = new Dictionary<string, BaseEntry>(StringComparer.OrdinalIgnoreCase);
        baselineAt = 0;
        hashCache.Clear();
        try { if (Directory.Exists(BaseDir)) Directory.Delete(BaseDir, true); } catch { }
        try { Directory.CreateDirectory(Path.Combine(BaseDir, "files")); } catch { }
        SaveBaseline();
    }

    // 全量重建基线（把当前工作区状态当作「已接受」）
    static void BuildBaseline()
    {
        List<string> files = new List<string>();
        Walk(Root, files, MaxBaselineFiles, true);
        baseline = new Dictionary<string, BaseEntry>(StringComparer.OrdinalIgnoreCase);
        hashCache.Clear();
        string dir = Path.Combine(BaseDir, "files");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        try { Directory.CreateDirectory(dir); } catch { }
        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                FileInfo fi = new FileInfo(files[i]);
                string rel = Rel(files[i]);
                BaseEntry e = new BaseEntry();
                e.size = fi.Length;
                e.mtime = fi.LastWriteTimeUtc.Ticks / 10000;
                e.hash = HashOf(fi.FullName, e.size, e.mtime);
                baseline[rel] = e;
                File.Copy(fi.FullName, CopyPath(rel), true);
            }
            catch { }
        }
        baselineAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SaveBaseline();
    }

    static Dictionary<string, object> ChangesSnapshot()
    {
        BuildBaseline();
        Dictionary<string, object> d = ChangesList();
        d["rebuilt"] = true;
        return Ok(d);
    }

    static Dictionary<string, object> SafeChangesSummary()
    {
        try
        {
            Dictionary<string, object> d = ChangesList();
            return d;
        }
        catch { return null; }
    }

    static Dictionary<string, object> ChangesList()
    {
        EnsureBaselineLoaded();
        if (baseline.Count == 0) BuildBaseline();
        List<string> files = new List<string>();
        Walk(Root, files, MaxBaselineFiles, true);
        Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
        int diffBudget = 60;
        for (int i = 0; i < files.Count; i++)
        {
            string rel = Rel(files[i]);
            seen[rel] = true;
            FileInfo fi;
            try { fi = new FileInfo(files[i]); } catch { continue; }
            long mtime = fi.LastWriteTimeUtc.Ticks / 10000;
            BaseEntry old;
            bool known = baseline.TryGetValue(rel, out old);
            if (known && old != null)
            {
                if (old.size == fi.Length && old.mtime == mtime) continue;   // 快路径：没动过
                string h = HashOf(fi.FullName, fi.Length, mtime);
                if (h.Length > 0 && h == old.hash) continue;
            }
            Dictionary<string, object> it = Obj();
            it["path"] = rel;
            it["status"] = known ? "modified" : "added";
            it["size"] = fi.Length;
            it["mtime"] = mtime;
            if (diffBudget > 0 && fi.Length <= 262144)
            {
                diffBudget--;
                try
                {
                    string newText = DecodeText(File.ReadAllBytes(fi.FullName));
                    string oldText = "";
                    if (known)
                    {
                        string cp = CopyPath(rel);
                        if (File.Exists(cp)) oldText = DecodeText(File.ReadAllBytes(cp));
                    }
                    string[] a = SplitLines(oldText);
                    string[] b = SplitLines(newText);
                    List<int[]> ops = DiffOps(a, b);
                    int adds = 0, dels = 0;
                    for (int k = 0; k < ops.Count; k++)
                    {
                        if (ops[k][0] == 1) dels++;
                        else if (ops[k][0] == 2) adds++;
                    }
                    it["adds"] = adds;
                    it["dels"] = dels;
                }
                catch { }
            }
            if (!it.ContainsKey("adds")) { it["adds"] = -1; it["dels"] = -1; }
            items.Add(it);
        }
        foreach (KeyValuePair<string, BaseEntry> kv in baseline)
        {
            if (seen.ContainsKey(kv.Key)) continue;
            Dictionary<string, object> it = Obj();
            it["path"] = kv.Key;
            it["status"] = "deleted";
            it["size"] = kv.Value == null ? 0 : kv.Value.size;
            it["mtime"] = 0;
            it["adds"] = 0;
            it["dels"] = -1;
            items.Add(it);
        }
        // 排序：修改 > 新增 > 删除，同类按路径
        items.Sort(delegate(Dictionary<string, object> x, Dictionary<string, object> y)
        {
            int rank = Rank(S(x, "status")) - Rank(S(y, "status"));
            if (rank != 0) return rank;
            return string.Compare(S(x, "path"), S(y, "path"), StringComparison.OrdinalIgnoreCase);
        });
        Dictionary<string, object> d = Obj();
        d["workspace"] = Root;
        d["baselineAt"] = baselineAt;
        d["count"] = items.Count;
        d["modified"] = CountStatus(items, "modified");
        d["added"] = CountStatus(items, "added");
        d["deleted"] = CountStatus(items, "deleted");
        d["items"] = items;
        d["files"] = files.Count;
        return d;
    }

    static int Rank(string status)
    {
        if (status == "modified") return 0;
        if (status == "added") return 1;
        return 2;
    }

    static int CountStatus(List<Dictionary<string, object>> items, string status)
    {
        int c = 0;
        for (int i = 0; i < items.Count; i++) if (S(items[i], "status") == status) c++;
        return c;
    }

    static Dictionary<string, object> ChangesDiff(Dictionary<string, object> msg)
    {
        EnsureBaselineLoaded();
        string rel = S(msg, "path");
        string full;
        try { full = Resolve(rel); } catch (Exception ex) { return ObjWith("error", ex.Message); }
        string oldText = "";
        string cp = CopyPath(rel);
        bool hasOld = false;
        try
        {
            if (File.Exists(cp)) { oldText = DecodeText(File.ReadAllBytes(cp)); hasOld = true; }
        }
        catch { }
        string newText = "";
        bool hasNew = false;
        try
        {
            if (File.Exists(full))
            {
                FileInfo fi = new FileInfo(full);
                if (fi.Length <= MaxTextRead) { newText = DecodeText(File.ReadAllBytes(full)); hasNew = true; }
            }
        }
        catch { }
        Dictionary<string, object> d = DiffResult(oldText, newText);
        d["path"] = rel;
        d["hasOld"] = hasOld;
        d["hasNew"] = hasNew;
        d["status"] = !hasOld ? "added" : (!hasNew ? "deleted" : "modified");
        return d;
    }

    static Dictionary<string, object> ObjWith(string k, object v)
    {
        Dictionary<string, object> d = Obj();
        d[k] = v;
        return d;
    }

    static Dictionary<string, object> ChangesAccept(Dictionary<string, object> msg)
    {
        EnsureBaselineLoaded();
        string[] paths = A(msg, "paths");
        if (paths.Length == 0)
        {
            // 全部接受
            BuildBaseline();
            Dictionary<string, object> d = ChangesList();
            d["accepted"] = -1;
            return Ok(d);
        }
        int n = 0;
        for (int i = 0; i < paths.Length; i++)
        {
            string rel = paths[i];
            string full;
            try { full = Resolve(rel); } catch { continue; }
            try
            {
                if (File.Exists(full))
                {
                    FileInfo fi = new FileInfo(full);
                    BaseEntry e = new BaseEntry();
                    e.size = fi.Length;
                    e.mtime = fi.LastWriteTimeUtc.Ticks / 10000;
                    e.hash = HashOf(fi.FullName, e.size, e.mtime);
                    baseline[rel] = e;
                    Directory.CreateDirectory(Path.Combine(BaseDir, "files"));
                    File.Copy(fi.FullName, CopyPath(rel), true);
                }
                else
                {
                    baseline.Remove(rel);
                    try { File.Delete(CopyPath(rel)); } catch { }
                }
                n++;
            }
            catch { }
        }
        SaveBaseline();
        Dictionary<string, object> res = ChangesList();
        res["accepted"] = n;
        return Ok(res);
    }

    static Dictionary<string, object> ChangesRevert(Dictionary<string, object> msg)
    {
        EnsureBaselineLoaded();
        string[] paths = A(msg, "paths");
        int n = 0;
        List<string> errors = new List<string>();
        for (int i = 0; i < paths.Length; i++)
        {
            string rel = paths[i];
            string full;
            try { full = Resolve(rel); } catch { continue; }
            try
            {
                string cp = CopyPath(rel);
                BaseEntry e;
                bool known = baseline.TryGetValue(rel, out e) && e != null;
                if (known && File.Exists(cp))
                {
                    byte[] raw = File.ReadAllBytes(cp);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllBytes(full, raw);
                    n++;
                }
                else if (!known)
                {
                    // 基线里没有 = 本次新增，回滚就是删掉
                    if (File.Exists(full)) { File.Delete(full); n++; }
                }
            }
            catch (Exception ex) { errors.Add(rel + "：" + ex.Message); }
        }
        Dictionary<string, object> res = ChangesList();
        res["reverted"] = n;
        if (errors.Count > 0) res["errors"] = errors;
        return Ok(res);
    }

    // ---------- 会话边车（任务看板 / 活动流） ----------
    static Process sidecar;
    static volatile bool sidecarStopping;
    static int sidecarFailures;
    static readonly object agentLock = new object();
    static Dictionary<string, object> agentGoal;
    static List<object> agentTodos;
    static readonly List<object> agentEvents = new List<object>();
    static string agentState = "unknown";
    static string agentTitle = "";
    static string agentSession = "";
    static string agentNote = "";
    // v3.6：看板卡片数据（轮次卡 / 排队消息 / 清单时间戳），由会话边车整包推送
    static Dictionary<string, object> agentBoard;

    // 边车脚本：v3.5 收进 workbench/（与 ui.html 放一起），老布局留在根目录的也认
    static string SidecarScript()
    {
        try
        {
            string p = Path.Combine(Program.ScriptRoot, "workbench", "session-reader.mjs");
            if (File.Exists(p)) return p;
            string legacy = Path.Combine(Program.ScriptRoot, "session-reader.mjs");
            if (File.Exists(legacy)) return legacy;
        }
        catch { }
        return null;
    }

    static void StartSidecar()
    {
        sidecarStopping = false;
        sidecarFailures = 0;
        lock (agentLock)
        {
            agentGoal = null;
            agentTodos = null;
            agentBoard = null;
            agentEvents.Clear();
            agentState = "unknown";
            agentTitle = "";
            agentSession = "";
        }
        LaunchSidecar();
    }

    static void StopSidecar()
    {
        sidecarStopping = true;
        try
        {
            if (sidecar != null && !sidecar.HasExited)
            {
                try { sidecar.StandardInput.Close(); } catch { }
                if (!sidecar.WaitForExit(1500)) sidecar.Kill();
            }
        }
        catch { }
        sidecar = null;
    }

    static void LaunchSidecar()
    {
        string script = SidecarScript();
        string node = FindNodeExeCached;
        if (script == null || string.IsNullOrEmpty(node))
        {
            lock (agentLock) { agentNote = "找不到 node.exe 或 workbench/session-reader.mjs，任务看板不可用（其他页签不受影响）"; }
            PushEvent("agent-note", ObjWith("text", agentNote));
            return;
        }
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(node, "\"" + script + "\" --cwd \"" + Root + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Program.ScriptRoot;
            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs ev) { OnSidecarLine(ev.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs ev)
            {
                if (ev.Data != null) Program.BootMark("wb-session: " + (ev.Data.Length > 120 ? ev.Data.Substring(0, 120) : ev.Data));
            };
            p.Exited += delegate(object s, EventArgs ev) { OnSidecarExit(); };
            sidecar = p;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            Program.LogEvent(LogKind.Info, "工作台会话边车已启动（node workbench/session-reader.mjs）");
        }
        catch (Exception ex)
        {
            lock (agentLock) { agentNote = "会话边车启动失败：" + ex.Message; }
            PushEvent("agent-note", ObjWith("text", agentNote));
            Program.LogEvent(LogKind.Warn, "工作台会话边车启动失败：" + ex.Message);
        }
    }

    static void OnSidecarExit()
    {
        if (sidecarStopping) return;
        sidecarFailures++;
        Program.LogEvent(LogKind.Warn, "工作台会话边车退出（第 " + sidecarFailures + " 次），准备重启");
        PushEvent("agent-note", ObjWith("text", "会话事件流已断开，正在重连…"));
        if (sidecarFailures > 5) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            Thread.Sleep(1000 * Math.Min(sidecarFailures, 5));
            if (sidecarStopping) return;
            LaunchSidecar();
        });
    }

    static void OnSidecarLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        Dictionary<string, object> m = null;
        try { m = Js.DeserializeObject(line) as Dictionary<string, object>; }
        catch { return; }
        if (m == null) return;
        string t = S(m, "t");
        if (t == "hello") return;
        if (t == "nostream")
        {
            lock (agentLock) { agentState = "idle"; agentSession = ""; }
            PushEvent("agent-idle", Obj());
            return;
        }
        if (t == "reset")
        {
            lock (agentLock)
            {
                agentSession = S(m, "sessionId");
                agentTitle = "";
                agentGoal = null;
                agentTodos = null;
                agentBoard = null;
                agentEvents.Clear();
                agentState = "idle";
            }
            Dictionary<string, object> d = Obj();
            d["sessionId"] = S(m, "sessionId");
            PushEvent("agent-reset", d);
            return;
        }
        if (t == "snapshot")
        {
            lock (agentLock)
            {
                agentGoal = m.ContainsKey("goal") ? m["goal"] as Dictionary<string, object> : null;
                agentTodos = m.ContainsKey("todos") ? ToList(m["todos"]) : null;
                agentBoard = BoardFromSnapshot(m);
                agentTitle = S(m, "title");
                agentState = S(m, "state");
                object evs;
                if (m.TryGetValue("events", out evs))
                {
                    object[] arr = evs as object[];
                    agentEvents.Clear();
                    if (arr != null) for (int i = 0; i < arr.Length; i++) agentEvents.Add(arr[i]);
                    TrimEvents();
                }
                agentNote = "";
            }
            PushEvent("agent-snapshot", AgentState());
            return;
        }
        if (t == "ev")
        {
            Dictionary<string, object> ev = m.ContainsKey("ev") ? m["ev"] as Dictionary<string, object> : null;
            if (ev == null) return;
            lock (agentLock)
            {
                string kind = S(ev, "kind");
                if (kind == "todos") agentTodos = ev.ContainsKey("todos") ? ToList(ev["todos"]) : null;
                else if (kind == "goal") agentGoal = ObjWith("objective", S(ev, "objective"));
                else if (kind == "title") agentTitle = S(ev, "title");
                else if (kind == "turn") agentState = S(ev, "state");
                else if (kind == "board") agentBoard = ev.ContainsKey("board") ? ev["board"] as Dictionary<string, object> : null;
                agentEvents.Add(ev);
                TrimEvents();
            }
            Dictionary<string, object> d = Obj();
            d["ev"] = ev;
            d["state"] = agentState;
            d["title"] = agentTitle;
            PushEvent("agent-ev", d);
            return;
        }
    }

    // JavaScriptSerializer 把数组反序列化成 object[]（不是 List<object>），
    // 这里统一转一次——否则 `as List<object>` 永远是 null（v3.5 踩过：看板一直空白）。
    static List<object> ToList(object v)
    {
        object[] arr = v as object[];
        if (arr == null)
        {
            List<object> direct = v as List<object>;
            return direct;
        }
        List<object> list = new List<object>(arr.Length);
        for (int i = 0; i < arr.Length; i++) list.Add(arr[i]);
        return list;
    }

    // snapshot 里的看板字段是平铺的；挑出来当初始看板（之后走 kind=board 增量整包替换）
    static Dictionary<string, object> BoardFromSnapshot(Dictionary<string, object> m)
    {
        Dictionary<string, object> b = Obj();
        b["turns"] = m.ContainsKey("turns") ? m["turns"] : null;
        b["current"] = m.ContainsKey("current") ? m["current"] : null;
        b["inbox"] = m.ContainsKey("inbox") ? m["inbox"] : null;
        b["todosAt"] = m.ContainsKey("todosAt") ? m["todosAt"] : (object)0;
        b["lastActivityAt"] = m.ContainsKey("lastActivityAt") ? m["lastActivityAt"] : (object)0;
        Dictionary<string, object> g = m.ContainsKey("goal") ? m["goal"] as Dictionary<string, object> : null;
        b["goal"] = g == null ? "" : S(g, "objective");
        b["title"] = S(m, "title");
        return b;
    }

    static void TrimEvents()
    {
        while (agentEvents.Count > MaxAgentEvents) agentEvents.RemoveAt(0);
    }

    static Dictionary<string, object> AgentState()
    {
        Dictionary<string, object> d = Obj();
        lock (agentLock)
        {
            d["state"] = agentState;
            d["title"] = agentTitle;
            d["sessionId"] = agentSession;
            d["goal"] = agentGoal;
            d["todos"] = agentTodos;
            d["board"] = agentBoard;
            d["note"] = agentNote;
            List<object> tail = agentEvents.Count > 120 ? agentEvents.GetRange(agentEvents.Count - 120, 120) : new List<object>(agentEvents);
            d["events"] = tail;
            d["eventCount"] = agentEvents.Count;
        }
        return d;
    }

    // ---------- 杂项 ----------
    static List<object> ShellList()
    {
        List<object> list = new List<object>();
        Dictionary<string, object> ps = Obj();
        ps["id"] = "powershell";
        ps["label"] = "PowerShell 5.1";
        ps["exe"] = "powershell.exe";
        list.Add(ps);
        string pwsh = FindPwsh();
        if (!string.IsNullOrEmpty(pwsh))
        {
            Dictionary<string, object> p7 = Obj();
            p7["id"] = "pwsh";
            p7["label"] = "PowerShell 7";
            p7["exe"] = pwsh;
            list.Add(p7);
        }
        Dictionary<string, object> cmd = Obj();
        cmd["id"] = "cmd";
        cmd["label"] = "cmd.exe";
        cmd["exe"] = "cmd.exe";
        list.Add(cmd);
        return list;
    }

    static string pwshPath;
    static bool pwshProbed;

    static string FindPwsh()
    {
        if (pwshProbed) return pwshPath;
        pwshProbed = true;
        try
        {
            string[] guesses = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe")
            };
            for (int i = 0; i < guesses.Length; i++) if (File.Exists(guesses[i])) { pwshPath = guesses[i]; return pwshPath; }
        }
        catch { }
        return pwshPath;
    }
}
}
