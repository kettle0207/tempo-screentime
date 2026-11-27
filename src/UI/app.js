(() => {
'use strict';
const $ = id => document.getElementById(id);
const categories = {dev:'開発',work:'仕事',study:'勉強',games:'ゲーム',social:'SNS',video:'動画',web:'ブラウジング',other:'その他'};
const order = Object.keys(categories);
const pages = {overview:'スクリーンタイム',apps:'アプリ',sites:'Webサイト',settings:'設定'};
const prior = {day:'前日',week:'前週',month:'前月',year:'前年'};
const dateKey = d => [d.getFullYear(), String(d.getMonth()+1).padStart(2,'0'), String(d.getDate()).padStart(2,'0')].join('-');
const parseDate = s => new Date(s+'T12:00:00');
const today = () => dateKey(new Date());
const escape = s => String(s ?? '').replace(/[&<>"']/g,c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const icon = id => `<svg aria-hidden="true"><use href="#${id}"/></svg>`;
const color = c => 'var(--'+(Object.hasOwn(categories,c)?c:'other')+')';
const dot = c => `<i class="dot" style="background:${color(c)}"></i>`;
let page='overview', period='day', date=today(), followToday=true, query='', visibleRows=50, report=null, prefs=null, revision=0, seq=0, editing=null, update=null;
const calls = new Map(), host=window.chrome?.webview;

function bridge(action, args={}, wait=15000) {
  return new Promise((resolve,reject) => {
    if (!host) { reject(new Error('アプリとの接続を確認できません。')); return; }
    const request=String(++seq);
    const timer=setTimeout(()=>{calls.delete(request);reject(new Error('応答に時間がかかっています。'));},wait);
    calls.set(request,m=>{clearTimeout(timer);m.error?reject(new Error(m.error)):resolve(m);});
    host.postMessage({action,request,...args});
  });
}
host?.addEventListener('message',e=>{
  const m=e.data;
  if(m.changed){refresh();return;}
  if(m.update){update=m.update;renderUpdate();return;}
  const fn=calls.get(m.request);
  if(fn){calls.delete(m.request);fn(m);}
});
function notice(message='',error=false){$('notice').textContent=message;$('notice').hidden=!message;$('notice').classList.toggle('error',error);}

function parts(ms){
  if(ms>0 && ms<60000) return [[Math.max(1,Math.floor(ms/1000)),'秒']];
  const m=Math.floor(ms/60000),h=Math.floor(m/60),out=[];
  if(h)out.push([h,'時間']);
  if(!h || m%60)out.push([m%60,'分']);
  return out;
}
const duration = ms => parts(ms).map(([n,u])=>n+u).join(' ');
function displayDate(s,opts={month:'long',day:'numeric'}){return parseDate(s).toLocaleDateString('ja-JP',opts);}
function dayOffset(s,n){const d=parseDate(s);d.setDate(d.getDate()+n);return dateKey(d);}
function daysBetween(a,b){return Math.round((parseDate(b)-parseDate(a))/86400000);}
function rangeLabel(){
  if(period==='day') return date===today()?'今日':date===dayOffset(today(),-1)?'昨日':displayDate(date,{year:'numeric',month:'long',day:'numeric',weekday:'short'});
  if(period==='week') return displayDate(report.start,{month:'long',day:'numeric'})+' – '+displayDate(dayOffset(report.end,-1),{month:'long',day:'numeric'});
  return displayDate(report.start,period==='year'?{year:'numeric'}:{year:'numeric',month:'long'});
}
function updateHeader(){
  $('title').textContent=pages[page];
  document.querySelectorAll('[data-page]').forEach(b=>{if(b.dataset.page===page)b.setAttribute('aria-current','page');else b.removeAttribute('aria-current');});
  $('controls').hidden=page==='settings';
  document.querySelectorAll('[data-period]').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.period===period)));
  $('date').value=date;$('date').max=today();$('date').min='1971-01-01';
  if(report){$('date-label').textContent=rangeLabel();$('next').disabled=report.end>today();$('back').disabled=report.start<='1971-01-01';$('today').hidden=date===today();}
}
const systemDark=matchMedia('(prefers-color-scheme:dark)');
function theme(){const t=prefs?.theme||'system';document.documentElement.dataset.theme=t==='system'?(systemDark.matches?'dark':'light'):t;}
systemDark.addEventListener('change',theme);
function status(){
  theme();const paused=prefs.paused,failed=!!prefs.error,r=document.querySelector('.record');
  $('record-text').textContent=failed?'記録エラー':paused?'一時停止中':'記録中';
  r.title=$('record-text').textContent;r.classList.toggle('off',paused&&!failed);r.classList.toggle('error',failed);
}
async function refresh(animate=false){
  const rev=++revision;
  if(followToday)date=today();
  try{
    const m=await bridge('report',{period,date});
    if(rev!==revision)return;
    report=m.report;prefs=m.settings;status();
    if(prefs.error)notice(prefs.error,true);else if(prefs.message)notice(prefs.message);else if($('notice').classList.contains('error'))notice('');
    updateHeader();render(animate);$('content').setAttribute('aria-busy','false');
  }catch(err){if(rev===revision){notice('履歴を読み込めません。'+err.message,true);$('content').setAttribute('aria-busy','false');}}
}
function go(next){
  if(!Object.hasOwn(pages,next))return;
  page=next;query='';visibleRows=50;updateHeader();if(report)render(true);window.scrollTo(0,0);
}
function changeDate(value){
  if(!/^\d{4}-\d\d-\d\d$/.test(value)||value>today()||value<'1971-01-01')return;
  date=value;followToday=date===today();refresh(true);
}
function shift(dir){
  if(!report)return;
  if(dir>0)changeDate(report.end>today()?today():report.end);
  else changeDate(dayOffset(report.start,-1));
}
$('back').onclick=()=>shift(-1);$('next').onclick=()=>shift(1);$('today').onclick=()=>changeDate(today());
$('date').onchange=e=>changeDate(e.target.value);
document.querySelectorAll('[data-page]').forEach(b=>b.onclick=()=>go(b.dataset.page));
document.querySelectorAll('[data-period]').forEach(b=>b.onclick=()=>{period=b.dataset.period;refresh(true);});

function totals(){
  const all={};for(const bin of report.buckets)for(const [k,v] of Object.entries(bin))all[k]=(all[k]||0)+v;
  return Object.entries(all).sort((a,b)=>b[1]-a[1]);
}
// Days of the period that have started, used for the daily average.
function elapsedDays(){
  const end=report.end>today()?dayOffset(today(),1):report.end;
  return Math.max(1,daysBetween(report.start,end));
}
function summary(){
  const big=parts(report.total).map(([n,u])=>`<strong>${n}</strong><small>${u}</small>`).join('');
  const label=period==='day'?(date===today()?'今日の使用時間':'使用時間'):'合計使用時間';
  const lines=[];
  if(report.previous>0){
    const diff=report.total-report.previous,pct=Math.round(100*Math.abs(diff)/report.previous);
    if(diff===0)lines.push(`<span>${prior[period]}と同じ</span>`);
    else lines.push(`<span>${icon(diff>0?'up':'down')}${prior[period]}より ${duration(Math.abs(diff))}（${pct}%）${diff>0?'多い':'少ない'}</span>`);
  } else if(report.total>0) lines.push(`<span>${prior[period]}の記録はありません</span>`);
  if(period!=='day' && report.total>0)lines.push(`<span>1日平均 ${duration(report.total/elapsedDays())}</span>`);
  if(!report.total)lines.push(`<span>${report.first?'この期間の記録はありません':'記録を開始しました。PC を使うと、ここに使用時間が表示されます'}</span>`);
  return `<section class="summary" aria-label="合計"><div class="label">${label}</div><div class="total" id="total">${big}</div><div class="compare">${lines.join('')}</div></section>`;
}
function binName(i,short){
  if(period==='day')return short?i+'時':i+':00 – '+(i+1)+':00';
  if(period==='week'){const d=dayOffset(report.start,i);return displayDate(d,short?{weekday:'short'}:{month:'numeric',day:'numeric',weekday:'short'});}
  if(period==='month')return short?String(i+1):displayDate(dayOffset(report.start,i),{month:'numeric',day:'numeric',weekday:'short'});
  return (i+1)+'月';
}
function chart(cats){
  const sums=report.buckets.map(b=>Object.values(b).reduce((n,v)=>n+v,0));
  const max=Math.max(0,...sums)/60000;
  const ladder=[1,2,5,10,15,20,30,60,120,180,240,360,480,720,960,1440,2880,4320,5760,10080,20160,30240,44640];
  const ceiling=max?(ladder.find(n=>n>=max)||Math.ceil(max)):(period==='day'?60:period==='year'?1440*7:240);
  const scale=n=>n>=60?(+(n/60).toFixed(1))+'時間':n+'分';
  const clickable=period==='week'||period==='month';
  const cols=report.buckets.map((bin,i)=>{
    const segs=Object.entries(bin).sort((a,b)=>order.indexOf(a[0])-order.indexOf(b[0]));
    const tip=binName(i,false)+'　'+duration(sums[i]);
    const day=clickable?dayOffset(report.start,i):'';
    const attrs=clickable&&sums[i]?` data-day="${day}" role="button" tabindex="0" aria-label="${escape(tip)}、この日を表示"`:` role="img" tabindex="${sums[i]?0:-1}" aria-label="${escape(tip)}"`;
    return `<div class="bar${clickable&&sums[i]?' link-day':''}" data-tip="${escape(tip)}"${attrs}>${segs.map(([k,ms])=>`<i style="height:${100*ms/(ceiling*60000)}%;background:${color(k)}"></i>`).join('')}</div>`;
  }).join('');
  const every=period==='day'?6:period==='month'?5:1;
  const labels=sums.map((_,i)=>{const show=period==='month'?(i===0||(i+1)%every===0):i%every===0;return `<span>${show?binName(i,true):''}</span>`;}).join('');
  let avg='';
  if(period!=='day' && report.total){
    const days=period==='year'?null:elapsedDays();
    const value=period==='year'?report.total/Math.max(1,sums.filter(Boolean).length):report.total/days;
    const pos=100-100*value/(ceiling*60000);
    if(pos>0)avg=`<div class="avg" style="top:${pos}%"><span>平均</span></div>`;
  }
  const legend=cats.length?`<div class="legend" aria-label="カテゴリ">${cats.map(([k,ms])=>`<span>${dot(k)}${categories[k]||categories.other}<b>${duration(ms)}</b></span>`).join('')}</div>`:'';
  return `<section class="chartgroup" aria-label="${period==='day'?'時間帯別':period==='year'?'月別':'日別'}の使用時間"><div class="chart"><div class="plot">${cols}${avg}</div><div class="ylabels"><span style="top:0">${scale(ceiling)}</span><span style="top:50%">${scale(ceiling/2)}</span><span style="top:100%">0</span></div><div class="xlabels">${labels}</div>${report.total?'':'<span class="emptyplot">この期間の記録はありません</span>'}<output class="charttip" hidden></output></div>${legend}</section>`;
}
function rows(list,kind,full){
  const filtered=list.map((row,index)=>({row,index})).filter(({row})=>!full||row.name.toLowerCase().includes(query.toLowerCase()));
  if(!filtered.length){
    if(query)return '<div class="empty">一致する項目はありません。</div>';
    if(kind==='site')return '<div class="empty"><strong>サイトの記録はありません</strong>Chrome・Edge の拡張機能を追加すると、ドメインごとの時間を表示できます。<br><button class="link" data-settings>拡張機能の設定</button></div>';
    return '<div class="empty"><strong>アプリの記録はありません</strong>PC を操作すると、使ったアプリがここに表示されます。</div>';
  }
  const top=list[0]?.ms||1;
  return '<ul class="list">'+filtered.slice(0,full?visibleRows:5).map(({row,index})=>{
    const img=row.icon?`<span class="appicon plain"><img src="data:image/png;base64,${escape(row.icon)}" alt=""></span>`:
      `<span class="appicon">${kind==='site'?icon('sites'):escape(Array.from(row.name)[0]||'•')}</span>`;
    const edit=full?`<button class="rowedit" data-edit="${index}" aria-label="${escape(row.name)}のカテゴリを変更" title="カテゴリを変更">${icon('edit')}</button>`:'';
    return `<li class="row${full?' editable':''}">${img}<div class="rowtext"><div class="appname" title="${escape(row.name)}">${escape(row.name)}</div><div class="category">${dot(row.category)}<span class="categorylabel">${categories[row.category]||categories.other}</span></div></div><span class="time">${duration(row.ms)}</span>${edit}</li>`;
  }).join('')+'</ul>'+(full&&filtered.length>visibleRows?'<button class="secondary more" data-more>さらに表示</button>':'');
}
function overview(){
  const cats=totals();
  const sites=report.sites.length?`<section class="section" data-list="sites"><div class="sectionhead"><h2>Webサイト</h2><button class="link" data-go="sites">すべて表示${icon('right')}</button></div>${rows(report.sites,'site',false)}</section>`:'';
  return summary()+chart(cats)+`<section class="section" data-list="apps"><div class="sectionhead"><h2>よく使われたアプリ</h2>${report.apps.length?`<button class="link" data-all>すべて表示${icon('right')}</button>`:''}</div>${rows(report.apps,'app',false)}</section>`+sites;
}
function listing(){
  const sites=page==='sites',list=sites?report.sites:report.apps;
  return summary()+`<section class="section"><div class="toolbar"><h2>${sites?'Webサイト':'アプリ'}<small class="count">${list.length} 件</small></h2><label class="search">${icon('search')}<input type="search" id="filter" aria-label="名前で検索" placeholder="検索" value="${escape(query)}"></label></div><div id="rows">${rows(list,sites?'site':'app',true)}</div></section>`;
}
function settingRow(name,desc,control){return `<div class="settingrow"><div><strong>${name}</strong>${desc?`<p>${desc}</p>`:''}</div>${control}</div>`;}
function toggle(action,on,label){return `<button class="toggle" data-toggle="${action}" role="switch" aria-label="${label}" aria-checked="${on}"><i></i></button>`;}
function choices(id,options,selected,label){return `<select data-select="${id}" aria-label="${label}">${options.map(([v,l])=>`<option value="${v}" ${String(selected)===v?'selected':''}>${l}</option>`).join('')}</select>`;}
function group(title,body){return `<section class="group"><h2>${title}</h2><div class="groupbox">${body}</div></section>`;}
function settings(){
  return group('記録',
      settingRow('使用時間を記録','画面を閉じた後も、操作中のアプリを記録します。',toggle('paused',!prefs.paused,'使用時間を記録'))+
      settingRow('サインイン時に起動','Windows にサインインすると、タスクトレイで記録を始めます。',toggle('startup',prefs.startup,'サインイン時に起動'))+
      settingRow('離席と判定するまで','最後の操作からこの時間を過ぎた分は含めません。',choices('idle',[['60','1 分'],['180','3 分'],['300','5 分'],['1800','30 分'],['3600','1 時間']],prefs.idle,'離席と判定するまで')))+
    group('表示',settingRow('テーマ','',choices('theme',[['system','Windows に合わせる'],['light','ライト'],['dark','ダーク']],prefs.theme,'テーマ')))+
    group('ソフトウェアアップデート',
      settingRow('Tempo '+escape(prefs.version||''),'更新情報と配布ファイルだけを取得します。使用履歴は送信しません。',
        `<button class="secondary" data-update-check ${busyUpdate()?'disabled':''}>アップデートを確認</button>`)+
      `<div class="update" id="update" aria-live="polite">${updateBody()}</div>`)+
    group('ブラウザ拡張機能',`<p class="explain">Chrome・Edge で、操作中のタブのドメイン別に使用時間を集計します。URL のパスやページ名は保存しません。</p><ol class="instruction"><li>ブラウザの拡張機能の管理画面で「デベロッパー モード」を有効にします。</li><li>「展開して読み込み」または「パッケージ化されていない拡張機能を読み込む」で、下記のフォルダーを選びます。</li></ol>`+
      settingRow(`<span class="path">${escape(prefs.extension)}</span>`,'',`<button class="secondary" data-extension>フォルダーを開く</button>`))+
    group('保存先',settingRow(`<span class="path">${escape(prefs.data)}\\history.sqlite3</span>`,'履歴はこの PC だけに保存されます。',''));
}
const busyUpdate=()=>['checking','downloading','verifying','installing'].includes(update?.state);
function notesList(text){
  const lines=String(text||'').split(/\r?\n/).map(s=>s.trim()).filter(Boolean);
  if(!lines.length)return '';
  const bullet=/^([-*・•]|\d+\.)\s*/;
  if(lines.every(l=>bullet.test(l)))return `<ul class="notes">${lines.map(l=>`<li>${escape(l.replace(bullet,''))}</li>`).join('')}</ul>`;
  return `<ul class="notes plain">${lines.map(l=>`<li>${escape(l)}</li>`).join('')}</ul>`;
}
function megabytes(n){return (n/1048576).toFixed(1)+' MB';}
function updateBody(){
  const u=update;if(!u||u.state==='idle')return '';
  switch(u.state){
    case 'checking':return '<p class="state"><span class="spinner"></span>確認しています…</p>';
    case 'latest':return `<p class="state">お使いの Tempo ${escape(u.current)} は最新です。</p>`;
    case 'error':return `<p class="state error">${escape(u.message)}</p>`+(u.version?`<div class="updateactions"><button class="secondary" data-update-install>もう一度試す</button></div>`:'');
  }
  const head=`<h3>Tempo ${escape(u.version)}</h3><p class="meta">${u.date?escape(u.date)+' · ':''}${megabytes(u.size)}</p>${notesList(u.notes)}`;
  if(u.state==='available')return head+(u.portable?`<p class="state" style="margin-top:10px">ZIP 版はアプリ内で更新できません。インストーラー版をお使いください。</p>`:`<div class="updateactions"><button class="primary" data-update-install>ダウンロードして更新</button></div>`);
  if(u.state==='downloading'){const pct=u.total?Math.floor(100*u.received/u.total):0;
    return head+`<div class="updateactions"><div class="progress" role="progressbar" aria-label="ダウンロード" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${pct}"><i style="width:${pct}%"></i></div><span class="state num">${pct}%</span><button class="secondary" data-update-cancel>中止</button></div>`;}
  if(u.state==='verifying')return head+'<p class="state" style="margin-top:10px"><span class="spinner"></span>署名とファイルを確認しています…</p>';
  if(u.state==='installing')return head+'<p class="state" style="margin-top:10px"><span class="spinner"></span>記録を保存して更新しています。完了すると Tempo が再起動します。</p>';
  return '';
}
function renderUpdate(){
  const box=$('update');if(!box)return;
  box.innerHTML=updateBody();
  const b=document.querySelector('[data-update-check]');if(b)b.disabled=busyUpdate();
}
function render(animate){
  const c=$('content');
  const active=document.activeElement,hadFilter=active?.id==='filter',pos=hadFilter?active.selectionStart:null;
  // Leave active preference controls intact during a periodic refresh.
  if(page==='settings'&&!animate&&c.querySelector('.group')&&active?.matches('select'))return;
  c.innerHTML=page==='overview'?overview():page==='settings'?settings():listing();
  if(hadFilter&&$('filter')){$('filter').focus();$('filter').setSelectionRange(pos,pos);}
  if(animate){c.classList.remove('enter');void c.offsetWidth;c.classList.add('enter');}
  c.querySelectorAll('.bar').forEach(bar=>{
    const show=()=>{const t=c.querySelector('.charttip'),plot=bar.parentNode;t.textContent=bar.dataset.tip;t.hidden=false;
      const box=bar.getBoundingClientRect(),w=plot.clientWidth,half=t.offsetWidth/2;
      t.style.left=Math.max(half,Math.min(w-half,box.left-plot.getBoundingClientRect().left+box.width/2))+'px';};
    const hide=()=>{c.querySelector('.charttip').hidden=true;};
    bar.onmouseenter=show;bar.onmouseleave=hide;bar.onfocus=show;bar.onblur=hide;
  });
}
async function change(action,args){
  try{const r=await bridge(action,args);if(r.settings){prefs=r.settings;status();}await refresh(true);return true;}
  catch(e){notice('変更できませんでした。'+e.message,true);return false;}
}
async function command(action){
  try{await bridge(action,{},30000);}catch(e){update={state:'error',message:e.message};renderUpdate();}
}
$('content').addEventListener('input',e=>{
  if(e.target.id==='filter'){query=e.target.value;visibleRows=50;$('rows').innerHTML=rows(page==='sites'?report.sites:report.apps,page==='sites'?'site':'app',true);}
});
$('content').addEventListener('change',e=>{if(e.target.dataset.select)change(e.target.dataset.select,{value:e.target.value});});
function openDay(day){period='day';changeDate(day);}
$('content').addEventListener('keydown',e=>{const bar=e.target.closest('[data-day]');if(bar&&(e.key==='Enter'||e.key===' ')){e.preventDefault();openDay(bar.dataset.day);}});
$('content').addEventListener('click',e=>{
  const bar=e.target.closest('[data-day]');if(bar){openDay(bar.dataset.day);return;}
  const b=e.target.closest('button');if(!b)return;
  if(b.hasAttribute('data-more')){visibleRows+=50;$('rows').innerHTML=rows(page==='sites'?report.sites:report.apps,page==='sites'?'site':'app',true);}
  if(b.hasAttribute('data-all'))go('apps');
  if(b.dataset.go)go(b.dataset.go);
  if(b.hasAttribute('data-settings'))go('settings');
  if(b.hasAttribute('data-extension'))change('extension',{});
  if(b.hasAttribute('data-update-check'))command('update-check');
  if(b.hasAttribute('data-update-install'))command('update-install');
  if(b.hasAttribute('data-update-cancel'))command('update-cancel');
  if(b.dataset.toggle){b.disabled=true;const action=b.dataset.toggle;change(action,{value:String(action==='paused'?!prefs.paused:!prefs.startup)}).finally(()=>b.disabled=false);}
  if(b.hasAttribute('data-edit')){
    const site=page==='sites',row=(site?report.sites:report.apps)[Number(b.dataset.edit)];
    editing={kind:site?'site':'app',id:row.id};$('edit-name').textContent=row.name;
    $('options').innerHTML=Object.entries({auto:'自動分類',...categories}).map(([k,v])=>`<label><input type="radio" name="category" value="${k}" ${k===(row.rule||'auto')?'checked':''}>${k==='auto'?'':dot(k)}${v}</label>`).join('');
    $('editor').showModal();
  }
});
$('save').onclick=async()=>{
  const selected=$('options').querySelector(':checked');if(!selected||!editing)return;
  $('save').disabled=true;try{if(await change('rule',{...editing,value:selected.value}))$('editor').close();}finally{$('save').disabled=false;}
};
theme();updateHeader();refresh(true);
setInterval(()=>{if(!document.hidden&&!$('editor').open&&page!=='settings'&&!calls.size)refresh();},15000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden&&!$('editor').open)refresh();});
})();
