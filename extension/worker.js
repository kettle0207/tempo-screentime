// Only a hostname crosses the native port. No browsing history or content scripts.
let port, revision = 0, retryAfter = 0, lastDomain, releaseTimer;
async function synchronize() {
  const version = ++revision;
  let domain = '', activeWindow = false;
  try {
    const win = await chrome.windows.getLastFocused({windowTypes: ['normal', 'popup']});
    activeWindow = win.focused && !win.incognito;
    if (activeWindow) {
      const [tab] = await chrome.tabs.query({active: true, windowId: win.id});
      if (tab && !tab.incognito && tab.url) {
        const url = new URL(tab.url);
        if (['http:', 'https:'].includes(url.protocol)) domain = url.hostname;
      }
    }
  } catch { /* Closing or private windows have no eligible domain. */ }
  if (version !== revision) return;
  if (domain && releaseTimer) { clearTimeout(releaseTimer); releaseTimer = undefined; }
  if (!domain && !port) return;
  if (!domain && port && !releaseTimer) {
    releaseTimer = setTimeout(() => {
      releaseTimer = undefined;
      const channel = port;
      port = undefined;
      lastDomain = undefined;
      channel?.disconnect();
    }, 30000);
  }
  if (port && lastDomain === domain) return;
  if (!port && Date.now() < retryAfter) return;
  if (!port) {
    const channel = chrome.runtime.connectNative('local.tempo.screentime');
    port = channel;
    channel.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      if (port === channel) { port = undefined; lastDomain = undefined; retryAfter = Date.now() + 30000; }
    });
  }
  try { port.postMessage({domain}); lastDomain = domain; } catch { port = undefined; }
}
chrome.tabs.onActivated.addListener(synchronize);
chrome.tabs.onUpdated.addListener((_id, change, tab) => {
  if (tab.active && ('url' in change || change.status === 'complete')) synchronize();
});
chrome.windows.onFocusChanged.addListener(synchronize);
chrome.windows.onRemoved.addListener(synchronize);
chrome.runtime.onStartup.addListener(synchronize);
chrome.runtime.onInstalled.addListener(synchronize);
chrome.alarms.onAlarm.addListener(() => { if (!port) synchronize(); });
chrome.alarms.create('reconnect', {periodInMinutes: 1});
synchronize();
