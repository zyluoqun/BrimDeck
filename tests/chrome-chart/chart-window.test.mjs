import { test } from 'node:test';
import assert from 'node:assert/strict';
import { prepareChart, setChartWindowState, releaseChartWindowMarker } from '../../extensions/brimdeck-chart/chart-window.mjs';

function browser(shared = false, present = true, options = {}) {
  const calls = [];
  const states = new Map();
  let tab = options.tab || { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', title: 'BTCUSDT', status: 'complete' };
  const catalog = new Map((options.catalog || [tab]).map(t => [t.id, t]));
  const windows = options.windows || (present ? [{ id: 3, left: 0, top: 0, width: 1200, height: 900 }] : []);
  if (shared) catalog.set(8, { id: 8, windowId: 3, url: 'https://example.com/', status: 'complete' });
  let nextId = 100;
  let chartTabs = { layout123: options.savedTabId };
  return { calls, tabs: {
    query: async q => q.url ? (present ? [...catalog.values()].filter(t => (t.pendingUrl || t.url).startsWith('https://openmarket.xyz/chart/')) : [])
      : [...catalog.values()].filter(t => t.windowId === q.windowId),
    get: async id => { if (!catalog.has(id)) throw new Error('Tab closed'); return catalog.get(id); },
    update: async (id, change) => { calls.push(['tab', id, change]); const updated = { ...catalog.get(id), ...change }; catalog.set(id, updated); return updated; },
    move: async (id, change) => { calls.push(['move', id, change]); const updated = { ...catalog.get(id), windowId: change.windowId }; catalog.set(id, updated); return updated; },
    create: async change => { calls.push(['new-tab', change]); const created = { id: nextId++, ...change, status: 'complete' }; catalog.set(created.id, created); return created; },
    remove: async ids => { calls.push(['remove', ids]); for (const id of Array.isArray(ids) ? ids : [ids]) catalog.delete(id); },
    reload: async id => { calls.push(['reload', id]); }
  }, windows: {
    get: async id => ({ ...windows.find(w => w.id === id), id, state: states.get(id) || 'normal' }),
    getAll: async () => windows,
    getLastFocused: async () => ({ id: options.focusedWindowId ?? 3 }),
    create: async change => { calls.push(['create', change]); tab = change.tabId ? { ...catalog.get(change.tabId), windowId: 9 }
      : { id: nextId++, url: change.url, windowId: 9, status: 'complete' }; catalog.set(tab.id, tab); windows.push({ id: 9, ...change }); return { id: 9, tabs: [tab] }; },
    update: async (id, change) => { calls.push(['window', id, change]); if (change.state) states.set(id, change.state); return { id, ...change }; }
  }, storage: { session: { get: async () => ({ chartTabs }), set: async value => { chartTabs = value.chartTabs; } } },
  scripting: { executeScript: async command => { calls.push(['script', command]); return [{ result: true }]; } } };
}
const request = () => ({ id: 'one', workspaceId: 'layout123', expiresAt: Date.now() + 15000,
  targetBounds: { left: 0, top: 0, width: 1920, height: 1080 } });

test('repeated countdown and capsule operations keep one tab per workspace across three layouts', async () => {
  const chrome = browser(false, true, { catalog: [
    { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/pair', active: true, status: 'complete' },
    { id: 8, windowId: 3, url: 'https://openmarket.xyz/chart/four', status: 'complete' },
    { id: 9, windowId: 4, url: 'https://openmarket.xyz/chart/xiaoluo', status: 'complete' }],
    windows: [{ id: 3, left: 2000, top: 0, width: 1200, height: 900 }, { id: 4, left: 0, top: 0, width: 1200, height: 900 }] });
  for (let n = 0; n < 4; n++) {
    assert.equal((await prepareChart(chrome, { ...request(), workspaceId: 'pair' })).tabId, 7);
    assert.equal((await prepareChart(chrome, { ...request(), workspaceId: 'four' })).tabId, 8);
  }
  assert.equal((await chrome.tabs.get(9)).url, 'https://openmarket.xyz/chart/xiaoluo');
  assert.equal(chrome.calls.filter(c => c[0] === 'create' || c[0] === 'new-tab' || c[0] === 'remove' || c[0] === 'tab' && c[2].url).length, 0);
});

test('preparation consolidates only duplicate tabs for the requested workspace', async () => {
  const chrome = browser(false, true, { catalog: [
    { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', active: true, status: 'complete' },
    { id: 8, windowId: 4, url: 'https://openmarket.xyz/chart/layout123', status: 'complete' },
    { id: 9, windowId: 3, url: 'https://openmarket.xyz/chart/other', status: 'complete' }],
    windows: [{ id: 3, left: 0, top: 0, width: 1200, height: 900 }, { id: 4, left: 2000, top: 0, width: 1200, height: 900 }] });
  const result = await prepareChart(chrome, request());
  assert.equal(result.tabId, 7);
  assert.equal(result.closedDuplicates, 1);
  assert.equal(chrome.calls.filter(c => c[0] === 'reload').length, 1);
  await assert.rejects(chrome.tabs.get(8), /closed/);
  assert.equal((await chrome.tabs.get(9)).url, 'https://openmarket.xyz/chart/other');
  await prepareChart(chrome, request());
  assert.equal(chrome.calls.filter(c => c[0] === 'remove').length, 1);
  assert.equal(chrome.calls.filter(c => c[0] === 'reload').length, 1);
});

test('duplicate cleanup preserves a tab that the user navigated to another layout', async () => {
  const chrome = browser(false, true, { catalog: [
    { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', status: 'complete' },
    { id: 8, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', status: 'complete' }] });
  const get = chrome.tabs.get;
  chrome.tabs.get = async id => id === 8 ? { ...await get(id), pendingUrl: 'https://openmarket.xyz/chart/other' } : get(id);
  await prepareChart(chrome, request());
  assert.ok(!chrome.calls.some(c => c[0] === 'remove' || c[0] === 'reload'));
});

test('a keeper redirected to login cannot close another tab', async () => {
  const chrome = browser(false, true, { catalog: [
    { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', status: 'complete' },
    { id: 8, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', status: 'complete' }] });
  const get = chrome.tabs.get;
  let reads = 0;
  chrome.tabs.get = async id => id === 7 && ++reads > 1 ? { ...await get(id), url: 'https://openmarket.xyz/login' } : get(id);
  await assert.rejects(prepareChart(chrome, request()), /登录|login/i);
  assert.ok(!chrome.calls.some(c => c[0] === 'remove' || c[0] === 'reload'));
});

test('the chart focus notification follows selection of the target tab and window', async () => {
  const chrome = browser();
  await prepareChart(chrome, request());
  const notify = chrome.calls.findIndex(c => c[0] === 'script' && c[1].func.name === 'notifyChartFocus');
  const activate = chrome.calls.findIndex(c => c[0] === 'tab' && c[2].active);
  const focus = chrome.calls.findIndex(c => c[0] === 'window' && c[2].focused);
  assert.ok(notify > activate && notify > focus);
  assert.deepEqual(chrome.calls[notify][1].target, { tabId: 7 });
});

test('moves only the chart tab into an existing Chrome window on display 2', async () => {
  const chrome = browser(true, true, { windows: [{ id: 3, left: 2000, top: 0, width: 1200, height: 900 },
    { id: 4, left: 0, top: 0, width: 1200, height: 900 }] });
  const result = await prepareChart(chrome, request());
  assert.equal(result.windowId, 4);
  assert.equal(result.singleTab, true);
  assert.equal((await chrome.tabs.get(8)).windowId, 3);
  assert.deepEqual(chrome.calls.find(c => c[0] === 'move'), ['move', 7, { windowId: 4, index: -1 }]);
  assert.ok(!chrome.calls.some(c => c[0] === 'window' && c[1] === 3));
});
test('detaches only the chart tab when display 2 has no Chrome window', async () => {
  const chrome = browser(true, true, { windows: [{ id: 3, left: 2000, top: 0, width: 1200, height: 900 }] });
  const result = await prepareChart(chrome, request());
  assert.equal(result.windowId, 9);
  assert.equal((await chrome.tabs.get(8)).windowId, 3);
  const created = chrome.calls.find(c => c[0] === 'create')[1];
  assert.equal(created.tabId, 7); assert.equal(created.url, undefined);
});
test('moves the existing exact layout to display 2 instead of replacing another layout', async () => {
  const chrome = browser(false, true, { catalog: [
    { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', status: 'complete' },
    { id: 8, windowId: 4, url: 'https://openmarket.xyz/chart/other', status: 'complete' }],
    windows: [{ id: 3, left: 2000, top: 0, width: 1200, height: 900 }, { id: 4, left: 0, top: 0, width: 1200, height: 900 }] });
  assert.equal((await prepareChart(chrome, request())).tabId, 7);
  assert.equal(chrome.calls.filter(c => c[0] === 'move').length, 1);
  assert.ok(!chrome.calls.some(c => c[0] === 'create' || c[0] === 'new-tab' || c[0] === 'tab' && c[2].url));
  assert.equal((await chrome.tabs.get(8)).url, 'https://openmarket.xyz/chart/other');
});
test('opens a new tab in display 2 Chrome when no chart exists', async () => {
  const chrome = browser(false, false, { windows: [{ id: 4, left: 0, top: 0, width: 1200, height: 900 }] });
  assert.equal((await prepareChart(chrome, request())).windowId, 4);
  assert.ok(chrome.calls.some(c => c[0] === 'new-tab'));
  assert.ok(!chrome.calls.some(c => c[0] === 'create'));
});
test('invalid target bounds cannot move tabs or focus windows', async () => {
  const chrome = browser();
  await assert.rejects(prepareChart(chrome, { ...request(), targetBounds: { left: 0, top: 0, width: -1, height: 1080 } }));
  assert.equal(chrome.calls.length, 0);
});

test('reuses the configured workspace and activates its existing dedicated window', async () => {
  const chrome = browser();
  const result = await prepareChart(chrome, request());
  assert.equal(result.title, 'BTCUSDT');
  assert.equal(result.tabId, 7);
  assert.equal(chrome.calls.filter(c => c[0] === 'create').length, 0);
  assert.ok(chrome.calls.some(c => c[0] === 'tab' && c[1] === 7 && c[2].active));
  assert.ok(chrome.calls.some(c => c[0] === 'window' && c[1] === 3 && c[2].focused));
});
test('an existing chart stays in its original window even when it has other tabs', async () => {
  const chrome = browser(true);
  const result = await prepareChart(chrome, request());
  assert.equal(result.windowId, 3);
  assert.equal(result.singleTab, false);
  assert.ok(!chrome.calls.some(c => c[0] === 'create'));
  assert.ok(chrome.calls.some(c => c[0] === 'window' && c[1] === 3 && c[2].focused));
});
test('opens the saved workspace in one dedicated window when absent', async () => {
  const chrome = browser(false, false);
  await prepareChart(chrome, request());
  assert.equal(chrome.calls.filter(c => c[0] === 'create').length, 1);
  assert.equal(chrome.calls.find(c => c[0] === 'create')[1].url, 'https://openmarket.xyz/chart/layout123');
});
test('expired requests cannot open or move a browser window', async () => {
  const chrome = browser();
  await assert.rejects(prepareChart(chrome, { ...request(), expiresAt: Date.now() - 1 }));
  assert.equal(chrome.calls.length, 0);
});
test('a redirected login page is not reported as the requested chart', async () => {
  const chrome = browser();
  chrome.tabs.get = async () => ({ id: 7, windowId: 3, url: 'https://openmarket.xyz/login', status: 'complete' });
  await assert.rejects(prepareChart(chrome, request()), /登录|login/i);
});
test('rejects invalid workspace ids before touching Chrome', async () => {
  const chrome = browser();
  await assert.rejects(prepareChart(chrome, { ...request(), workspaceId: '../../settings' }));
  assert.equal(chrome.calls.length, 0);
});
test('restores a minimized chart window before asking it to receive focus', async () => {
  const chrome = browser();
  chrome.windows.get = async id => ({ id, state: 'minimized' });
  await prepareChart(chrome, request());
  const restore = chrome.calls.findIndex(c => c[0] === 'window' && c[2].state === 'normal');
  const focus = chrome.calls.findIndex(c => c[0] === 'window' && c[2].focused);
  assert.ok(restore >= 0 && focus > restore);
});
test('waits for a newly loading chart tab instead of immediately reporting success', async () => {
  const chrome = browser(); let reads = 0;
  chrome.tabs.get = async () => ({ id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', title: 'ETHUSDT', status: ++reads === 1 ? 'loading' : 'complete' });
  await prepareChart(chrome, request());
  assert.ok(reads >= 3);
});
test('enters browser fullscreen after placement, without sending a toggle key', async () => {
  const chrome = browser();
  const reply = await setChartWindowState(chrome, { ...request(), type: 'window_state', windowId: 3, tabId: 7, state: 'fullscreen' });
  assert.equal(reply.state, 'fullscreen');
  assert.deepEqual(chrome.calls.find(c => c[0] === 'window')[2], { state: 'fullscreen', focused: true });
  await setChartWindowState(chrome, { ...request(), type: 'window_state', windowId: 3, tabId: 7, state: 'fullscreen' });
  assert.equal(chrome.calls.filter(c => c[0] === 'window').length, 1, 'already-fullscreen windows must stay fullscreen without toggling');
});
test('fullscreen state changes are limited to the prepared workspace tab and window', async () => {
  const chrome = browser();
  await assert.rejects(setChartWindowState(chrome, { ...request(), type: 'window_state', windowId: 99, tabId: 7, state: 'fullscreen' }));
  assert.equal(chrome.calls.length, 0);
});
test('rejects unsupported window states without modifying Chrome', async () => {
  const chrome = browser();
  await assert.rejects(setChartWindowState(chrome, { ...request(), type: 'window_state', windowId: 3, tabId: 7, state: 'minimized' }));
  assert.equal(chrome.calls.length, 0);
});
test('preparing an already-fullscreen chart does not take it out of fullscreen', async () => {
  const chrome = browser();
  chrome.windows.get = async id => ({ id, state: 'fullscreen' });
  const prepared = await prepareChart(chrome, request());
  assert.equal(prepared.protocolVersion, 4);
  assert.equal(prepared.state, 'fullscreen');
  assert.ok(!chrome.calls.some(c => c[0] === 'window' && c[2].state === 'normal'));
});
test('each preparation identifies its actual tab with a unique native window marker', async () => {
  const chrome = browser();
  const prepared = await prepareChart(chrome, request());
  const command = chrome.calls.find(c => c[0] === 'script')[1];
  assert.equal(prepared.nativeMarker, ' [BrimDeck-one]');
  assert.deepEqual(command.target, { tabId: prepared.tabId });
  assert.equal(command.world, 'ISOLATED');
  assert.equal(command.args[0], prepared.nativeMarker);
  const next = await prepareChart(chrome, { ...request(), id: 'two' });
  assert.notEqual(next.nativeMarker, prepared.nativeMarker);
});
test('cleanup is limited to the originally prepared tab and marker', async () => {
  const chrome = browser();
  await releaseChartWindowMarker(chrome, { ...request(), type: 'release_marker', tabId: 7, windowId: 3, nativeMarker: ' [BrimDeck-one]' });
  const command = chrome.calls.find(c => c[0] === 'script')[1];
  assert.deepEqual(command.args, [' [BrimDeck-one]', command.args[1], true]);
  await assert.rejects(releaseChartWindowMarker(chrome, { ...request(), type: 'release_marker', tabId: 7, windowId: 99, nativeMarker: ' [BrimDeck-one]' }));
  assert.equal(chrome.calls.filter(c => c[0] === 'script').length, 1);
});
test('a remembered tab switched to another layout is preserved and the missing workspace opens once', async () => {
  const chrome = browser(false, true, { savedTabId: 7, tab: { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/xiaoluo', title: 'ETHUSDT', status: 'complete' } });
  const prepared = await prepareChart(chrome, request());
  assert.equal(prepared.tabId, 100); assert.equal(prepared.windowId, 3);
  assert.equal((await chrome.tabs.get(7)).url, 'https://openmarket.xyz/chart/xiaoluo');
  assert.equal((await prepareChart(chrome, request())).tabId, 100);
  assert.equal(chrome.calls.filter(c => c[0] === 'new-tab').length, 1);
  assert.ok(!chrome.calls.some(c => c[0] === 'create'));
});
test('additional layouts do not change the saved target or replace other tabs', async () => {
  const layouts = Array.from({ length: 20 }, (_, i) => ({ id: i + 7, windowId: 3, url: `https://openmarket.xyz/chart/other-${i}`, title: 'ETHUSDT', status: 'complete' }));
  const chrome = browser(false, true, { catalog: layouts, savedTabId: 12 });
  const prepared = await prepareChart(chrome, request());
  assert.equal(prepared.tabId, 100); assert.equal(prepared.url, 'https://openmarket.xyz/chart/layout123');
  assert.equal(chrome.calls.filter(c => c[0] === 'new-tab').length, 1);
  assert.equal(chrome.calls.filter(c => c[0] === 'tab' && c[2].url).length, 0);
  assert.ok(!chrome.calls.some(c => c[0] === 'create'));
  assert.equal((await chrome.tabs.get(7)).url, layouts[0].url);
});
test('an already-open configured workspace is preferred over a remembered different layout', async () => {
  const other = { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/xiaoluo', title: 'ETHUSDT', status: 'complete' };
  const exact = { id: 8, windowId: 3, url: 'https://openmarket.xyz/chart/layout123', title: 'BTCUSDT', status: 'complete' };
  const chrome = browser(false, true, { catalog: [other, exact], savedTabId: 7 });
  assert.equal((await prepareChart(chrome, request())).tabId, 8);
  assert.ok(!chrome.calls.some(c => c[0] === 'create' || c[0] === 'tab' && c[2].url));
});
test('after browser restart the missing workspace opens in the target window without replacing another chart', async () => {
  const first = { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/first', title: 'ETHUSDT', status: 'complete' };
  const recent = { id: 8, windowId: 4, url: 'https://openmarket.xyz/chart/recent', title: 'ETHUSDT', status: 'complete' };
  const chrome = browser(false, true, { catalog: [first, recent], focusedWindowId: 4,
    windows: [{ id: 3, left: 0, top: 0, width: 800, height: 900 }, { id: 4, left: 800, top: 0, width: 800, height: 900 }] });
  assert.equal((await prepareChart(chrome, request())).tabId, 100);
  assert.equal((await chrome.tabs.get(8)).url, recent.url);
  assert.equal(chrome.calls.find(c => c[0] === 'new-tab')[1].windowId, 4);
  assert.ok(!chrome.calls.some(c => c[0] === 'create'));
});
test('account console tabs are not repurposed as chart layouts', async () => {
  const chrome = browser(false, true, { tab: { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/console/agents', title: 'Agent console', status: 'complete' } });
  await prepareChart(chrome, request());
  assert.equal(chrome.calls.filter(c => c[0] === 'new-tab').length, 1);
  assert.ok(!chrome.calls.some(c => c[0] === 'tab' && c[2].url));
});
test('a pending workspace waits until its new URL has loaded before marking the window', async () => {
  const original = { id: 7, windowId: 3, url: 'https://openmarket.xyz/chart/xiaoluo', title: 'ETHUSDT', status: 'complete' };
  const chrome = browser(false, true, { tab: { ...original, pendingUrl: 'https://openmarket.xyz/chart/layout123', status: 'loading' }, savedTabId: 7 });
  const get = chrome.tabs.get; let reads = 0;
  chrome.tabs.get = async id => {
    reads++;
    if (reads < 3) return { ...original, pendingUrl: 'https://openmarket.xyz/chart/layout123', status: 'loading' };
    return { ...await get(id), url: 'https://openmarket.xyz/chart/layout123', pendingUrl: undefined, status: 'complete' };
  };
  chrome.scripting.executeScript = async command => {
    assert.ok(reads >= 3, 'native marker must not be installed on the old layout');
    chrome.calls.push(['script', command]); return [{ result: true }];
  };
  assert.equal((await prepareChart(chrome, request())).url, 'https://openmarket.xyz/chart/layout123');
  assert.ok(!chrome.calls.some(c => c[0] === 'create'));
});
test('a remembered tab repurposed for another website is left alone', async () => {
  const otherSite = { id: 7, windowId: 3, url: 'https://example.com/', title: 'Other site', status: 'complete' };
  const chart = { id: 8, windowId: 4, url: 'https://openmarket.xyz/chart/another', title: 'ETHUSDT', status: 'complete' };
  const chrome = browser(false, true, { catalog: [otherSite, chart], savedTabId: 7, focusedWindowId: 4 });
  assert.equal((await prepareChart(chrome, request())).tabId, 100);
  assert.equal((await chrome.tabs.get(8)).url, chart.url);
  assert.equal((await chrome.tabs.get(7)).url, 'https://example.com/');
  assert.ok(!chrome.calls.some(c => c[0] === 'create'));
});
