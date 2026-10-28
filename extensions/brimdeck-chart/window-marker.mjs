// Runs in this extension's isolated world. No chart data, settings or account state is changed.
export function updateWindowMarker(marker, expiresAt, clear = false) {
  const key = '__brimdeckChartWindowMarker';
  const previous = globalThis[key];
  if (clear) {
    if (previous?.marker !== marker) return false;
    previous.release(); return true;
  }
  previous?.release();
  let active = true, timer;
  const observer = new MutationObserver(() => {
    if (!active) return;
    const clean = document.title.split(marker).join('');
    if (document.title !== clean + marker) document.title = clean + marker;
  });
  const state = { marker, release() {
    if (!active) return;
    active = false; observer.disconnect(); clearTimeout(timer);
    document.title = document.title.split(marker).join('');
    if (globalThis[key] === state) delete globalThis[key];
  } };
  globalThis[key] = state;
  observer.observe(document.head || document.documentElement, { subtree: true, childList: true, characterData: true });
  document.title += marker;
  timer = setTimeout(state.release, Math.max(1, Math.min(10000, expiresAt - Date.now())));
  return true;
}
