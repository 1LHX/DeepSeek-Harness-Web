#!/usr/bin/env node
// session-reader.mjs - 面板工作台的「会话事件流」边车（v3.5 起，v3.6 加任务卡）
//
// 为什么需要它：DSH 把每一次会话事件追加进
//   %USERPROFILE%\.dsh\sessions\<编码后的cwd>\session-<id>\session.jsonl.zstd
// 该文件是**一串独立的 zstd 帧**（每次追加一个新帧），里面既有 user/message、
// tool/call、tool/result，也有 todo/write（Agent 自己的任务清单）与 goal/change。
//
// 工作台「任务看板」三个来源（v3.6）：
//   1) 轮次卡（turn card）：每轮对话自动生成一张卡——
//      turn/start 开卡（目标取最近一条 user/message）、tool/call 往里加条目、
//      turn/end 关卡（完成/中断 + 用时 + 工具数）。这保证「Agent 没有维护
//      todo 清单时看板也不空」——v3.5 只认 todo_write，实测踩过：Agent 不写
//      清单时「待规划 / 进行中」永远是空的，看起来像看板坏了。
//   2) Agent 自己的 todo/write 清单：有就按三态渲染，没有就只显示轮次卡。
//   3) agent/inbox/spliced（排队到下一轮/下一步的消息）落在「待规划」。
//
// 只用 Node 内置能力（zlib.zstdDecompressSync，Node >= 22.15），不引入任何依赖。
//
// 用法：
//   node workbench/session-reader.mjs --cwd C:\path\to\workspace [--poll 800] [--backfill 4194304]
//
// stdout：每行一个 JSON 事件，stderr：诊断信息（面板只记录不显示）
//    {"t":"hello","pid":..,"sessions":"..","cwd":".."}
//    {"t":"reset","file":"..","sessionId":"..","cwd":".."}
//    {"t":"snapshot","goal":..,"todos":[..],"todosAt":ms,"title":"..","state":"running|idle",
//     "turn":N,"inbox":[{text,time}],"turns":[card..],"current":card|null,"events":[ev..]}
//    {"t":"ev","ev":{kind:..}}
//    {"t":"nostream","reason":".."}
//
// 卡（card）：{turn, goal, startedAt, endedAt, status, toolCount, steps, tools:[{name,summary,time}]}
// 事件 kind：goal | todos | title | user | tool-call | tool-result | turn | inbox | board

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import zlib from 'node:zlib';

const ZSTD_MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);
const MAX_TEXT_PER_POLL = 12 * 1024 * 1024;
const MAX_EVENTS_BACKFILL = 300;
const MAX_TURNS = 20;
const MAX_TOOLS_PER_CARD = 6;
const BOARD_THROTTLE_MS = 700;
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
// 追加式 zstd 文件 = frame frame frame ...；Node 没有多帧 API，只能按魔数切分。
// 魔数也可能出现在载荷里，所以「切出来解不开」时向后合并再试。
function frameOffsets(buf) {
  const offs = [];
  let i = buf.indexOf(ZSTD_MAGIC);
  while (i >= 0 && offs.length < 200000) { offs.push(i); i = buf.indexOf(ZSTD_MAGIC, i + 1); }
  return offs;
}

function decodeFrames(buf) {
  const offs = frameOffsets(buf);
  if (offs.length === 0) return { text: '', consumed: 0 };
  if (offs[0] !== 0) return { text: '', consumed: 0 };
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
        text += zlib.zstdDecompressSync(buf.subarray(offs[i], offs[j])).toString('utf8');
        ok = true;
        break;
      } catch { j++; }
    }
    if (!ok) break;
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
  return Object.keys(a).length ? clip(JSON.stringify(a), 160) : '';
}

// ---------------------------------------------------------------- 看板状态
const board = {
  turns: [],
  current: null,
  inbox: [],
  todos: null,
  todosAt: 0,
  goal: null,
  title: '',
  state: 'idle',
  lastActivityAt: 0
};

function markBoard(now) {
  if (now && now > board.lastActivityAt) board.lastActivityAt = now;
  boardDirty = true;
}

function openTurn(turnNo, time) {
  if (board.current) closeTurn(time, 'superseded');
  const queued = board.inbox.shift();
  board.current = {
    turn: turnNo,
    goal: queued ? queued.text : '',
    startedAt: time,
    endedAt: 0,
    status: 'running',
    reason: '',
    toolCount: 0,
    steps: 0,
    tools: []
  };
  board.state = 'running';
  markBoard(time);
}

