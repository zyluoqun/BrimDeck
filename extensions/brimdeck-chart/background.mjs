import { prepareChart, setChartWindowState, releaseChartWindowMarker } from './chart-window.mjs';

const host = 'com.brimdeck.chart';
let port;
let work = Promise.resolve();
function connect() {
  if (port) return;
  const connection = chrome.runtime.connectNative(host);
  port = connection;
  connection.onMessage.addListener(request => {
    work = work.catch(() => {}).then(async () => {
      try {
        const result = await (request.type === 'window_state' ? setChartWindowState(chrome, request)
          : request.type === 'release_marker' ? releaseChartWindowMarker(chrome, request) : prepareChart(chrome, request));
        connection.postMessage({ id: request.id, ok: true, ...result });
      } catch (error) {
        try { connection.postMessage({ id: request.id, ok: false, error: String(error.message || error) }); } catch { }
      }
    });
  });
  connection.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    if (port === connection) port = null;
    setTimeout(connect, 1500);
  });
}
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'reconnect') connect(); });
chrome.alarms.create('reconnect', { periodInMinutes: 0.5 });
connect();
