// remote-proxy.mjs - token-gated LAN access in front of `dsh web`
//
// WHY THIS EXISTS
// `dsh web` binds 127.0.0.1 and has no authentication at all -- upstream refuses
// `--host 0.0.0.0` on purpose, because an unauthenticated DSH endpoint is remote code
// execution for anyone who can reach it. So LAN/phone access is implemented HERE, as an
// explicit, opt-in door in front of the loopback server, never as a default:
//
//   * a token is required for every request (header, cookie, or ?token= once);
//   * only private/loopback source addresses are accepted unless --allow-public;
//   * nothing is started by the panel -- you run start-remote.cmd yourself;
//   * the loopback server itself is untouched (still 127.0.0.1, still no auth).
//
// TRANSPORT: unary RPCs use fetch (plain HTTP), but mux/host event streams are
// **WebSocket** downlinks, so this proxy must handle HTTP *and* upgrade requests.
// A plain HTTP proxy would render the page and then never receive an event.
//
// USAGE (normally via start-remote.ps1 / start-remote.cmd):
//   node remote-proxy.mjs --target-port 3080 --listen-port 3081 \
//        --token-file run/remote-token.txt [--bind 0.0.0.0] [--allow-public]
import http from 'node:http';
import net from 'node:net';
import fs from 'node:fs';
import crypto from 'node:crypto';
import path from 'node:path';
import { networkInterfaces } from 'node:os';

// ---------- arguments ----------

function parseArgs(argv) {
  const out = { bind: '0.0.0.0', allowPublic: false };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    if (a === '--target-port') out.targetPort = Number.parseInt(next(), 10);
    else if (a === '--listen-port') out.listenPort = Number.parseInt(next(), 10);
    else if (a === '--token-file') out.tokenFile = next();
    else if (a === '--token') out.token = next();
    else if (a === '--bind') out.bind = next();
    else if (a === '--allow-public') out.allowPublic = true;
    else if (a === '--help' || a === '-h') out.help = true;
    else {
      process.stderr.write(`unknown argument: ${a}\n`);
      out.help = true;
    }
  }
  return out;
}

const args = parseArgs(process.argv.slice(2));
if (args.help || !args.targetPort || !args.listenPort || (!args.token && !args.tokenFile)) {
  process.stderr.write(
    'usage: node remote-proxy.mjs --target-port <p> --listen-port <p> --token-file <f>\n' +
    '                              [--token <t>] [--bind 0.0.0.0] [--allow-public]\n',
  );
  process.exit(args.help ? 0 : 2);
}

function readToken() {
  if (args.token) return args.token.trim();
  try {
    return fs.readFileSync(args.tokenFile, 'utf8').trim();
  } catch (error) {
    process.stderr.write(`cannot read token file ${args.tokenFile}: ${error && error.message}\n`);
    process.exit(2);
  }
}

const TOKEN = readToken();
if (TOKEN.length < 16) {
  process.stderr.write('refusing to start: token is shorter than 16 characters\n');
  process.exit(2);
}
const TOKEN_HASH = crypto.createHash('sha256').update(TOKEN).digest();
const COOKIE = 'dsh_remote';
const TARGET = { host: '127.0.0.1', port: args.targetPort };
const HOP_BY_HOP = new Set([
  'connection', 'keep-alive', 'proxy-authenticate', 'proxy-authorization',
  'te', 'trailer', 'transfer-encoding', 'upgrade',
]);

// ---------- helpers ----------

// Constant-time token check: compare digests so length never leaks through timing.
function tokenMatches(candidate) {
  if (typeof candidate !== 'string' || candidate.length === 0) return false;
  const digest = crypto.createHash('sha256').update(candidate).digest();
  return digest.length === TOKEN_HASH.length && crypto.timingSafeEqual(digest, TOKEN_HASH);
}

