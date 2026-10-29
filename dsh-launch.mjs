// dsh-launch.mjs - 快速启动路径（替代「冷启动 PowerShell + start-service.ps1」）
//
// 为什么存在：面板用它启动服务时，PowerShell 5.1 的冷启动要 1.45 秒，脚本本身还要 0.47 秒；
// 而 node 的冷启动只要 0.10–0.15 秒。实测（2026-10-29）走 PowerShell 时「双击→页面」5.84 秒，
// 其中约 1.9 秒花在 PowerShell 上。
//
// 安全性设计（关键）：这里**不是**第二套启动策略。它只做「直连 bin.js」这一条最确定的快路径，
// 任何与预期不符的情况都立刻转交 start-service.ps1（那份脚本仍然是唯一权威：
// 试次阶梯、端口顺延、npx 兜底、失败现场保留都在那里）。转交时退出码 0，由 ps1 继续。
// 转交触发条件：找不到 bin.js / 配置端口被占 / 拉起后进程提前退出 / 任何异常。
//
// 日志：把**文件描述符**交给子进程（stdio: ['ignore', fd, fd]），而不是管道——
// 这样本进程退出后服务仍能正常写 stdout/stderr（管道会 EPIPE 把服务写死）。
//
// 用法（由面板调用，也可手动诊断）：
//   node dsh-launch.mjs
//   node dsh-launch.mjs --no-delegate    只跑快路径，失败直接退出（调试用）
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = path.dirname(fileURLToPath(import.meta.url));
const RUN = path.join(ROOT, 'run');
const LOGS = path.join(ROOT, 'logs');
const CFG = path.join(ROOT, 'dsh-web.config');
const TIMING = path.join(RUN, 'boot-timing.log');
const PID_FILE = path.join(RUN, 'dsh-web.pid');
const PORT_FILE = path.join(RUN, 'dsh-web.port');
const HOOK = path.join(ROOT, 'hooks', 'compile-cache.mjs');
const CACHE_DIR = path.join(RUN, 'node-compile-cache');
const NO_DELEGATE = process.argv.includes('--no-delegate');
const WAIT_MS = 120000;

const startedAt = Date.now();
const marks = [];

// ---------- 基础设施 ----------

