import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { updateWindowMarker } from '../../extensions/brimdeck-chart/window-marker.mjs';

function page() {
  const observers = new Set(), timers = new Map();
  let title = '1.00 ETHUSDT', queued = false, nextTimer = 0;
  const document = { head: {}, get title() { return title; }, set title(value) {
    title = value;
    if (!queued) {
      queued = true;
      queueMicrotask(() => { queued = false; for (const observer of observers) observer.callback(); });
    }
  } };
  class Observer {
    constructor(callback) { this.callback = callback; }
    observe() { observers.add(this); }
    disconnect() { observers.delete(this); }
  }
  const context = vm.createContext({ document, MutationObserver: Observer,
    setTimeout: callback => { timers.set(++nextTimer, callback); return nextTimer; }, clearTimeout: id => timers.delete(id) });
  function run(marker, clear = false) {
    context.args = [marker, Date.now() + 10000, clear];
    return vm.runInContext(`(${updateWindowMarker.toString()})(...args)`, context);
  }
  return { document, run, observers, timers };
}
const settle = async () => { for (let n = 0; n < 5; n++) await Promise.resolve(); };

test('a changing price cannot replace the marker before native window identification', async () => {
  const p = page(), marker = ' [BrimDeck-first]';
  p.run(marker); await settle();
  assert.equal(p.document.title, '1.00 ETHUSDT' + marker);
  p.document.title = '1.01 ETHUSDT'; await settle();
  assert.equal(p.document.title, '1.01 ETHUSDT' + marker);
  p.run(marker, true); await settle();
  assert.equal(p.document.title, '1.01 ETHUSDT');
  assert.equal(p.observers.size, 0);
  assert.equal(p.timers.size, 0);
});
test('an old request cannot clear a newer window marker', async () => {
  const p = page();
  p.run(' [BrimDeck-first]'); await settle();
  p.run(' [BrimDeck-second]'); await settle();
  p.run(' [BrimDeck-first]', true); await settle();
  assert.equal(p.document.title, '1.00 ETHUSDT [BrimDeck-second]');
  p.run(' [BrimDeck-second]', true); await settle();
  assert.equal(p.document.title, '1.00 ETHUSDT');
});
test('the temporary marker expires even when the native request is interrupted', async () => {
  const p = page();
  p.run(' [BrimDeck-timeout]'); await settle();
  p.document.title = '1.02 ETHUSDT'; await settle();
  [...p.timers.values()][0](); await settle();
  assert.equal(p.document.title, '1.02 ETHUSDT');
  assert.equal(p.observers.size, 0);
});
