function checkRequest(request, expectedType = 'prepare') {
  if (request.type && request.type !== expectedType) throw new Error('Unsupported chart command');
  if (!/^[A-Za-z0-9_-]{1,200}$/.test(request.workspaceId || '')) throw new Error('Invalid workspace');
  if (!Number.isFinite(request.expiresAt) || Date.now() >= request.expiresAt) throw new Error('Chart request expired');
}
function matches(url, workspaceId) {
  try {
    const parsed = new URL(url);
    return parsed.origin === 'https://openmarket.xyz' && parsed.pathname.replace(/\/$/, '') === '/chart/' + workspaceId;
  } catch { return false; }
}
function chartWorkspace(url) {
  try {
    const parsed = new URL(url);
    const path = /^\/chart\/([A-Za-z0-9_-]{1,200})\/?$/.exec(parsed.pathname);
    return parsed.origin === 'https://openmarket.xyz' && path
      && !['console', 'assets', 'fonts', 'login', 'settings'].includes(path[1]);
  } catch { return false; }
}
const tabAddress = tab => tab.pendingUrl || tab.url;

export async function prepareChart(chrome, request) {
  checkRequest(request);
  if (typeof request.id !== 'string' || !/^[A-Za-z0-9_-]{1,80}$/.test(request.id)) throw new Error('Invalid chart request id');
  const bounds = request.targetBounds;
  if (!bounds || !['left', 'top', 'width', 'height'].every(key => Number.isFinite(bounds[key]))
      || bounds.width <= 0 || bounds.height <= 0) throw new Error('Invalid target display bounds');
  const windows = (await chrome.windows.getAll({ windowTypes: ['normal'] })).filter(window => !window.incognito);
  const onTarget = window => window.left + window.width / 2 >= bounds.left
    && window.left + window.width / 2 < bounds.left + bounds.width
    && window.top + window.height / 2 >= bounds.top && window.top + window.height / 2 < bounds.top + bounds.height;
  const targetWindows = windows.filter(onTarget);
  const targetIds = new Set(targetWindows.map(window => window.id));
  const tabs = (await chrome.tabs.query({ url: 'https://openmarket.xyz/chart/*' }))
    .filter(tab => !tab.incognito && chartWorkspace(tabAddress(tab)));
  const local = tabs.filter(tab => targetIds.has(tab.windowId));
  const pool = local.length ? local : tabs;
  const saved = await chrome.storage.session.get('chartTabs');
  const recent = await chrome.windows.getLastFocused().catch(() => ({}));
  const choose = candidates => candidates.find(tab => tab.id === saved.chartTabs?.[request.workspaceId])
    || candidates.find(tab => tab.windowId === recent.id && tab.active)
    || candidates.find(tab => tab.windowId === recent.id) || candidates.find(tab => tab.active) || candidates[0];
  let tab = choose(pool.filter(tab => matches(tabAddress(tab), request.workspaceId))) || choose(pool);
  const destination = targetWindows.find(window => window.id === recent.id) || targetWindows[0];
  const position = { left: Math.round(bounds.left + 40), top: Math.round(bounds.top + 40),
    width: Math.max(320, Math.round(bounds.width - 80)), height: Math.max(240, Math.round(bounds.height - 80)) };
  let navigationFrom;
  checkRequest(request);
  if (tab) {
    tab = await chrome.tabs.get(tab.id);
    if (!chartWorkspace(tabAddress(tab))) throw new Error(tabAddress(tab)?.startsWith('https://openmarket.xyz/login')
      ? '请先在 Chrome 中登录 OpenMarket / OpenMarket login required' : 'The chart tab navigated away; retry');
    // Transfer the selected tab, never reposition its source window and the unrelated tabs inside it.
    if (!targetIds.has(tab.windowId)) {
      checkRequest(request);
      if (destination) tab = await chrome.tabs.move(tab.id, { windowId: destination.id, index: -1 });
      else {
        const created = await chrome.windows.create({ tabId: tab.id, focused: true, type: 'normal', ...position });
        if (!created) throw new Error('Chrome did not detach the chart tab');
        tab = await chrome.tabs.get(tab.id);
      }
    }
    if (!matches(tabAddress(tab), request.workspaceId)) {
      checkRequest(request);
      navigationFrom = tab.url;
      tab = await chrome.tabs.update(tab.id, { url: 'https://openmarket.xyz/chart/' + request.workspaceId, active: true });
      if (!tab) throw new Error('The chart tab closed during layout selection');
    }
  } else if (destination) {
    tab = await chrome.tabs.create({ windowId: destination.id, url: 'https://openmarket.xyz/chart/' + request.workspaceId, active: true });
  } else {
    const created = await chrome.windows.create({ url: 'https://openmarket.xyz/chart/' + request.workspaceId,
      focused: true, type: 'normal', ...position });
    tab = created?.tabs?.[0];
    if (!tab) throw new Error('Chrome did not create the chart tab');
  }
  const windowId = tab.windowId;
  await chrome.storage.session.set({ chartTabs: { ...saved.chartTabs, [request.workspaceId]: tab.id } });
  // Loading the page is distinct from the chart's live connection, which BrimDeck verifies afterwards.
  for (;;) {
    checkRequest(request);
    tab = await chrome.tabs.get(tab.id);
    if (tab.status === 'complete') {
      if (matches(tab.url, request.workspaceId)) break;
      if (!matches(tab.pendingUrl, request.workspaceId) && (navigationFrom === undefined || tab.url !== navigationFrom))
        throw new Error('请先在 Chrome 中登录 OpenMarket 并打开配置的四屏布局 / OpenMarket login required');
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  checkRequest(request);
  const nativeMarker = ` [BrimDeck-${request.id}]`;
  await chrome.scripting.executeScript({ target: { tabId: tab.id }, world: 'ISOLATED', func: updateWindowMarker,
    args: [nativeMarker, request.expiresAt, false] });
  const window = await chrome.windows.get(windowId);
  if (window.state === 'minimized') await chrome.windows.update(windowId, { state: 'normal' });
  await chrome.tabs.update(tab.id, { active: true });
  checkRequest(request);
  await chrome.windows.update(windowId, { focused: true });
  tab = await chrome.tabs.get(tab.id);
  if (!matches(tab.url, request.workspaceId)) throw new Error('The chart tab navigated away; retry');
  const singleTab = (await chrome.tabs.query({ windowId })).length === 1;
  return { protocolVersion: 4, nativeMarker, tabId: tab.id, windowId, singleTab, title: tab.title, url: tab.url, state: (await chrome.windows.get(windowId)).state };
}

export async function setChartWindowState(chrome, request) {
  checkRequest(request, 'window_state');
  if (!Number.isInteger(request.windowId) || request.windowId < 0 || !Number.isInteger(request.tabId) || request.tabId < 0
      || !['normal', 'fullscreen'].includes(request.state)) throw new Error('Invalid chart window state request');
  const tab = await chrome.tabs.get(request.tabId);
  if (tab.windowId !== request.windowId || !matches(tab.url, request.workspaceId)) throw new Error('Chart workspace/window mismatch');
  let window = await chrome.windows.get(request.windowId);
  if (window.state !== request.state) await chrome.windows.update(request.windowId, { state: request.state, focused: true });
  for (;;) {
    checkRequest(request, 'window_state');
    window = await chrome.windows.get(request.windowId);
    if (window.state === request.state) return { protocolVersion: 4, windowId: request.windowId, tabId: request.tabId, state: window.state };
    await new Promise(resolve => setTimeout(resolve, 100));
  }
}
import { updateWindowMarker } from './window-marker.mjs';


export async function releaseChartWindowMarker(chrome, request) {
  checkRequest(request, 'release_marker');
  if (typeof request.nativeMarker !== 'string' || !/^ \[BrimDeck-[A-Za-z0-9_-]{1,80}\]$/.test(request.nativeMarker)) throw new Error('Invalid chart marker');
  const tab = await chrome.tabs.get(request.tabId);
  if (tab.windowId !== request.windowId || !matches(tab.url, request.workspaceId)) throw new Error('Chart workspace/window mismatch');
  await chrome.scripting.executeScript({ target: { tabId: tab.id }, world: 'ISOLATED', func: updateWindowMarker,
    args: [request.nativeMarker, request.expiresAt, true] });
  return { protocolVersion: 4, released: true };
}
