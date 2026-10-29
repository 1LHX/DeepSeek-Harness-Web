// workbench/smoke.mjs - 工作台自动化冒烟（CDP 驱动真实面板里的界面与桥接）
//
// 为什么需要它：工作台是「跑起来才知道对不对」的部分——界面注入、postMessage 桥接、
// 终端子进程、会话边车，任何一环坏掉的表现都只是「界面不出现 / 一直空」，日志里没有栈。
// 这个脚本一次验完：挂载了吗 / 桥接通不通 / 五个页签有没有内容 / 终端能不能跑 /
// 变更审阅能不能「编辑 -> 预览 -> 应用 -> 回滚」。
//
// 用法：
//   1) dsh-web.config 里临时写 debugport=9222 并重启面板（脚本连的是 CDP）
//   2) node workbench/smoke.mjs         （可选 --port 9222）
//   3) 验完把 debugport 注释掉，重启面板
//
// 退出码：0 = 全通过；1 = 有失败项（stdout 有逐项明细）。
// 安全：只读工作区；只写一个临时文件 wb-scratch-smoke.txt，且最后用 changes.revert 删掉它。
//
// 注意：本文件刻意不使用任何反斜杠转义（换行一律用 String.fromCharCode(10)）——
// 页面侧表达式是拼成字符串再求值的，斜杠转义在两层字符串里最容易出错。

const argPort = process.argv.indexOf('--port');
const port = argPort >= 0 ? Number(process.argv[argPort + 1] || 9222) : Number(process.env.CDP_PORT || 9222);
const SCRATCH = 'wb-scratch-smoke.txt';
const LF = 'String.fromCharCode(10)';

let targets = null;
try {
  const res = await fetch('http://127.0.0.1:' + port + '/json/list');
  targets = await res.json();
} catch (e) {
  console.error('[smoke] 连不上 CDP 端口 ' + port + '：' + e.message);
  console.error('[smoke] 先在 dsh-web.config 打开 debugport=9222 并重启面板（README「开发用：CDP 巡检开关」）');
  process.exit(1);
}
const page = (targets || []).find((t) => t.type === 'page') || (targets || [])[0];
if (!page) { console.error('[smoke] 没有可用的页面目标'); process.exit(1); }

const ws = new WebSocket(page.webSocketDebuggerUrl);
const waiters = new Map();
const pageErrors = [];
let seq = 0;
await new Promise((res, rej) => { ws.addEventListener('open', res); ws.addEventListener('error', rej); });
ws.addEventListener('message', (ev) => {
  let m = null;
  try { m = JSON.parse(ev.data); } catch (e) { return; }
  if (m.id && waiters.has(m.id)) { waiters.get(m.id)(m); waiters.delete(m.id); return; }
  if (m.method === 'Runtime.exceptionThrown') { pageErrors.push(String(m.params.exceptionDetails.text)); }
});
const send = (method, params) => {
  const id = ++seq;
  return new Promise((r) => { waiters.set(id, r); ws.send(JSON.stringify({ id, method, params: params || {} })); });
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function ev(expr) {
  const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true, userGesture: true });
  if (r.result.exceptionDetails) {
    const d = r.result.exceptionDetails;
    return { __error: (d.exception && d.exception.description) || d.text };
  }
  return r.result.result.value;
}

const SR = 'document.getElementById("dsh-workbench-host").shadowRoot';
const SEL = (attr, val) => 's.querySelector(String.fromCharCode(91)+"' + attr + '="+String.fromCharCode(34)+"' + val + '"+String.fromCharCode(34)+String.fromCharCode(93))';
const Q = (tag) => 's.getElementById("' + tag + '")';

const results = [];
function check(name, ok, detail) {
  const t = detail === undefined ? '' : String(detail);
  results.push({ name: name, ok: !!ok });
  console.log((ok ? '  [ok]   ' : '  [FAIL] ') + name + (t ? '  — ' + t : ''));
}

await send('Runtime.enable');
console.log('=== 工作台冒烟（CDP :' + port + '）===');