function stamp() {
  const d = new Date();
  const p = (n, w = 2) => String(n).padStart(w, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ` +
    `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}.${p(d.getMilliseconds(), 3)}`;
}

// 与 start-service.ps1 / 面板写同一个文件、同一种行格式，show-timing.cmd 直接能看
function timing(text) {
  marks.push(text);
  try {
    fs.mkdirSync(RUN, { recursive: true });
    fs.appendFileSync(TIMING, `${stamp()} [launcher] ${text}\n`, 'utf8');
  } catch { /* 打点失败绝不影响启动 */ }
}

function errlog(text) {
  try {
    fs.mkdirSync(LOGS, { recursive: true });
    fs.appendFileSync(path.join(LOGS, 'dsh-web.err.log'), `[launcher] ${text}\n`, 'utf8');
  } catch { /* ignore */ }
}

function elapsed() {
  return `${((Date.now() - startedAt) / 1000).toFixed(2)}s`;
}

// 唯一的兜底出口：转交 start-service.ps1
//
// 为什么必须同步阻塞、且不能关掉 windowsHide 之外的花样（实测三条都踩过）：
//   1) 直接 spawn + detached:true  -> PowerShell 落到无控制台环境，静默不启动；
//   2) 直接 spawn + detached:false -> 随本进程退出被控制台关闭事件带走；
//   3) cmd /c start "" /b ...      -> 本进程的控制台一销毁，cmd 跟着死，PowerShell 根本没起。
// 结论：让 PowerShell 以**子进程共享本进程控制台**的方式同步跑（本进程活着，它的控制台就活着）。
// 因此这里用 spawnSync 阻塞到 ps1 结束；代价是罕见情况下多留一个隐藏的 node 进程 1~2 分钟，
// 换来的是「绝不出现两个服务、也绝不静默失败」。
function delegate(reason) {
  timing(`delegate to start-service.ps1 (${reason}) at +${elapsed()}`);
  const ps1 = path.join(ROOT, 'start-service.ps1');
  if (!fs.existsSync(ps1)) {
    errlog(`cannot delegate (${reason}): start-service.ps1 missing`);
    process.exit(1);
  }
  if (NO_DELEGATE) {
    process.stderr.write(`[launcher] not delegating (--no-delegate): ${reason}\n`);
    process.exit(2);
  }
  let status = null;
  try {
    const result = spawnSync('powershell.exe',
      ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ps1],
      { cwd: ROOT, stdio: 'ignore', timeout: 300000 });
    status = result.status;
    if (result.error) errlog(`delegation error (${reason}): ${result.error.message}`);
  } catch (error) {
    errlog(`delegation threw (${reason}): ${error && error.message}`);
  }
  timing(`delegation finished (exit=${status})`);
  errlog(`delegated to start-service.ps1 (${reason}); exit=${status}`);
  process.exit(status === 0 ? 0 : 1);
}

// ---------- 配置 ----------

function readConfig() {
  const out = { port: 3080, compileCache: false };
  let text = '';
  try { text = fs.readFileSync(CFG, 'utf8'); } catch { return out; }
  for (const line of text.split(/\r?\n/)) {
    const p = line.match(/^\s*port\s*=\s*(\d+)/i);
    if (p) out.port = Number.parseInt(p[1], 10);
    const c = line.match(/^\s*compilecache\s*=\s*(\S+)/i);
    if (c && /^(1|true|on|yes)$/i.test(c[1].trim())) out.compileCache = true;
  }
  if (!Number.isInteger(out.port) || out.port <= 0 || out.port > 65535) out.port = 3080;
  return out;
}

function readPortFile() {
  try {
    const value = Number.parseInt(fs.readFileSync(PORT_FILE, 'utf8').trim(), 10);
    return Number.isInteger(value) && value > 0 && value <= 65535 ? value : null;
  } catch { return null; }
}

function readPidFile() {
  try {
    const value = Number.parseInt(fs.readFileSync(PID_FILE, 'utf8').trim(), 10);
    return Number.isInteger(value) && value > 0 ? value : null;
  } catch { return null; }
}

function pidAlive(pid) {
  if (!pid) return false;
  try { process.kill(pid, 0); return true; } catch { return false; }
}

function portOpen(port, timeoutMs = 300) {
  return new Promise((resolve) => {
    const socket = net.connect({ host: '127.0.0.1', port });
    let settled = false;
    const done = (value) => {
      if (settled) return;
      settled = true;
      try { socket.destroy(); } catch { /* ignore */ }
      resolve(value);
    };
    socket.setTimeout(timeoutMs, () => done(false));
    socket.once('connect', () => done(true));
    socket.once('error', () => done(false));
  });
}

// ---------- 入口定位（与 start-service.ps1 的 Resolve-DshBinJs 同序） ----------

function resolveBinJs() {
  const candidates = [];
  const npxRoot = path.join(process.env.LOCALAPPDATA ?? '', 'npm-cache', '_npx');
  try {
    for (const dir of fs.readdirSync(npxRoot, { withFileTypes: true })) {
      if (!dir.isDirectory()) continue;
      candidates.push(path.join(npxRoot, dir.name, 'node_modules', '@deepseek-ai', 'dsh', 'lib', 'bin.js'));
    }
  } catch { /* no npx cache */ }
  candidates.push(path.join(process.env.APPDATA ?? '', 'npm', 'node_modules', '@deepseek-ai', 'dsh', 'lib', 'bin.js'));
  candidates.push(path.join(path.dirname(process.execPath), 'node_modules', '@deepseek-ai', 'dsh', 'lib', 'bin.js'));

  let best = null;
  let bestTime = -1;
  for (const candidate of candidates) {
    try {
      const stat = fs.statSync(candidate);
      if (stat.isFile() && stat.mtimeMs > bestTime) { best = candidate; bestTime = stat.mtimeMs; }
    } catch { /* skip */ }
  }
  return best;
}

// ---------- 日志文件 ----------

function logPaths() {
  fs.mkdirSync(LOGS, { recursive: true });
  let out = path.join(LOGS, 'dsh-web.log');
  let err = path.join(LOGS, 'dsh-web.err.log');
  // 轮转：>5MB 保留一代（与 start-service.ps1 一致）
  for (const file of [out, err]) {
    try {
      const st = fs.statSync(file);
      if (st.size > 5 * 1024 * 1024) fs.renameSync(file, `${file}.1`);
    } catch { /* 不存在或无权：忽略 */ }
  }
  // 被别的进程独占时改用时间戳文件名，绝不因此起不来
  const writable = (file) => {
    try { if (!fs.existsSync(file)) return true; fs.closeSync(fs.openSync(file, 'a')); return true; } catch { return false; }
  };
  if (!writable(out) || !writable(err)) {
    const tag = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 15);
    out = path.join(LOGS, `dsh-web.${tag}.log`);
    err = path.join(LOGS, `dsh-web.${tag}.err.log`);
    timing('service logs were locked; using timestamped file names');
  }
  return { out, err };
}

function preserveLogs(tag) {
  for (const name of ['dsh-web.log', 'dsh-web.err.log']) {
    const file = path.join(LOGS, name);
    try { if (fs.existsSync(file)) fs.renameSync(file, `${file}.${tag}`); } catch { /* ignore */ }
  }
}

// ---------- 主流程 ----------

async function main() {
  fs.mkdirSync(RUN, { recursive: true });
  timing('start');

  const cfg = readConfig();
  const targetPort = cfg.port;

  // 1) 已经在跑？
  const recorded = readPortFile();
  const recordedPid = readPidFile();
  if (recorded && pidAlive(recordedPid) && await portOpen(recorded)) {
    timing(`already running on recorded port ${recorded} (pid ${recordedPid}); nothing to do`);
    process.exit(0);
  }
  // 配置端口被占：交给 ps1 判断「是不是我们自己的 dsh」并做顺延
  if (await portOpen(targetPort)) {
    delegate(`port ${targetPort} already in use`);
    return;
  }

  // 2) 入口
  const binJs = resolveBinJs();
  if (!binJs) {
    delegate('no dsh entry point found (npx cache / global npm / beside node.exe)');
    return;
  }
  timing(`entry: ${binJs}`);

  // 3) 编译缓存钩子：先探测再使用，探测失败就丢掉钩子继续走快路径
  let hookUri = null;
  const env = { ...process.env };
  if (cfg.compileCache && fs.existsSync(HOOK)) {
    try {
      hookUri = new URL(`file:///${HOOK.replace(/\\/g, '/')}`).href;
      fs.mkdirSync(CACHE_DIR, { recursive: true });
      env.DSH_COMPILE_CACHE_DIR = CACHE_DIR;
      env.DSH_COMPILE_CACHE_FLUSH_MS = '15000';
      const probe = spawnSync(process.execPath, ['--import', hookUri, '-e', 'process.exit(0)'],
        { timeout: 10000, windowsHide: true });
      if (probe.status === 0) {
        timing('compile cache hook ok');
      } else {
        timing(`compile cache hook probe failed (status ${probe.status}); launching without it`);
        hookUri = null;
        delete env.DSH_COMPILE_CACHE_DIR;
        delete env.DSH_COMPILE_CACHE_FLUSH_MS;
      }
    } catch (error) {
      timing(`compile cache hook unavailable: ${error && error.message}`);
      hookUri = null;
      delete env.DSH_COMPILE_CACHE_DIR;
      delete env.DSH_COMPILE_CACHE_FLUSH_MS;
    }
  }

  // 4) 拉起服务：stdio 直接给文件描述符（不是管道），本进程退出后服务照常写日志
  const logs = logPaths();
  let fdOut;
  let fdErr;
  try {
    fdOut = fs.openSync(logs.out, 'a');
    fdErr = fs.openSync(logs.err, 'a');
  } catch (error) {
    delegate(`cannot open log files: ${error && error.message}`);
    return;
  }

  const args = [binJs, 'web', '--port', String(targetPort)];
  if (hookUri) args.unshift('--import', hookUri);

  let child;
  try {
    child = spawn(process.execPath, args, {
      cwd: ROOT,
      stdio: ['ignore', fdOut, fdErr],
      windowsHide: true,
      // 必须 detached：实测（2026-10-29）非 detached 时，启动器一退出子进程就静默死亡
      // ——Windows 上子进程与父进程共用同一进程组/控制台关联，父进程退出触发控制台关闭，
      // Node 把它映射成 SIGHUP，而 dsh 只处理 SIGINT/SIGTERM，于是被默认动作终止。
      // detached:true 让服务拥有独立的进程组，启动器退出后它照常服务（已实测 HTTP 200）。
      detached: true,
      env,
    });
  } catch (error) {
    try { fs.closeSync(fdOut); fs.closeSync(fdErr); } catch { /* ignore */ }
    delegate(`spawn failed: ${error && error.message}`);
    return;
  } finally {
    // 子进程已持有自己的句柄副本，父进程这边立刻关掉
    try { fs.closeSync(fdOut); } catch { /* ignore */ }
    try { fs.closeSync(fdErr); } catch { /* ignore */ }
  }

  if (!child || !child.pid) {
    delegate('spawn returned no pid');
    return;
  }
  fs.writeFileSync(PID_FILE, String(child.pid), 'ascii');
  fs.writeFileSync(PORT_FILE, String(targetPort), 'ascii');
  timing(`spawned pid=${child.pid} at +${((Date.now() - startedAt) / 1000).toFixed(3)}s`);
  child.unref();

  // 5) 等端口
  const deadline = Date.now() + WAIT_MS;
  while (Date.now() < deadline) {
    if (await portOpen(targetPort)) {
      timing(`PORT READY in ${((Date.now() - startedAt) / 1000).toFixed(2)}s (pid=${child.pid})`);
      process.exit(0);
    }
    if (child.exitCode !== null) break;
    await new Promise((r) => setTimeout(r, 150));
  }

  if (child.exitCode !== null) {
    timing(`process exited before the port opened (exit=${child.exitCode})`);
    preserveLogs('failed-launcher');
    errlog(`service exited before opening port ${targetPort} (exit=${child.exitCode}); handing over to start-service.ps1`);
    delegate('child exited before port ready');
    return;
  }

  timing(`no port after ${WAIT_MS / 1000}s, process still running (pid=${child.pid})`);
  errlog(`no port ${targetPort} after ${WAIT_MS / 1000}s; process ${child.pid} left running`);
  process.exit(1);
}

main().catch((error) => {
  try { errlog(`unexpected: ${error && error.stack ? error.stack : error}`); } catch { /* ignore */ }
  delegate(`unexpected: ${error && error.message}`);
});
