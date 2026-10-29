// hooks/compile-cache.mjs
// Preloaded into the DSH service process only, via `node --import <this file>`.
//
// WHY: every `dsh web` boot recompiles the same few thousand modules. Node can keep
// V8 bytecode on disk (the "compile cache"), but it only flushes that cache on a
// NORMAL process exit -- and the panel stops the service by terminating the process
// tree (Job Object), which skips every exit hook. So this hook enables the cache and
// flushes it on a timer: a force-killed service still leaves a warm cache behind for
// the next boot.
//
// SAFETY: this file must never be able to keep the service from booting. Every step
// is guarded, failures are reported on stderr only, and the launcher probes the hook
// before using it (and can be told to skip it entirely with `compilecache=0` in
// dsh-web.config). The cache directory lives under run/ and can be deleted at any
// time; Node treats a corrupt/missing entry as a cache miss.
//
// Environment:
//   DSH_COMPILE_CACHE_DIR      cache directory (required; nothing happens without it)
//   DSH_COMPILE_CACHE_FLUSH_MS flush interval, default 20000, minimum 1000

const FLUSH_MS_DEFAULT = 20000;
const FLUSH_MS_MIN = 1000;
const TAG = '[dsh-web]';

function note(message) {
  try {
    process.stderr.write(`${TAG} compile cache: ${message}\n`);
  } catch {
    /* stderr unavailable - never fatal */
  }
}

const directory = process.env.DSH_COMPILE_CACHE_DIR;
if (directory && directory.trim().length > 0) {
  try {
    const nodeModule = await import('node:module');
    const enable = nodeModule.enableCompileCache;
    if (typeof enable !== 'function') {
      // Node < 22.1: no compile cache. Silent by design, not an error.
    } else {
      const result = enable.call(nodeModule, directory);
      if (result && result.status === 0) {
        note(`disabled (${result.message ?? 'unknown reason'})`);
      } else if (typeof nodeModule.flushCompileCache === 'function') {
        const flush = nodeModule.flushCompileCache;
        let warned = false;
        const flushOnce = () => {
          try {
            flush.call(nodeModule);
          } catch (error) {
            if (!warned) {
              warned = true;
              note(`flush failed: ${error && error.message ? error.message : String(error)}`);
            }
          }
        };
        const parsed = Number.parseInt(process.env.DSH_COMPILE_CACHE_FLUSH_MS ?? '', 10);
        const interval = Number.isFinite(parsed) && parsed > 0 ? Math.max(parsed, FLUSH_MS_MIN) : FLUSH_MS_DEFAULT;
        const timer = setInterval(flushOnce, interval);
        if (typeof timer.unref === 'function') timer.unref();
        process.once('exit', flushOnce);
      }
    }
  } catch (error) {
    note(`hook skipped: ${error && error.message ? error.message : String(error)}`);
  }
}