function parseCookies(header) {
  const out = {};
  if (!header) return out;
  for (const part of header.split(';')) {
    const eq = part.indexOf('=');
    if (eq <= 0) continue;
    out[part.slice(0, eq).trim()] = decodeURIComponent(part.slice(eq + 1).trim());
  }
  return out;
}

function isPrivateAddress(remote) {
  if (!remote) return false;
  let addr = remote;
  if (addr.startsWith('::ffff:')) addr = addr.slice(7);
  if (addr === '::1' || addr.startsWith('fe80:') || addr.startsWith('fd') || addr.startsWith('fc')) return true;
  const parts = addr.split('.').map((n) => Number.parseInt(n, 10));
  if (parts.length !== 4 || parts.some((n) => !Number.isFinite(n))) return false;
  const [a, b] = parts;
  if (a === 127) return true;
  if (a === 10) return true;
  if (a === 192 && b === 168) return true;
  if (a === 172 && b >= 16 && b <= 31) return true;
  if (a === 169 && b === 254) return true; // link-local
  return false;
}

// ---------- brute-force damping ----------
const failures = new Map();          // ip -> { count, until }
const FAIL_AFTER = 3;
const MAX_DELAY_MS = 2000;

function noteFailure(ip) {
  const now = Date.now();
  const entry = failures.get(ip) ?? { count: 0, until: 0 };
  entry.count += 1;
  const delay = entry.count <= FAIL_AFTER ? 0 : Math.min(MAX_DELAY_MS, 250 * (entry.count - FAIL_AFTER));
  entry.until = now + delay;
  failures.set(ip, entry);
  if (failures.size > 500) failures.clear(); // keep the map bounded
  return delay;
}

function failureDelay(ip) {
  const entry = failures.get(ip);
  if (!entry) return 0;
  return Math.max(0, entry.until - Date.now());
}

// ---------- responses ----------

function loginPage(message) {
  return `<!DOCTYPE html>
<html lang="zh-CN"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>DeepSeek Harness · 远程访问</title>
<style>
body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;
background:#0f172a;color:#e2e8f0;font-family:system-ui,'Microsoft YaHei UI',sans-serif}
form{width:min(360px,86vw);background:#1e293b;padding:26px 24px;border-radius:14px;
box-shadow:0 12px 32px #0006}
h1{font-size:17px;margin:0 0 6px}p{font-size:12.5px;color:#94a3b8;margin:0 0 18px;line-height:1.6}
input{width:100%;box-sizing:border-box;padding:11px 12px;border-radius:9px;border:1px solid #334155;
background:#0f172a;color:#e2e8f0;font-size:14px;font-family:ui-monospace,Consolas,monospace}
button{margin-top:12px;width:100%;padding:11px;border:0;border-radius:9px;background:#2563eb;
color:#fff;font-size:14px;font-weight:600;cursor:pointer}
.err{color:#f87171;font-size:12.5px;margin:10px 0 0}
</style></head><body>
<form method="POST" action="/__auth">
  <h1>需要访问口令</h1>
  <p>这是 DeepSeek Harness 的远程入口：口令即等同于本机代码执行权限，
     请只在自己的可信网络使用，用完请关闭代理窗口。</p>
  <input name="token" type="password" placeholder="粘贴访问口令" autofocus autocomplete="off">
  <button type="submit">进入</button>
  ${message ? `<p class="err">${message}</p>` : ''}
</form></body></html>`;
}

function send(res, status, body, extraHeaders) {
  const headers = Object.assign({
    'content-type': 'text/html; charset=utf-8',
    'cache-control': 'no-store',
    'content-length': Buffer.byteLength(body),
  }, extraHeaders ?? {});
  res.writeHead(status, headers);
  res.end(body);
}

function forbiddenReason(req) {
  if (args.allowPublic) return null;
  if (isPrivateAddress(req.socket.remoteAddress)) return null;
  return '只接受局域网/本机会话（如需公网访问，请显式加 --allow-public）';
}

// ---------- HTTP ----------

