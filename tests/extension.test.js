'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const vm=require('node:vm');
const path=require('node:path');
const crypto=require('node:crypto');
const root=path.resolve(__dirname,'..');
const extension=JSON.parse(fs.readFileSync(path.join(root,'extension/manifest.json')));
const worker=fs.readFileSync(path.join(root,'extension/worker.js'),'utf8');
function browser(){
  const listeners={};
  const event=name=>({addListener:fn=>listeners[name]=fn});
  const state={win:{id:1,focused:true,incognito:false},tab:{url:'https://private.example/path?token=a#section',incognito:false},sent:[]};
  const port={disconnect:()=>listeners.disconnect(),onDisconnect:event('disconnect'),postMessage:m=>state.sent.push(JSON.parse(JSON.stringify(m)))};
  const chrome={tabs:{query:async()=>[state.tab],onUpdated:event('update'),onActivated:event('activate')},
    windows:{getLastFocused:async()=>state.win,onFocusChanged:event('focus'),onRemoved:event('remove')},
    alarms:{onAlarm:event('alarm'),create(){}},runtime:{connectNative:()=>port,onStartup:event('startup'),onInstalled:event('install')}};
  vm.runInNewContext(worker,{chrome,URL,Date,setTimeout:fn=>{listeners.release=fn;return 1;},clearTimeout:()=>delete listeners.release});
  return {state,listeners};
}
const tick=()=>new Promise(resolve=>setImmediate(resolve));
test('stable extension ID matches only permitted native host origin',()=>{
  const hex=crypto.createHash('sha256').update(Buffer.from(extension.key,'base64')).digest('hex').slice(0,32);
  const id=[...hex].map(x=>String.fromCharCode(97+parseInt(x,16))).join('');
  const host=JSON.parse(fs.readFileSync(path.join(root,'installer/browser-host.json')));
  assert.deepEqual(host.allowed_origins,['chrome-extension://'+id+'/']);
  assert.equal(extension.incognito,'not_allowed');
});
test('only the foreground hostname enters the native message',async()=>{
  const {state,listeners}=browser();await tick();
  assert.deepEqual(state.sent,[{domain:'private.example'}]);
  state.tab.url='https://user:password@www.example.org:4242/file?secret#hash';
  await listeners.activate();assert.deepEqual(state.sent.at(-1),{domain:'www.example.org'});
  state.win.focused=false;await listeners.focus();assert.deepEqual(state.sent.at(-1),{domain:''});
});
test('private tabs, internal pages, files and absent tabs clear the domain',async()=>{
  const {state,listeners}=browser();await tick();
  for(const url of ['edge://settings/','file:///local/private.txt','about:blank','data:text/plain,x']){
    state.tab.url=url;await listeners.activate();assert.deepEqual(state.sent.at(-1),{domain:''});
  }
  state.tab={incognito:true,url:'https://private.example/'};await listeners.activate();assert.deepEqual(state.sent.at(-1),{domain:''});
  state.tab=null;await listeners.activate();assert.deepEqual(state.sent.at(-1),{domain:''});
});
test('unfocused tabs and disconnected ports do not send stale updates',async()=>{
  const {state,listeners}=browser();await tick();
  listeners.update(42,{url:'https://elsewhere.example/'},{active:false});await tick();assert.equal(state.sent.length,1);
  listeners.disconnect();await listeners.activate();assert.equal(state.sent.length,1);
});

test('unchanged tabs produce no heartbeat; an inactive host is released',async()=>{
  const {state,listeners}=browser();await tick();
  await listeners.activate();assert.equal(state.sent.length,1);
  state.win.focused=false;await listeners.focus();
  listeners.release();await listeners.alarm();await tick();
  assert.equal(state.sent.length,2);
  state.win.focused=true;await listeners.focus();assert.deepEqual(state.sent.at(-1),{domain:'private.example'});
});