function closeTurn(time, reason) {
  const cur = board.current;
  if (!cur) return;
  cur.endedAt = time || Date.now();
  cur.status = reason === 'completed' ? 'completed' : (reason || 'ended');
  cur.reason = reason || 'ended';
  board.turns.push(cur);
  while (board.turns.length > MAX_TURNS) board.turns.shift();
  board.current = null;
  board.state = 'idle';
  markBoard(time);
}

function cardView(t, open) {
  return {
    turn: t.turn,
    goal: t.goal,
    startedAt: t.startedAt,
    endedAt: t.endedAt,
    status: open ? 'running' : t.status,
    reason: t.reason || '',
    toolCount: t.toolCount,
    steps: t.steps,
    tools: t.tools.slice(-MAX_TOOLS_PER_CARD)
  };
}

function boardView() {
  return {
    turns: board.turns.slice(-MAX_TURNS).map((t) => cardView(t, false)),
    current: board.current ? cardView(board.current, true) : null,
    inbox: board.inbox.slice(-8),
    todosAt: board.todosAt,
    goal: board.goal ? board.goal.objective : '',
    title: board.title,
    lastActivityAt: board.lastActivityAt
  };
}

let boardDirty = false;
let lastBoardSent = 0;

function flushBoard() {
  if (!boardDirty) return;
  const now = Date.now();
  if (now - lastBoardSent < BOARD_THROTTLE_MS) return;
  boardDirty = false;
  lastBoardSent = now;
  emit({ t: 'ev', ev: { kind: 'board', time: now, board: boardView() } });
}

// 把一行会话记录喂进看板状态，并返回给界面的轻量事件
function toEvent(rec) {
  if (!rec || typeof rec !== 'object') return null;
  const d = rec.data || {};
  const time = rec.time || rec.time0 || 0;
  switch (rec.type) {
    case 'user/message': {
      const text = firstText(d.content);
      if (!text) return null;
      const from = d.source && d.source.kind ? d.source.kind : 'user';
      // 正在跑的这一轮还没目标时，用这条用户消息当目标
      // （DSH 的 user/message 常在本轮 turn/start 之后才落盘）
      if (board.current && !board.current.goal && from !== 'plugin') {
        board.current.goal = clip(text, 200);
        markBoard(time);
      }
      return { kind: 'user', time, text: clip(text, 240), from };
    }
    case 'agent/inbox/spliced': {
      const inserted = Array.isArray(d.inserted) ? d.inserted : [];
      for (const item of inserted) {
        if (!item || (item.role && item.role !== 'user')) continue;
        // 插件注入的通知（如「审批策略已变更」）不是用户目标，别占「待规划」的卡位
        if (item.source && item.source.kind === 'plugin') continue;
        const text = clip(firstText(item.content), 160);
        if (text) board.inbox.push({ text, time, target: d.target || 'next-turn' });
      }
      while (board.inbox.length > 8) board.inbox.shift();
      markBoard(time);
      return { kind: 'inbox', time, count: board.inbox.length };
    }
    case 'tool/call': {
      const summary = toolSummary(d.name, d.arguments);
      if (board.current) {
        board.current.toolCount++;
        board.current.tools.push({ name: d.name || 'tool', summary, time });
        while (board.current.tools.length > 40) board.current.tools.shift();
      }
      markBoard(time);
      return { kind: 'tool-call', time, callId: d.callId, name: d.name || 'tool', summary };
    }
    case 'tool/result': {
      const content = d.message && d.message.content;
      const text = firstText(content);
      const isError = !!(d.message && (d.message.isError || d.message.error));
      markBoard(time);
      return { kind: 'tool-result', time, callId: d.callId || (d.message && d.message.source && d.message.source.callId), ok: !isError, text: clip(text, 200) };
    }
    case 'todo/write': {
      const todos = Array.isArray(d.todos)
        ? d.todos.map((t) => ({ content: clip(t.content, 400), status: t.status || 'pending' }))
        : [];
      board.todos = todos;
      board.todosAt = time;
      markBoard(time);
      return { kind: 'todos', time, todos };
    }
    case 'goal/change': {
      const g = d.goal || {};
      board.goal = { objective: clip(g.objective || '', 400), phase: g.phase || '', operation: d.operation || '' };
      markBoard(time);
      return { kind: 'goal', time, operation: d.operation || d.kind || 'change', objective: board.goal.objective, phase: board.goal.phase, revision: g.revision || 0 };
    }
    case 'session/title':
      board.title = clip(d.title || '', 120);
      return { kind: 'title', time, title: board.title };
    case 'turn/start': {
      openTurn(d.turn || 0, time);
      return { kind: 'turn', time, state: 'running', turn: d.turn || 0 };
    }
    case 'turn/end': {
      const reason = d.reason && d.reason.kind ? d.reason.kind : '';
      closeTurn(time, reason);
      return { kind: 'turn', time, state: 'idle', turn: d.turn || 0, reason };
    }
    case 'session/end-seed':
      closeTurn(time, 'end-seed');
      return { kind: 'turn', time, state: 'idle', reason: 'end-seed' };
    case 'step/start':
      if (board.current) { board.current.steps++; }
      markBoard(time);
      return null;
    default:
      return null;
  }
}

