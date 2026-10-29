#!/usr/bin/env node
// session-reader.mjs - 面板工作台的「会话事件流」边车（v3.5）
//
// 为什么需要它：DSH 把每一次会话事件追加进
//   %USERPROFILE%\.dsh\sessions\<编码后的cwd>\session-<id>\session.jsonl.zstd
// 该文件是**一串独立的 zstd 帧**（每次追加一个新帧），里面既有 user/message、
// tool/call、tool/result，也有 todo/write（Agent 的任务清单）与 goal/change（目标）。
// 这就是工作台「任务看板 / 活动流」唯一的真实数据来源——面板不该靠猜，也不该靠截图。
//
// 只用 Node 内置能力（zlib.zstdDecompressSync，Node >= 22.15），不引入任何依赖。
//
// 用法：
//   node session-reader.mjs --cwd C:\path\to\workspace [--poll 800] [--backfill 4194304]
//
// stdout：每行一个 JSON 事件（协议见下），stderr：诊断信息（面板只记录不显示）
//    {"t":"hello","pid":1234,"sessions":"...","cwd":"..."}
//    {"t":"reset","file":"...","sessionId":"...","title":"..."}     切换/首次绑定会话
//    {"t":"snapshot","goal":{...}|null,"todos":[...]|null,"title":"...",
//     "state":"running"|"idle","events":[...],"skipped":N}
//    {"t":"ev", "ev":{...}}                                          增量事件
//    {"t":"nostream","reason":"..."}                                 暂无匹配会话（继续等）
//
// ev.kind ∈ goal | todos | title | user | tool-call | tool-result | turn | session

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import zlib from 'node:zlib';

const ZSTD_MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);
const MAX_TEXT_PER_POLL = 12 * 1024 * 1024;
const MAX_EVENTS_BACKFILL = 300;
const FILE_NAME = 'session.jsonl.zstd';

function parseArgs(argv) {
  const out = { cwd: process.cwd(), poll: 800, backfill: 4 * 1024 * 1024, sessions: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    if (a === '--cwd') out.cwd = next();
    else if (a === '--poll') out.poll = Math.max(200, parseInt(next(), 10) || 800);
    else if (a === '--backfill') out.backfill = Math.max(65536, parseInt(next(), 10) || 0);
    else if (a === '--sessions') out.sessions = next();
  }
  return out;
}

const opts = parseArgs(process.argv.slice(2));
const SESSIONS_ROOT = opts.sessions || path.join(os.homedir(), '.dsh', 'sessions');
const TARGET = path.resolve(opts.cwd);
const TARGET_KEY = TARGET.toLowerCase();

const emit = (obj) => { try { process.stdout.write(JSON.stringify(obj) + '\n'); } catch { } };
const note = (msg) => { try { process.stderr.write(String(msg) + '\n'); } catch { } };

// ---------------------------------------------------------------- zstd 帧解码
// 一个追加式 zstd 文件 = frame frame frame ...；Node 没有多帧 API，
// 只能按魔数切分。魔数也可能出现在载荷里，所以「切出来解不开」时向后合并再试。
function frameOffsets(buf) {
  const offs = [];
  let i = buf.indexOf(ZSTD_MAGIC);
  while (i >= 0 && offs.length < 200000) { offs.push(i); i = buf.indexOf(ZSTD_MAGIC, i + 1); }
  return offs;
}

// 解码 buf（必须从一个帧边界开始）。返回 { text, consumed }：
// consumed = 已经成功解掉的字节数；尾部不完整的帧留在下轮再读。
function decodeFrames(buf) {
  const offs = frameOffsets(buf);
  if (offs.length === 0) return { text: '', consumed: 0 };
  if (offs[0] !== 0) return { text: '', consumed: 0 }; // 起点不是帧边界：放弃这轮
  offs.push(buf.length);
  let text = '';
  let i = 0;
  while (i < offs.length - 1) {
    let j = i + 1;
    let ok = false;
    let attempts = 0;
    while (j < offs.length && attempts < 24) {
      attempts++;
      try {
        const out = zlib.zstdDecompressSync(buf.subarray(offs[i], offs[j]));
        text += out.toString('utf8');
        ok = true;
        break;
      } catch { j++; }
    }
    if (!ok) break;            // 尾部帧还没写完（或数据损坏）：这轮到此为止
    if (text.length > MAX_TEXT_PER_POLL) break;
    i = j;
  }
  return { text, consumed: i < offs.length ? offs[i] : buf.length };
}

// ---------------------------------------------------------------- 事件抽取
function clip(s, n) {
  const t = String(s === undefined || s === null ? '' : s).replace(/\r/g, ' ').replace(/\n/g, ' ⏎ ');
  return t.length > n ? t.slice(0, n - 1) + '…' : t;
}