// 测试用桥接客户端（不进产品代码）：注意 args 不能覆盖信封的 op/id
const clientJs = [
  '(function () {',
  '  window.__smokeCall = function (op, args) {',
  '    return new Promise(function (resolve, reject) {',
  '      var id = "smoke-" + Math.random().toString(36).slice(2);',
  '      function h(e) { var m = e.data; if (m && m.id === id) { window.chrome.webview.removeEventListener("message", h); resolve(m); } }',
  '      window.chrome.webview.addEventListener("message", h);',
  '      var msg = { op: op, id: id };',
  '      if (args) { for (var k in args) { if (k === "id" || k === "op") { continue; } msg[k] = args[k]; } }',
  '      window.chrome.webview.postMessage(JSON.stringify(msg));',
  '      setTimeout(function () { window.chrome.webview.removeEventListener("message", h); reject(new Error("timeout:" + op)); }, 30000);',
  '    });',
  '  };',
  '  return "ok";',
  '})()'
].join(String.fromCharCode(10));
const installed = await ev(clientJs);
check('测试客户端注入', installed === 'ok', installed);

const mounted = await ev('!!document.getElementById("dsh-workbench-host")');
check('工作台已挂载', mounted, mounted);
const api = await ev('!!window.__DSH_WORKBENCH__');
check('全局入口存在', api, api);

// 1) 桥接
const hello = await ev('__smokeCall("hello").then(function(m){return m.ok?m.data:{__error:m.error};})');
if (hello && !hello.__error) {
  check('桥接 hello', true, 'v' + hello.version + ' · ' + hello.workspace + ' · shells=' + ((hello.shells || []).length));
} else {
  check('桥接 hello', false, hello && hello.__error);
}

// 2) 文件页
await ev('window.__DSH_WORKBENCH__.open("files")');
await sleep(700);
const treeRows = await ev('(function(){var s=' + SR + ';return s.querySelectorAll("#wb-tree .wb-row").length;})()');
check('文件树有内容', treeRows > 0, treeRows + ' 行');
const openFile = await ev('(function(){var s=' + SR + ';var rows=Array.from(s.querySelectorAll("#wb-tree .wb-row"));var t=rows.find(function(r){var x=r.textContent;return x.indexOf(String.fromCharCode(9654)) < 0 && (x.indexOf(".md") > 0 || x.indexOf(".cs") > 0 || x.indexOf(".mjs") > 0);});if(!t)return "no-file-row";t.click();return "ok";})()');
await sleep(900);
const previewLines = await ev('(function(){var s=' + SR + ';return s.querySelectorAll("#wb-file-view .wb-line").length;})()');
check('文件预览渲染', openFile === 'ok' && previewLines > 0, openFile + ' / ' + previewLines + ' 行');

// 3) 终端
await ev('window.__DSH_WORKBENCH__.open("term")');
await sleep(400);
const runCmd = await ev('(function(){var s=' + SR + ';s.getElementById("wb-term-clear").click();var i=s.getElementById("wb-cmd");i.value="git rev-parse --short HEAD";i.dispatchEvent(new KeyboardEvent("keydown",{key:"Enter",bubbles:true}));return "sent";})()');
await sleep(3500);
const termOut = await ev('(function(){var s=' + SR + ';return (s.getElementById("wb-term-out").textContent||"");})()');
const termFlat = String(termOut || '').split(String.fromCharCode(10)).join(' ').slice(-70);
check('终端执行命令', runCmd === 'sent' && String(termOut).indexOf('退出码') >= 0, termFlat);
const policyDanger = await ev('__smokeCall("term.policy",{shell:"powershell",command:"Remove-Item -Recurse -Force x"}).then(function(m){return m.data.level;})');
check('危险命令被识别', policyDanger === 'danger', policyDanger);
const policyAllow = await ev('__smokeCall("term.policy",{shell:"powershell",command:"git status; npm run build"}).then(function(m){return m.data.level;})');
check('白名单命令被放行', policyAllow === 'allow', policyAllow);