function parseLines(text, out, skipped) {
  const lines = text.split('\n');
  for (const line of lines) {
    if (!line || line.charCodeAt(0) !== 123) continue;
    let rec;
    try { rec = JSON.parse(line); } catch { skipped.n++; continue; }
    const ev = toEvent(rec);
    if (ev) out.push(ev);
  }
}

// ---------------------------------------------------------------- 会话发现
const cwdCache = new Map();

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
    if (!info || !info.cwd) continue;
    if (path.resolve(info.cwd).toLowerCase() !== TARGET_KEY) continue;
    return { file: c.file, size: c.size, info };
  }
  return null;
}

// ---------------------------------------------------------------- 主循环
let current = null;
const skipped = { n: 0 };
const FULL_SCAN_LIMIT = 32 * 1024 * 1024;

function backfillSession(sel) {
  try {
    const size = sel.size;
    if (size <= 0) return { text: '', offset: 0 };
    const from = size <= FULL_SCAN_LIMIT ? 0 : Math.max(0, size - opts.backfill);
    const fd = fs.openSync(sel.file, 'r');
    const len = size - from;
    const buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, from);
    fs.closeSync(fd);
    let start = 0;
    if (from > 0) { start = buf.indexOf(ZSTD_MAGIC); if (start < 0) start = 0; }
    const dec = decodeFrames(buf.subarray(start));
    return { text: dec.text, offset: from + start + dec.consumed };
  } catch (e) { note('backfill failed: ' + e.message); return { text: '', offset: 0 }; }
}

function emitSnapshot(events, sessionInfo) {
  const view = boardView();
  emit({
    t: 'snapshot',
    goal: board.goal,
    todos: board.todos,
    todosAt: board.todosAt,
    title: board.title || sessionInfo.title || '',
    state: board.state,
    turn: board.current ? board.current.turn : (board.turns.length ? board.turns[board.turns.length - 1].turn : 0),
    inbox: view.inbox,
    turns: view.turns,
    current: view.current,
    lastActivityAt: view.lastActivityAt,
    events: events.slice(-MAX_EVENTS_BACKFILL),
    skipped: skipped.n
  });
}

function resetBoard() {
  board.turns = [];
  board.current = null;
  board.inbox = [];
  board.todos = null;
  board.todosAt = 0;
  board.goal = null;
  board.title = '';
  board.state = 'idle';
  board.lastActivityAt = 0;
  boardDirty = false;
  lastBoardSent = 0;
}

function bindSession(sel) {
  const sessionId = (sel.info.id || path.basename(path.dirname(sel.file))).replace(/^session-/, '');
  resetBoard();
  current = { file: sel.file, offset: 0, info: sel.info, id: sessionId };
  emit({ t: 'reset', file: sel.file, sessionId, cwd: sel.info.cwd, createdAt: sel.info.createdAt });
  const bf = backfillSession(sel);
  current.offset = bf.offset;
  const events = [];
  if (bf.text) parseLines(bf.text, events, skipped);
  boardDirty = false;
  emitSnapshot(events, sel.info);
  note('bound ' + sel.file + ' offset=' + current.offset + ' turns=' + board.turns.length);
}

function pollOnce() {
  const sel = findActiveSession();
  if (!sel) {
    if (current) { current = null; resetBoard(); emit({ t: 'nostream', reason: 'session-gone' }); }
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
  flushBoard();
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