function firstText(content) {
  if (!Array.isArray(content)) return '';
  for (const part of content) {
    if (part && part.type === 'text' && typeof part.text === 'string') return part.text;
  }
  for (const part of content) {
    if (part && typeof part.text === 'string') return part.text;
    if (part && part.type === 'tool-result') { const t = firstText(part.content); if (t) return t; }
  }
  return '';
}

function toolSummary(name, args) {
  let a = args;
  if (typeof a === 'string') { try { a = JSON.parse(a); } catch { a = null; } }
  if (!a || typeof a !== 'object') return '';
  if (typeof a.command === 'string') return clip(a.command, 160);
  if (typeof a.file_path === 'string') return clip(a.file_path, 160);
  if (typeof a.path === 'string' && typeof a.pattern === 'string') return clip(a.pattern + '  ·  ' + a.path, 160);
  if (typeof a.pattern === 'string') return clip(a.pattern, 160);
  if (typeof a.url === 'string') return clip(a.url, 160);
  if (typeof a.description === 'string') return clip(a.description, 160);
  const keys = Object.keys(a);
  if (keys.length === 0) return '';
  return clip(JSON.stringify(a), 160);
}

// 把一行会话记录翻译成工作台事件（不关心的类型返回 null）
function toEvent(rec) {
  if (!rec || typeof rec !== 'object') return null;
  const d = rec.data || {};
  const time = rec.time || rec.time0 || 0;
  switch (rec.type) {
    case 'user/message': {
      const text = firstText(d.content);
      if (!text) return null;
      const from = d.source && d.source.kind ? d.source.kind : 'user';
      return { kind: 'user', time, text: clip(text, 240), from };
    }
    case 'tool/call':
      return { kind: 'tool-call', time, callId: d.callId, name: d.name || 'tool', summary: toolSummary(d.name, d.arguments) };
    case 'tool/result': {
      const content = d.message && d.message.content;
      const text = firstText(content);
      const isError = !!(d.message && (d.message.isError || d.message.error));
      return { kind: 'tool-result', time, callId: d.callId || (d.message && d.message.source && d.message.source.callId), ok: !isError, text: clip(text, 200) };
    }
    case 'todo/write': {
      const todos = Array.isArray(d.todos) ? d.todos.map((t) => ({ content: clip(t.content, 400), status: t.status || 'pending' })) : [];
      return { kind: 'todos', time, todos };
    }
    case 'goal/change': {
      const g = d.goal || {};
      return { kind: 'goal', time, operation: d.operation || d.kind || 'change', objective: clip(g.objective || '', 400), phase: g.phase || '', revision: g.revision || 0 };
    }
    case 'session/title':
      return { kind: 'title', time, title: clip(d.title || '', 120) };
    case 'turn/start':
      return { kind: 'turn', time, state: 'running', turn: d.turn || 0 };
    case 'turn/end': {
      const reason = d.reason && d.reason.kind ? d.reason.kind : '';
      return { kind: 'turn', time, state: 'idle', turn: d.turn || 0, reason };
    }
    case 'session/end-seed':
      return { kind: 'turn', time, state: 'idle', reason: 'end-seed' };
    default:
      return null;
  }
}

function parseLines(text, out, skipped) {
  const lines = text.split('\n');
  for (const line of lines) {
    if (!line || line.charCodeAt(0) !== 123) continue; // 只可能是 JSON 对象行
    let rec;
    try { rec = JSON.parse(line); } catch { skipped.n++; continue; }
    const ev = toEvent(rec);
    if (ev) out.push(ev);
  }
}

// ---------------------------------------------------------------- 会话发现
const cwdCache = new Map(); // file -> cwd（首帧里的 cwd 字段）

function readSessionHeader(file) {
  try {
    if (cwdCache.has(file)) return cwdCache.get(file);
    const fd = fs.openSync(file, 'r');
    const buf = Buffer.alloc(4096);
    const n = fs.readSync(fd, buf, 0, 4096, 0);
    fs.closeSync(fd);
    if (n <= 0) return null;
    const { text } = decodeFrames(buf.subarray(0, n));
    const first = text.split('\n').find((l) => l.startsWith('{'));
    if (!first) return null;
    const rec = JSON.parse(first);
    if (!rec || rec.type !== 'session') return null;
    const info = { cwd: rec.cwd || '', id: rec.id || '', createdAt: rec.createdAt || 0 };
    cwdCache.set(file, info);
    return info;
  } catch { return null; }
}

function candidateFiles() {
  const files = [];
  let dirs;
  try { dirs = fs.readdirSync(SESSIONS_ROOT, { withFileTypes: true }); } catch { return files; }
  for (const dir of dirs) {
    if (!dir.isDirectory()) continue;
    const base = path.join(SESSIONS_ROOT, dir.name);
    let subs;
    try { subs = fs.readdirSync(base, { withFileTypes: true }); } catch { continue; }
    for (const sub of subs) {
      const f = sub.isDirectory() ? path.join(base, sub.name, FILE_NAME) : (sub.name === FILE_NAME ? path.join(base, sub.name) : null);
      if (!f) continue;
      try { const st = fs.statSync(f); if (st.isFile()) files.push({ file: f, size: st.size, mtime: st.mtimeMs }); } catch { }
    }
  }
  files.sort((a, b) => b.mtime - a.mtime);
  return files.slice(0, 40);
}