const server = http.createServer((req, res) => {
  const ip = req.socket.remoteAddress ?? 'unknown';
  const blocked = forbiddenReason(req);
  if (blocked) return send(res, 403, loginPage(blocked));

  const url = new URL(req.url ?? '/', 'http://placeholder');

  // ---- auth ----
  if (url.pathname === '/__auth' && req.method === 'POST') {
    let body = '';
    req.on('data', (chunk) => {
      body += chunk;
      if (body.length > 4096) req.destroy();
    });
    req.on('end', () => {
      const candidate = new URLSearchParams(body).get('token') ?? '';
      if (!tokenMatches(candidate)) {
        const delay = noteFailure(ip);
        setTimeout(() => send(res, 401, loginPage('口令不正确')), delay);
        return;
      }
      failures.delete(ip);
      res.writeHead(302, {
        location: '/',
        'set-cookie': `${COOKIE}=${encodeURIComponent(TOKEN)}; Path=/; HttpOnly; SameSite=Lax`,
        'cache-control': 'no-store',
      });
      res.end();
    });
    return;
  }

  const cookies = parseCookies(req.headers.cookie);
  const authorized =
    tokenMatches(req.headers['x-dsh-token']) ||
    tokenMatches(cookies[COOKIE]) ||
    tokenMatches(url.searchParams.get('token') ?? '');

  if (!authorized) {
    const delay = failureDelay(ip) || noteFailure(ip);
    if (delay > 0) {
      setTimeout(() => send(res, 401, loginPage('尝试过于频繁，请稍候')), delay);
      return;
    }
    return send(res, 401, loginPage(''));
  }
  failures.delete(ip);

  // ?token= is accepted once, then traded for a cookie so it never stays in the URL bar
  if (url.searchParams.has('token')) {
    const clean = new URL(url);
    clean.searchParams.delete('token');
    res.writeHead(302, {
      location: clean.pathname + (clean.search ? clean.search : ''),
      'set-cookie': `${COOKIE}=${encodeURIComponent(TOKEN)}; Path=/; HttpOnly; SameSite=Lax`,
      'cache-control': 'no-store',
    });
    res.end();
    return;
  }

  // ---- proxy ----
  const headers = {};
  for (const [key, value] of Object.entries(req.headers)) {
    if (HOP_BY_HOP.has(key.toLowerCase())) continue;
    if (key.toLowerCase() === 'cookie') continue;      // never leak the remote token upstream
    headers[key] = value;
  }
  headers.host = `${TARGET.host}:${TARGET.port}`;
  // The service fence derives its trusted authorities from the loopback bind,
  // so browser Origin/Referer must look like loopback to be accepted.
  for (const [key, value] of Object.entries({ origin: 'http://', referer: 'http://' })) {
    const current = headers[key];
    if (typeof current === 'string') {
      try {
        const parsed = new URL(current);
        headers[key] = `${value}${TARGET.host}:${TARGET.port}${parsed.pathname}${parsed.search}`;
      } catch {
        delete headers[key];
      }
    }
  }

  const upstream = http.request({
    host: TARGET.host,
    port: TARGET.port,
    method: req.method,
    path: url.pathname + (url.search ? url.search : ''),
    headers,
  }, (up) => {
    const outHeaders = {};
    for (const [key, value] of Object.entries(up.headers)) {
      if (HOP_BY_HOP.has(key.toLowerCase())) continue;
      if (key.toLowerCase() === 'set-cookie') continue;
      outHeaders[key] = value;
    }
    res.writeHead(up.statusCode ?? 502, up.statusMessage, outHeaders);
    if (up.headers['set-cookie']) res.setHeader('set-cookie', up.headers['set-cookie']);
    // Streaming responses (event streams) must not be buffered
    if (typeof res.flushHeaders === 'function') res.flushHeaders();
    try { req.socket.setNoDelay(true); } catch { /* ignore */ }
    up.pipe(res);
    up.on('error', () => { try { res.destroy(); } catch { /* ignore */ } });
  });

  upstream.on('error', (error) => {
    if (res.headersSent) {
      try { res.destroy(); } catch { /* ignore */ }
      return;
    }
    const hint = error && error.code === 'ECONNREFUSED'
      ? `本地服务 127.0.0.1:${TARGET.port} 没有响应——请确认面板里的服务正在运行。`
      : `代理失败：${error && error.message ? error.message : String(error)}`;
    send(res, 502, loginPage(hint));
  });
  req.pipe(upstream);
});

