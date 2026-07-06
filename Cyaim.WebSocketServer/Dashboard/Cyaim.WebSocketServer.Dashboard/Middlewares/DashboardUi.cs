namespace Cyaim.WebSocketServer.Dashboard.Middlewares
{
    /// <summary>
    /// Self-contained single-page management console (dashboard + admin operations),
    /// served inline so it works when the Dashboard is consumed as a NuGet library
    /// (no wwwroot files required). No external/CDN assets — CSP-safe and offline-capable.
    /// 自包含的单页管理控制台（看板 + 管理操作），内联提供，作为 NuGet 库引用时无需 wwwroot 文件；
    /// 无外部/CDN 资源，CSP 安全、可离线。
    /// </summary>
    internal static class DashboardUi
    {
        /// <summary>The full dashboard page. {BASE} is replaced with the API base path at serve time.</summary>
        public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Cyaim WebSocketServer — Console</title>
<style>
:root{--bg:#0f1420;--panel:#171e2e;--panel2:#1e2740;--line:#2a3550;--txt:#e6ebf5;--mut:#8b97b3;--acc:#4f8cff;--ok:#31c48d;--warn:#f6a609;--err:#f04c58;--radius:10px}
*{box-sizing:border-box}
body{margin:0;font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,"PingFang SC","Microsoft YaHei",sans-serif;background:var(--bg);color:var(--txt)}
a{color:var(--acc);text-decoration:none}
.topbar{display:flex;align-items:center;gap:16px;padding:12px 20px;background:var(--panel);border-bottom:1px solid var(--line);position:sticky;top:0;z-index:10}
.brand{font-weight:700;font-size:16px;letter-spacing:.3px}
.brand b{color:var(--acc)}
.pill{display:inline-flex;align-items:center;gap:6px;padding:3px 10px;border-radius:999px;font-size:12px;font-weight:600;background:var(--panel2);border:1px solid var(--line)}
.pill .dot{width:8px;height:8px;border-radius:50%}
.dot.ok{background:var(--ok)}.dot.err{background:var(--err)}.dot.warn{background:var(--warn)}
.spacer{flex:1}
.ctrl{display:flex;align-items:center;gap:8px;color:var(--mut);font-size:12px}
select,input,textarea,button{font:inherit;color:var(--txt);background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:7px 10px;outline:none}
select:focus,input:focus,textarea:focus{border-color:var(--acc)}
button{cursor:pointer;background:var(--panel2)}
button:hover{border-color:var(--acc)}
button.primary{background:var(--acc);border-color:var(--acc);color:#fff;font-weight:600}
button.primary:hover{filter:brightness(1.08)}
button.danger{color:var(--err);border-color:#4a2733}
button.danger:hover{background:#33202a}
button.sm{padding:4px 9px;font-size:12px}
.layout{display:flex;min-height:calc(100vh - 53px)}
.side{width:210px;flex:0 0 210px;background:var(--panel);border-right:1px solid var(--line);padding:14px 10px}
.nav{display:flex;flex-direction:column;gap:2px}
.nav a{display:flex;align-items:center;gap:10px;padding:9px 12px;border-radius:8px;color:var(--mut);font-weight:600}
.nav a:hover{background:var(--panel2);color:var(--txt)}
.nav a.active{background:var(--panel2);color:var(--txt);box-shadow:inset 3px 0 0 var(--acc)}
.main{flex:1;padding:22px 26px;overflow:auto}
h1{font-size:20px;margin:0 0 4px}
.sub{color:var(--mut);margin:0 0 20px}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:14px;margin-bottom:24px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:16px}
.card .k{color:var(--mut);font-size:12px;text-transform:uppercase;letter-spacing:.5px}
.card .v{font-size:26px;font-weight:700;margin-top:6px}
.card .v.sm{font-size:18px}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);overflow:hidden;margin-bottom:20px}
.panel .ph{display:flex;align-items:center;gap:12px;padding:12px 16px;border-bottom:1px solid var(--line)}
.panel .ph h2{font-size:14px;margin:0}
table{width:100%;border-collapse:collapse}
th,td{text-align:left;padding:10px 16px;border-bottom:1px solid var(--line);font-size:13px;white-space:nowrap}
th{color:var(--mut);font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:.5px}
tbody tr:hover{background:var(--panel2)}
td.mono,.mono{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}
.tag{display:inline-block;padding:2px 8px;border-radius:6px;font-size:11px;font-weight:600;background:var(--panel2);border:1px solid var(--line)}
.tag.open{color:var(--ok);border-color:#1f4739}
.empty{padding:40px;text-align:center;color:var(--mut)}
.row{display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end}
.field{display:flex;flex-direction:column;gap:5px}
.field label{color:var(--mut);font-size:12px}
.field input,.field select,.field textarea{min-width:220px}
textarea{min-height:90px;resize:vertical;font-family:ui-monospace,monospace}
.modal-bg{position:fixed;inset:0;background:rgba(0,0,0,.5);display:none;align-items:center;justify-content:center;z-index:50}
.modal-bg.show{display:flex}
.modal{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);width:460px;max-width:92vw;padding:20px}
.modal h3{margin:0 0 14px}
.toast{position:fixed;right:20px;bottom:20px;display:flex;flex-direction:column;gap:8px;z-index:60}
.toast div{background:var(--panel2);border:1px solid var(--line);border-left:3px solid var(--acc);padding:10px 14px;border-radius:8px;box-shadow:0 6px 20px rgba(0,0,0,.4);animation:in .2s}
.toast div.ok{border-left-color:var(--ok)}.toast div.err{border-left-color:var(--err)}
@keyframes in{from{opacity:0;transform:translateY(8px)}}
.hide{display:none!important}
@media(max-width:720px){.side{width:56px;flex-basis:56px}.nav a span{display:none}}
</style>
</head>
<body>
<div class="topbar">
  <div class="brand">Cyaim <b>WebSocketServer</b></div>
  <span id="healthPill" class="pill"><span class="dot warn"></span><span id="healthTxt">connecting…</span></span>
  <span id="modePill" class="pill">—</span>
  <div class="spacer"></div>
  <div class="ctrl">auto
    <select id="interval">
      <option value="0">off</option>
      <option value="2000">2s</option>
      <option value="5000" selected>5s</option>
      <option value="10000">10s</option>
    </select>
    <button class="sm" onclick="refreshAll()">↻ Refresh</button>
  </div>
</div>
<div class="layout">
  <aside class="side"><nav class="nav" id="nav">
    <a data-view="overview" class="active">▤ <span>Overview</span></a>
    <a data-view="connections">⇄ <span>Connections</span></a>
    <a data-view="endpoints">◎ <span>Endpoints</span></a>
    <a data-view="broadcast">📣 <span>Broadcast</span></a>
    <a data-view="cluster">⛭ <span>Cluster</span></a>
  </nav></aside>
  <main class="main">
    <section id="v-overview">
      <h1>Overview</h1><p class="sub">Live server status and traffic.</p>
      <div class="cards" id="ovCards"></div>
    </section>
    <section id="v-connections" class="hide">
      <h1>Connections</h1><p class="sub">Active WebSocket connections. Send a message or disconnect a client.</p>
      <div class="panel"><div class="ph"><h2>Connections</h2><span id="connCount" class="pill">0</span><div class="spacer"></div><button class="sm" onclick="loadConnections()">↻</button></div>
        <div style="overflow:auto"><table><thead><tr><th>Connection ID</th><th>Node</th><th>State</th><th>Endpoint</th><th>IP</th><th>Sent</th><th>Recv</th><th>Actions</th></tr></thead><tbody id="connBody"></tbody></table></div>
      </div>
    </section>
    <section id="v-endpoints" class="hide">
      <h1>Endpoints</h1><p class="sub">Discovered WebSocket MVC endpoints.</p>
      <div class="panel"><div class="ph"><h2>Endpoints</h2><span id="epCount" class="pill">0</span><div class="spacer"></div><button class="sm" onclick="loadEndpoints()">↻</button></div>
        <div style="overflow:auto"><table><thead><tr><th>Controller</th><th>Action</th><th>Target</th><th>Methods</th></tr></thead><tbody id="epBody"></tbody></table></div>
      </div>
    </section>
    <section id="v-broadcast" class="hide">
      <h1>Broadcast</h1><p class="sub">Send a message to every connected client (management operation).</p>
      <div class="panel"><div class="ph"><h2>Broadcast message</h2></div>
        <div style="padding:16px">
          <div class="field" style="margin-bottom:12px"><label>Message</label><textarea id="bcMsg" placeholder="Text or JSON to broadcast…"></textarea></div>
          <button class="primary" onclick="doBroadcast()">Broadcast to all</button>
        </div>
      </div>
    </section>
    <section id="v-cluster" class="hide">
      <h1>Cluster</h1><p class="sub">Cluster nodes and leader (standalone mode shows a single node).</p>
      <div class="cards" id="clCards"></div>
      <div class="panel"><div class="ph"><h2>Nodes</h2><div class="spacer"></div><button class="sm" onclick="loadCluster()">↻</button></div>
        <div style="overflow:auto"><table><thead><tr><th>Node ID</th><th>State</th><th>Leader</th><th>Connected</th><th>Connections</th></tr></thead><tbody id="clBody"></tbody></table></div>
      </div>
    </section>
  </main>
</div>

<div class="modal-bg" id="sendModal">
  <div class="modal">
    <h3>Send message to connection</h3>
    <div class="field" style="margin-bottom:10px"><label>Connection ID</label><input id="smId" class="mono" readonly></div>
    <div class="field" style="margin-bottom:10px"><label>Type</label><select id="smType"><option>Text</option><option>Binary</option></select></div>
    <div class="field" style="margin-bottom:16px"><label>Content</label><textarea id="smContent" placeholder="Message content…"></textarea></div>
    <div class="row" style="justify-content:flex-end"><button onclick="closeSend()">Cancel</button><button class="primary" onclick="doSend()">Send</button></div>
  </div>
</div>
<div class="toast" id="toast"></div>

<script>
const BASE = "{BASE}";
const api = (p,o)=>fetch(BASE+p,o).then(async r=>{const j=await r.json().catch(()=>({}));return {ok:r.ok,j};});
const $=s=>document.querySelector(s), el=(t,c,h)=>{const e=document.createElement(t);if(c)e.className=c;if(h!=null)e.innerHTML=h;return e;};
function toast(msg,kind){const t=el('div',kind||'',msg);$('#toast').appendChild(t);setTimeout(()=>t.remove(),3200);}
function fmtBytes(n){n=Number(n)||0;if(n<1024)return n+' B';if(n<1048576)return (n/1024).toFixed(1)+' KB';return (n/1048576).toFixed(1)+' MB';}
function esc(s){return String(s==null?'':s).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}

let view='overview';
$('#nav').addEventListener('click',e=>{const a=e.target.closest('a');if(!a)return;view=a.dataset.view;
  document.querySelectorAll('.nav a').forEach(x=>x.classList.toggle('active',x===a));
  document.querySelectorAll('main section').forEach(s=>s.classList.add('hide'));
  $('#v-'+view).classList.remove('hide');refreshAll();});

async function loadHealth(){
  const {ok,j}=await api('/health');
  const d=(ok&&j.success)?j.data:null;
  const hp=$('#healthPill'), ht=$('#healthTxt');
  if(d){const good=d.isHealthy;hp.querySelector('.dot').className='dot '+(good?'ok':'err');ht.textContent=good?'healthy':'unhealthy';
    $('#modePill').textContent=(d.details&&d.details.Mode)?('mode: '+d.details.Mode):'—';}
  else{hp.querySelector('.dot').className='dot err';ht.textContent='unreachable';}
  return d;
}
async function loadOverview(){
  const d=await loadHealth();
  const {j:cj}=await api('/client/count'); const cnt=(cj&&cj.success)?cj.data:{total:0,local:0};
  const {j:bj}=await api('/statistics/bandwidth'); const bw=(bj&&bj.success)?bj.data:null;
  const cards=[
    ['Health', d?(d.isHealthy?'Healthy':'Unhealthy'):'—', d&&d.isHealthy?'ok':'err'],
    ['Total connections', cnt.total ?? 0],
    ['Local connections', cnt.local ?? 0],
    ['Mode', d&&d.details?d.details.Mode:'—','',true],
    ['Nodes', d?d.totalNodes:'—'],
    ['Has leader', d?(d.hasLeader?'Yes':'No'):'—','',true],
  ];
  const box=$('#ovCards');box.innerHTML='';
  cards.forEach(([k,v,cls,sm])=>{const c=el('div','card');c.innerHTML=`<div class="k">${k}</div><div class="v ${sm?'sm':''}" style="${cls==='ok'?'color:var(--ok)':cls==='err'?'color:var(--err)':''}">${esc(v)}</div>`;box.appendChild(c);});
  if(bw){['Bytes sent','Bytes received'].forEach((k,i)=>{const v=i===0?bw.totalBytesSent:bw.totalBytesReceived;const c=el('div','card');c.innerHTML=`<div class="k">${k}</div><div class="v sm">${fmtBytes(v)}</div>`;box.appendChild(c);});}
}
async function loadConnections(){
  const {ok,j}=await api('/client'); const list=(ok&&j.success&&j.data)?j.data:[];
  $('#connCount').textContent=list.length; const b=$('#connBody');b.innerHTML='';
  if(!list.length){b.innerHTML='<tr><td colspan="8"><div class="empty">No active connections</div></td></tr>';return;}
  list.forEach(c=>{const tr=el('tr');
    tr.innerHTML=`<td class="mono">${esc(c.connectionId)}</td><td>${esc(c.nodeId)}</td>
      <td><span class="tag ${c.state==='Open'?'open':''}">${esc(c.state)}</span></td>
      <td class="mono">${esc(c.endpoint||'—')}</td><td class="mono">${esc(c.remoteIpAddress||'—')}</td>
      <td>${fmtBytes(c.bytesSent)}</td><td>${fmtBytes(c.bytesReceived)}</td>
      <td><button class="sm" data-send="${esc(c.connectionId)}">Send</button> <button class="sm danger" data-kill="${esc(c.connectionId)}">Disconnect</button></td>`;
    b.appendChild(tr);});
  b.querySelectorAll('[data-send]').forEach(x=>x.onclick=()=>openSend(x.dataset.send));
  b.querySelectorAll('[data-kill]').forEach(x=>x.onclick=()=>killConn(x.dataset.kill));
}
async function loadEndpoints(){
  const {ok,j}=await api('/endpoints'); const list=(ok&&j.success&&j.data)?j.data:[];
  $('#epCount').textContent=list.length; const b=$('#epBody');b.innerHTML='';
  if(!list.length){b.innerHTML='<tr><td colspan="4"><div class="empty">No endpoints</div></td></tr>';return;}
  list.forEach(e=>{const tr=el('tr');tr.innerHTML=`<td>${esc(e.controller)}</td><td>${esc(e.action)}</td><td class="mono">${esc(e.target)}</td><td>${esc((e.methods||[]).join(', '))}</td>`;b.appendChild(tr);});
}
async function loadCluster(){
  const {j:oj}=await api('/cluster/overview'); const ov=(oj&&oj.success)?oj.data:null;
  const box=$('#clCards');box.innerHTML='';
  const cards=[['Total nodes',ov?ov.totalNodes:'—'],['Leader',ov&&ov.leaderId?ov.leaderId:'—','',true],['Current term',ov?ov.currentTerm:'—']];
  cards.forEach(([k,v,c,sm])=>{const e=el('div','card');e.innerHTML=`<div class="k">${k}</div><div class="v ${sm?'sm':''}">${esc(v)}</div>`;box.appendChild(e);});
  const {j:nj}=await api('/cluster/nodes'); const nodes=(nj&&nj.success&&nj.data)?nj.data:[];
  const b=$('#clBody');b.innerHTML='';
  if(!nodes.length){b.innerHTML='<tr><td colspan="5"><div class="empty">No nodes</div></td></tr>';return;}
  nodes.forEach(n=>{const tr=el('tr');tr.innerHTML=`<td class="mono">${esc(n.nodeId)}</td><td>${esc(n.state)}</td><td>${n.isLeader?'★':''}</td><td>${n.isConnected?'✓':'✗'}</td><td>${esc(n.connectionCount??0)}</td>`;b.appendChild(tr);});
}

// ---- management operations ----
let sendId=null;
function openSend(id){sendId=id;$('#smId').value=id;$('#smContent').value='';$('#sendModal').classList.add('show');}
function closeSend(){$('#sendModal').classList.remove('show');}
async function doSend(){
  const body={connectionId:sendId,content:$('#smContent').value,messageType:$('#smType').value};
  const {ok,j}=await api('/messages/send',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  if(ok&&j.success){toast('Message sent','ok');closeSend();}else{toast('Send failed: '+((j&&j.error)||'error'),'err');}
}
async function killConn(id){
  if(!confirm('Disconnect '+id+'?'))return;
  const {ok,j}=await api('/client/'+encodeURIComponent(id),{method:'DELETE'});
  if(ok&&j.success){toast('Disconnected '+id,'ok');loadConnections();}else{toast('Disconnect failed: '+((j&&j.error)||'error'),'err');}
}
async function doBroadcast(){
  const msg=$('#bcMsg').value; if(!msg){toast('Enter a message','err');return;}
  const {ok,j}=await api('/messages/broadcast',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({content:msg,messageType:'Text'})});
  if(ok&&j.success){toast('Broadcast sent'+(j.data!=null?(' ('+j.data+' clients)'):''),'ok');$('#bcMsg').value='';}
  else{toast('Broadcast failed: '+((j&&j.error)||'error'),'err');}
}

function refreshAll(){loadHealth();
  if(view==='overview')loadOverview();
  else if(view==='connections')loadConnections();
  else if(view==='endpoints')loadEndpoints();
  else if(view==='cluster')loadCluster();
}
let timer=null;
function setTimerFromSelect(){if(timer)clearInterval(timer);const ms=+$('#interval').value;if(ms>0)timer=setInterval(refreshAll,ms);}
$('#interval').addEventListener('change',setTimerFromSelect);
$('#sendModal').addEventListener('click',e=>{if(e.target.id==='sendModal')closeSend();});
refreshAll();setTimerFromSelect();
</script>
</body>
</html>
""";
    }
}