// 4) 任务看板
await ev('window.__DSH_WORKBENCH__.open("tasks")');
await sleep(1500);
const tasks = await ev('(function(){var s=' + SR + ';var c={};["pending","in_progress","completed"].forEach(function(k){c[k]=s.querySelectorAll("[data-cards="+k+"] .wb-card").length;});return {cards:c, events:s.querySelectorAll("#wb-events .wb-ev").length, goal:s.getElementById("wb-goal-text").textContent.slice(0,40)};})()');
const cardTotal = tasks && tasks.cards ? tasks.cards.pending + tasks.cards.in_progress + tasks.cards.completed : 0;
check('任务看板读到清单/活动', cardTotal > 0 || (tasks && tasks.events > 0), tasks ? (cardTotal + ' 张卡 · ' + tasks.events + ' 条活动 · 目标：' + tasks.goal) : 'no-data');

// 5) 变更审阅：写临时文件 -> 出现在清单 -> 点开 -> 编辑 -> 应用 -> 回滚
const saved = await ev('__smokeCall("fs.save",{path:"' + SCRATCH + '",text:"alpha" + ' + LF + ' + "beta" + ' + LF + '}).then(function(m){return m.ok?"ok":m.error;})');
check('临时文件写入', saved === 'ok', saved);
await ev('window.__DSH_WORKBENCH__.open("diff")');
await sleep(2000);
const chListed = await ev('(function(){var s=' + SR + ';var items=Array.from(s.querySelectorAll("#wb-ch-list .wb-item"));return items.filter(function(i){return i.textContent.indexOf("' + SCRATCH + '") >= 0;}).length;})()');
check('变更页列出临时文件', chListed > 0, chListed + ' 条');
const openRow = await ev('(function(){var s=' + SR + ';var items=Array.from(s.querySelectorAll("#wb-ch-list .wb-item"));var t=items.find(function(i){return i.textContent.indexOf("' + SCRATCH + '") >= 0;});if(!t)return "row-missing";t.click();' + SEL('data-ch', 'edit') + '.click();return "ok";})()');
await sleep(1200);
const editorOpen = await ev('(function(){var s=' + SR + ';return s.getElementById("wb-editor").classList.contains("wb-on");})()');
check('编辑器打开', openRow === 'ok' && editorOpen === true, openRow);
const writeRes = await ev('(function(){var s=' + SR + ';var t=s.getElementById("wb-editor-text");t.value="alpha" + ' + LF + ' + "beta-changed" + ' + LF + ';s.getElementById("wb-editor-apply").click();return "applied";})()');
await sleep(1600);
const content = await ev('__smokeCall("fs.read",{path:"' + SCRATCH + '"}).then(function(m){return m.ok?m.data.text.split(String.fromCharCode(10)).join("|"):("ERR:"+m.error);})');
check('编辑器 Apply 写回磁盘', writeRes === 'applied' && String(content).indexOf('beta-changed') >= 0, content);
const reverted = await ev('__smokeCall("changes.revert",{paths:["' + SCRATCH + '"]}).then(function(m){return m.ok?("reverted="+m.data.reverted):("ERR:"+m.error);})');
await sleep(700);
const gone = await ev('__smokeCall("fs.read",{path:"' + SCRATCH + '"}).then(function(m){return m.data.kind;})');
check('回滚删除新增文件', gone === 'missing', reverted + ' · 现状：' + gone);

// 6) Git
await ev('window.__DSH_WORKBENCH__.open("git")');
await sleep(1800);
const git = await ev('(function(){var s=' + SR + ';return {branch:s.getElementById("wb-git-branch").textContent, items:s.querySelectorAll("#wb-git-list .wb-item").length};})()');
check('Git 视图', git && String(git.branch).length > 0, git ? (git.branch + ' · ' + git.items + ' 条') : 'no-data');

check('页面无未捕获异常', pageErrors.length === 0, pageErrors.slice(0, 3).join(' | '));

const failed = results.filter((r) => !r.ok);
console.log(failed.length === 0 ? '冒烟：全部通过（' + results.length + ' 项）' : '冒烟：' + failed.length + ' 项失败（共 ' + results.length + ' 项）');
ws.close();
process.exit(failed.length === 0 ? 0 : 1);