// ---------- WebSocket / raw upgrade ----------

server.on('upgrade', (req, socket, head) => {
  const ip = req.socket.remoteAddress ?? 'unknown';
  if (forbiddenReason(req)) {
    socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');
    return;
  }
  const url = new URL(req.url ?? '/', 'http://placeholder');
  const cookies = parseCookies(req.headers.cookie);
  const authorized =
    tokenMatches(req.headers['x-dsh-token']) ||
    tokenMatches(cookies[COOKIE]) ||
    tokenMatches(url.searchParams.get('token') ?? '');
  if (!authorized) {
    noteFailure(ip);
    socket.end('HTTP/1.1 401 Unauthorized\r\nConnection: close\r\n\r\n');
    return;
  }
  failures.delete(ip);

  // Rebuild the handshake verbatim, with a loopback Host header, then splice the sockets.
  const lines = [`${req.method} ${url.pathname}${url.search} HTTP/1.1`];
  const raw = req.rawHeaders;
  for (let i = 0; i < raw.length; i += 2) {
    const name = raw[i];
    const value = raw[i + 1];
    const lower = name.toLowerCase();
    if (lower === 'host' || lower === 'cookie') continue;
    lines.push(`${name}: ${value}`);
  }
  lines.push(`Host: ${TARGET.host}:${TARGET.port}`);
  const handshake = `${lines.join('\r\n')}\r\n\r\n`;

  const upstream = net.connect(TARGET.port, TARGET.host, () => {
    upstream.write(handshake);
    if (head && head.length > 0) upstream.write(head);
    socket.pipe(upstream);
    upstream.pipe(socket);
  });
  const close = () => {
    try { upstream.destroy(); } catch { /* ignore */ }
    try { socket.destroy(); } catch { /* ignore */ }
  };
  upstream.on('error', close);
  socket.on('error', close);
  socket.on('close', close);
  upstream.on('close', close);
});

// ---------- start ----------

server.on('error', (error) => {
  process.stderr.write(`cannot listen on ${args.bind}:${args.listenPort}: ${error && error.message}\n`);
  process.exit(1);
});

server.listen(args.listenPort, args.bind, () => {
  const tokenPath = args.tokenFile ? path.resolve(args.tokenFile) : '(command line)';
  const lines = [
    '',
    '  ============================================================',
    '   DeepSeek Harness 远程访问已开启（令牌鉴权）',
    '  ============================================================',
    `   本地服务   http://127.0.0.1:${TARGET.port}`,
    `   监听地址   ${args.bind}:${args.listenPort}`,
    `   令牌文件   ${tokenPath}`,
    '',
    '   手机/其它设备在同一局域网时，打开：',
  ];
  for (const addr of localAddresses()) {
    lines.push(`     http://${addr}:${args.listenPort}/?token=<令牌文件里的字符串>`);
  }
  lines.push(
    '',
    '   ⚠ 持有这个令牌 = 可以在本机执行命令。只在可信网络使用；',
    '     用完请关闭这个窗口（或 Ctrl+C）即可立即收回入口。',
    '',
  );
  process.stdout.write(`${lines.join('\n')}\n`);
});

function localAddresses() {
  const out = [];
  try {
    for (const list of Object.values(networkInterfaces())) {
      for (const item of list ?? []) {
        if (item.family === 'IPv4' && !item.internal) out.push(item.address);
      }
    }
  } catch { /* ignore */ }
  if (out.length === 0) out.push('127.0.0.1');
  return out;
}