function findActiveSession() {
  for (const c of candidateFiles()) {
    const info = readSessionHeader(c.file);
    if (!info) continue;
    if (!info.cwd) continue;
    if (path.resolve(info.cwd).toLowerCase() !== TARGET_KEY) continue;
    return { file: c.file, size: c.size, info };
  }
  return null;
}

// ---------------------------------------------------------------- 主循环
let current = null;     // { file, offset, info }
let pending = Buffer.alloc(0);
let backfillLeft = opts.backfill;
const skipped = { n: 0 };

// 回填：小于全量阈值就整份扫（任务清单/目标常常写在会话很靠前的位置，
// 只读尾部会「看得见活动、看不见清单」）；大文件才退化成只读尾部。
const FULL_SCAN_LIMIT = 32 * 1024 * 1024;

function backfillSession(sel) {
  let text = '';
  try {
    const size = sel.size;
    const from = size <= FULL_SCAN_LIMIT ? 0 : Math.max(0, size - opts.backfill);
    if (size <= 0) return { text: '', offset: 0 };
    const fd = fs.openSync(sel.file, 'r');
    const len = size - from;
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, from);
    fs.closeSync(fd);
    // 没从帧边界开始就向后找第一个边界
    let start = 0;
    if (from > 0) { start = buf.indexOf(ZSTD_MAGIC); if (start < 0) start = 0; }
    const dec = decodeFrames(buf.subarray(start));
    text = dec.text;
    return { text, offset: from + start + dec.consumed };
  } catch (e) { note('backfill failed: ' + e.message); return { text: '', offset: 0 }; }
}

function emitSnapshot(events, sessionInfo) {
  let goal = null;
  let todos = null;
  let title = sessionInfo.title || '';
  let state = 'idle';
  let lastTurn = 0;
  for (const ev of events) {
    if (ev.kind === 'goal') goal = { objective: ev.objective, phase: ev.phase, operation: ev.operation };
    else if (ev.kind === 'todos') todos = ev.todos;
    else if (ev.kind === 'title') title = ev.title;
    else if (ev.kind === 'turn') state = ev.state;
  }
  const tail = events.slice(-MAX_EVENTS_BACKFILL);
  for (let i = tail.length - 1; i >= 0; i--) { if (tail[i].kind === 'turn') { lastTurn = tail[i].turn || 0; break; } }
  emit({ t: 'snapshot', goal, todos, title, state, turn: lastTurn, events: tail, skipped: skipped.n });
}

function bindSession(sel) {
  const sessionId = (sel.info.id || path.basename(path.dirname(sel.file))).replace(/^session-/, '');
  current = { file: sel.file, offset: 0, info: sel.info, id: sessionId };
  emit({ t: 'reset', file: sel.file, sessionId, cwd: sel.info.cwd, createdAt: sel.info.createdAt });
  const bf = backfillSession(sel);
  current.offset = bf.offset;
  const events = [];
  if (bf.text) parseLines(bf.text, events, skipped);
  emitSnapshot(events, sel.info);
  note('bound ' + sel.file + ' offset=' + current.offset);
}

function pollOnce() {
  const sel = findActiveSession();
  if (!sel) {
    if (current) { current = null; emit({ t: 'nostream', reason: 'session-gone' }); }
    else emit({ t: 'nostream', reason: 'no-match' });
    return;
  }
  if (!current || current.file !== sel.file) { bindSession(sel); return; }
  let size;
  try { size = fs.statSync(sel.file).size; } catch { return; }
  if (size <= current.offset) return;
  let buf;
  try {
    const fd = fs.openSync(sel.file, 'r');
    const len = size - current.offset;
    buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, current.offset);
    fs.closeSync(fd);
  } catch (e) { note('read failed: ' + e.message); return; }
  const dec = decodeFrames(buf);
  if (dec.consumed > 0) current.offset += dec.consumed;
  if (!dec.text) return;
  const events = [];
  parseLines(dec.text, events, skipped);
  for (const ev of events) emit({ t: 'ev', ev, sessionId: current.id });
}

emit({ t: 'hello', pid: process.pid, cwd: TARGET, sessions: SESSIONS_ROOT, node: process.version });
note('watching ' + TARGET + ' under ' + SESSIONS_ROOT);

let stopped = false;
function shutdown() {
  if (stopped) return;
  stopped = true;
  emit({ t: 'bye' });
  process.exit(0);
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
process.on('SIGBREAK', shutdown);
process.stdin.on('end', shutdown);
process.stdin.resume();

function loop() {
  if (stopped) return;
  try { pollOnce(); } catch (e) { note('poll error: ' + (e && e.message)); }
  setTimeout(loop, opts.poll);
}

loop();
