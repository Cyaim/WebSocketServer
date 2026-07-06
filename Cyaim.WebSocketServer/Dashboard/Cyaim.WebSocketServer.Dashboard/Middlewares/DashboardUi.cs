namespace Cyaim.WebSocketServer.Dashboard.Middlewares
{
    /// <summary>
    /// Self-contained single-page management console (dashboard + admin operations), served inline so
    /// it works when the Dashboard is consumed as a NuGet library (no wwwroot files required).
    /// No external/CDN assets — CSP-safe and offline-capable. Charts are drawn on canvas in vanilla JS.
    /// 自包含单页管理后台（看板 + 管理操作），内联提供；无外部/CDN 依赖，CSP 安全、可离线；图表用原生 JS 画在 canvas 上。
    /// </summary>
    internal static class DashboardUi
    {
        /// <summary>The full console page. {BASE} is replaced with the API base path at serve time.</summary>
        public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Cyaim WebSocketServer — Management Console</title>
<style>
:root{--bg:#0e1320;--panel:#151d2e;--panel2:#1c2740;--line:#28344f;--txt:#e7ecf6;--mut:#8a97b5;--acc:#4f8cff;--acc2:#8b5cf6;--ok:#31c48d;--warn:#f6a609;--err:#f0556b;--r:10px}
*{box-sizing:border-box}
body{margin:0;font:13.5px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,"PingFang SC","Microsoft YaHei",sans-serif;background:var(--bg);color:var(--txt)}
a{color:var(--acc);text-decoration:none}
.top{display:flex;align-items:center;gap:14px;padding:11px 20px;background:var(--panel);border-bottom:1px solid var(--line);position:sticky;top:0;z-index:10}
.brand{font-weight:700;font-size:15px}.brand b{color:var(--acc)}
.pill{display:inline-flex;align-items:center;gap:6px;padding:3px 10px;border-radius:999px;font-size:12px;font-weight:600;background:var(--panel2);border:1px solid var(--line)}
.dot{width:8px;height:8px;border-radius:50%}.dot.ok{background:var(--ok)}.dot.err{background:var(--err)}.dot.warn{background:var(--warn)}
.sp{flex:1}
.ctrl{display:flex;align-items:center;gap:8px;color:var(--mut);font-size:12px}
select,input,textarea,button{font:inherit;color:var(--txt);background:var(--panel2);border:1px solid var(--line);border-radius:8px;padding:7px 10px;outline:none}
select:focus,input:focus,textarea:focus{border-color:var(--acc)}
button{cursor:pointer}button:hover{border-color:var(--acc)}
button.primary{background:var(--acc);border-color:var(--acc);color:#fff;font-weight:600}button.primary:hover{filter:brightness(1.08)}
button.danger{color:var(--err);border-color:#4a2733}button.danger:hover{background:#33202a}
button.sm{padding:4px 9px;font-size:12px}
.layout{display:flex;min-height:calc(100vh - 51px)}
.side{width:200px;flex:0 0 200px;background:var(--panel);border-right:1px solid var(--line);padding:12px 10px}
.nav{display:flex;flex-direction:column;gap:2px}
.nav a{display:flex;align-items:center;gap:10px;padding:9px 12px;border-radius:8px;color:var(--mut);font-weight:600;cursor:pointer}
.nav a:hover{background:var(--panel2);color:var(--txt)}
.nav a.active{background:var(--panel2);color:var(--txt);box-shadow:inset 3px 0 0 var(--acc)}
.main{flex:1;padding:20px 24px;overflow:auto}
h1{font-size:19px;margin:0 0 3px}.sub{color:var(--mut);margin:0 0 18px}
.cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:12px;margin-bottom:20px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:var(--r);padding:15px}
.card .k{color:var(--mut);font-size:11px;text-transform:uppercase;letter-spacing:.5px}
.card .v{font-size:24px;font-weight:700;margin-top:5px}.card .v.sm{font-size:17px}
.charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:14px;margin-bottom:20px}
.chart{background:var(--panel);border:1px solid var(--line);border-radius:var(--r);padding:14px}
.chart h3{margin:0 0 2px;font-size:13px}.chart .now{color:var(--mut);font-size:12px;margin-bottom:8px}
canvas{width:100%;height:120px;display:block}
.panel{background:var(--panel);border:1px solid var(--line);border-radius:var(--r);overflow:hidden;margin-bottom:18px}
.ph{display:flex;align-items:center;gap:10px;padding:11px 15px;border-bottom:1px solid var(--line)}.ph h2{font-size:13px;margin:0}
table{width:100%;border-collapse:collapse}
th,td{text-align:left;padding:9px 15px;border-bottom:1px solid var(--line);font-size:12.5px;white-space:nowrap}
th{color:var(--mut);font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:.5px}
tbody tr:hover{background:var(--panel2)}
.mono{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}
.tag{display:inline-block;padding:2px 8px;border-radius:6px;font-size:11px;font-weight:600;background:var(--panel2);border:1px solid var(--line)}
.tag.open{color:var(--ok);border-color:#1f4739}.tag.leader{color:var(--warn);border-color:#4a3a17}
.empty{padding:38px;text-align:center;color:var(--mut)}
.tbl-wrap{overflow:auto}
.topo{display:flex;gap:16px;flex-wrap:wrap;padding:16px}
.node{background:var(--panel2);border:1px solid var(--line);border-radius:12px;padding:14px 18px;min-width:170px}
.node .id{font-weight:700;font-family:ui-monospace,monospace}.node.leadr{border-color:var(--warn)}
.node .m{color:var(--mut);font-size:12px;margin-top:6px;display:flex;justify-content:space-between}
.field{display:flex;flex-direction:column;gap:5px}.field label{color:var(--mut);font-size:12px}
textarea{min-height:80px;resize:vertical;font-family:ui-monospace,monospace}
.row{display:flex;gap:10px;flex-wrap:wrap;align-items:flex-end}
.modal-bg{position:fixed;inset:0;background:rgba(0,0,0,.55);display:none;align-items:center;justify-content:center;z-index:50}
.modal-bg.show{display:flex}
.modal{background:var(--panel);border:1px solid var(--line);border-radius:var(--r);width:460px;max-width:92vw;padding:20px}
.toast{position:fixed;right:18px;bottom:18px;display:flex;flex-direction:column;gap:8px;z-index:60}
.toast div{background:var(--panel2);border:1px solid var(--line);border-left:3px solid var(--acc);padding:10px 14px;border-radius:8px;box-shadow:0 6px 20px rgba(0,0,0,.4)}
.toast div.ok{border-left-color:var(--ok)}.toast div.err{border-left-color:var(--err)}
.hide{display:none!important}
</style>
</head>
<body>
<div class="top">
  <div class="brand">Cyaim <b>WebSocketServer</b> · Console</div>
  <span id="healthPill" class="pill"><span class="dot warn"></span><span id="healthTxt">connecting…</span></span>
  <span id="modePill" class="pill">—</span>
  <div class="sp"></div>
  <div class="ctrl">auto
    <select id="interval"><option value="0">off</option><option value="2000">2s</option><option value="3000" selected>3s</option><option value="5000">5s</option></select>
    <button class="sm" onclick="refreshAll()">↻ Refresh</button>
  </div>
</div>
<div class="layout">
  <aside class="side"><nav class="nav" id="nav">
    <a data-v="overview" class="active">▤ Dashboard</a>
    <a data-v="users">◉ Online Users</a>
    <a data-v="routing">⇄ Message Routing</a>
    <a data-v="cluster">⛭ WS Cluster</a>
    <a data-v="endpoints">◎ Endpoints</a>
    <a data-v="messages">✉ Messages</a>
  </nav></aside>
  <main class="main">

    <section id="v-overview">
      <h1>Dashboard</h1><p class="sub">Live server metrics.</p>
      <div class="cards" id="ovCards"></div>
      <div class="charts">
        <div class="chart"><h3>Connections</h3><div class="now" id="cNowConn">—</div><canvas id="chConn"></canvas></div>
        <div class="chart"><h3>Messages / sec</h3><div class="now" id="cNowMsg">—</div><canvas id="chMsg"></canvas></div>
        <div class="chart"><h3>Bandwidth / sec</h3><div class="now" id="cNowBw">—</div><canvas id="chBw"></canvas></div>
      </div>
    </section>

    <section id="v-users" class="hide">
      <h1>Online Users</h1><p class="sub">Connected clients with live traffic. Send a message or disconnect.</p>
      <div class="panel"><div class="ph"><h2>Connections</h2><span id="uCount" class="pill">0</span><div class="sp"></div><button class="sm" onclick="loadUsers()">↻</button></div>
        <div class="tbl-wrap"><table><thead><tr><th>Connection ID</th><th>Node</th><th>State</th><th>Endpoint</th><th>IP</th><th>Msg ↑/↓</th><th>Bytes ↑/↓</th><th>Actions</th></tr></thead><tbody id="uBody"></tbody></table></div>
      </div>
    </section>

    <section id="v-routing" class="hide">
      <h1>Message Routing</h1><p class="sub">Connection→node routing links, endpoint targets, and node topology.</p>
      <div class="panel"><div class="ph"><h2>Node topology</h2><div class="sp"></div><button class="sm" onclick="loadRouting()">↻</button></div>
        <div class="topo" id="topo"></div></div>
      <div class="panel"><div class="ph"><h2>Connection routing table</h2><span id="rCount" class="pill">0</span></div>
        <div class="tbl-wrap"><table><thead><tr><th>Connection ID</th><th>→ Node</th><th>Endpoint target</th></tr></thead><tbody id="rBody"></tbody></table></div></div>
    </section>

    <section id="v-cluster" class="hide">
      <h1>WS Cluster</h1><p class="sub">Nodes, leader and Raft state (standalone shows a single self-leading node).</p>
      <div class="cards" id="clCards"></div>
      <div class="panel"><div class="ph"><h2>Nodes</h2><div class="sp"></div><button class="sm" onclick="loadCluster()">↻</button></div>
        <div class="tbl-wrap"><table><thead><tr><th>Node ID</th><th>State</th><th>Role</th><th>Connected</th><th>Connections</th><th>Term</th><th>Log</th></tr></thead><tbody id="clBody"></tbody></table></div></div>
    </section>

    <section id="v-endpoints" class="hide">
      <h1>Endpoints</h1><p class="sub">Discovered WebSocket MVC endpoints (message targets).</p>
      <div class="panel"><div class="ph"><h2>Endpoints</h2><span id="epCount" class="pill">0</span><div class="sp"></div><button class="sm" onclick="loadEndpoints()">↻</button></div>
        <div class="tbl-wrap"><table><thead><tr><th>Controller</th><th>Action</th><th>Target</th><th>Methods</th></tr></thead><tbody id="epBody"></tbody></table></div></div>
    </section>

    <section id="v-messages" class="hide">
      <h1>Messages</h1><p class="sub">Broadcast to all clients (management operation).</p>
      <div class="panel"><div class="ph"><h2>Broadcast</h2></div>
        <div style="padding:15px"><div class="field" style="margin-bottom:12px"><label>Message</label><textarea id="bcMsg" placeholder="Text or JSON…"></textarea></div>
          <button class="primary" onclick="doBroadcast()">Broadcast to all</button></div></div>
    </section>
  </main>
</div>

<div class="modal-bg" id="sendModal"><div class="modal">
  <h3 style="margin:0 0 14px">Send message to connection</h3>
  <div class="field" style="margin-bottom:10px"><label>Connection ID</label><input id="smId" class="mono" readonly></div>
  <div class="field" style="margin-bottom:10px"><label>Type</label><select id="smType"><option>Text</option><option>Binary</option></select></div>
  <div class="field" style="margin-bottom:16px"><label>Content</label><textarea id="smContent"></textarea></div>
  <div class="row" style="justify-content:flex-end"><button onclick="closeSend()">Cancel</button><button class="primary" onclick="doSend()">Send</button></div>
</div></div>
<div class="toast" id="toast"></div>

<script>
const BASE="{BASE}";
const api=(p,o)=>fetch(BASE+p,o).then(async r=>{const j=await r.json().catch(()=>({}));return{ok:r.ok,j};});
const $=s=>document.querySelector(s),el=(t,c,h)=>{const e=document.createElement(t);if(c)e.className=c;if(h!=null)e.innerHTML=h;return e;};
function toast(m,k){const t=el('div',k||'',m);$('#toast').appendChild(t);setTimeout(()=>t.remove(),3000);}
function esc(s){return String(s==null?'':s).replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));}
function fb(n){n=Number(n)||0;if(n<1024)return n.toFixed(0)+' B';if(n<1048576)return(n/1024).toFixed(1)+' KB';return(n/1048576).toFixed(2)+' MB';}
function num(n){return (Number(n)||0).toLocaleString();}

let view='overview';
$('#nav').addEventListener('click',e=>{const a=e.target.closest('a');if(!a)return;view=a.dataset.v;
  document.querySelectorAll('.nav a').forEach(x=>x.classList.toggle('active',x===a));
  document.querySelectorAll('main section').forEach(s=>s.classList.add('hide'));$('#v-'+view).classList.remove('hide');refreshAll();});

// ---- canvas line chart (no libs) ----
function drawChart(id,series,color){
  const c=$('#'+id);if(!c)return;const dpr=window.devicePixelRatio||1;
  const w=c.clientWidth,h=c.clientHeight;c.width=w*dpr;c.height=h*dpr;
  const g=c.getContext('2d');g.scale(dpr,dpr);g.clearRect(0,0,w,h);
  const pad=4,max=Math.max(1,...series);const n=series.length;
  // grid baseline
  g.strokeStyle='rgba(255,255,255,.06)';g.lineWidth=1;
  for(let i=0;i<=2;i++){const y=pad+(h-2*pad)*i/2;g.beginPath();g.moveTo(0,y);g.lineTo(w,y);g.stroke();}
  if(n<2)return;
  g.beginPath();
  for(let i=0;i<n;i++){const x=w*i/(n-1);const y=h-pad-(h-2*pad)*(series[i]/max);i?g.lineTo(x,y):g.moveTo(x,y);}
  g.strokeStyle=color;g.lineWidth=2;g.stroke();
  // fill
  g.lineTo(w,h);g.lineTo(0,h);g.closePath();g.fillStyle=color+'22';g.fill();
}

async function loadHealth(){
  const {ok,j}=await api('/health');const d=(ok&&j.success)?j.data:null;
  const hp=$('#healthPill');
  if(d){hp.querySelector('.dot').className='dot '+(d.isHealthy?'ok':'err');$('#healthTxt').textContent=d.isHealthy?'healthy':'unhealthy';
    $('#modePill').textContent=(d.details&&d.details.Mode)?('mode: '+d.details.Mode):'—';}
  else{hp.querySelector('.dot').className='dot err';$('#healthTxt').textContent='unreachable';}
  return d;
}
async function loadOverview(){
  const d=await loadHealth();
  const {j:cj}=await api('/client/count');const cnt=(cj&&cj.success)?cj.data:{total:0,local:0};
  const {j:bj}=await api('/statistics/bandwidth');const bw=(bj&&bj.success)?bj.data:{};
  const cards=[['Total connections',num(cnt.total)],['Local connections',num(cnt.local)],
    ['Msg sent /s',(bw.messagesSentPerSecond||0).toFixed(1)],['Msg recv /s',(bw.messagesReceivedPerSecond||0).toFixed(1)],
    ['Total messages',num((bw.totalMessagesSent||0)+ (bw.totalMessagesReceived||0))],
    ['Total traffic',fb((bw.totalBytesSent||0)+(bw.totalBytesReceived||0))],
    ['Nodes',d?d.totalNodes:'—'],['Mode',d&&d.details?d.details.Mode:'—']];
  const box=$('#ovCards');box.innerHTML='';
  cards.forEach(([k,v])=>{const c=el('div','card');c.innerHTML=`<div class="k">${k}</div><div class="v ${String(v).length>8?'sm':''}">${esc(v)}</div>`;box.appendChild(c);});
  // charts from time series
  const {j:tj}=await api('/statistics/timeseries');const hist=(tj&&tj.success&&tj.data)?tj.data:[];
  const conn=hist.map(s=>s.connections), msg=hist.map(s=>(s.messagesSentPerSecond||0)+(s.messagesReceivedPerSecond||0)), byt=hist.map(s=>(s.bytesSentPerSecond||0)+(s.bytesReceivedPerSecond||0));
  drawChart('chConn',conn,'#4f8cff');drawChart('chMsg',msg,'#31c48d');drawChart('chBw',byt,'#8b5cf6');
  $('#cNowConn').textContent='now: '+(conn.length?conn[conn.length-1]:0);
  $('#cNowMsg').textContent='now: '+(msg.length?msg[msg.length-1].toFixed(1):0)+' /s';
  $('#cNowBw').textContent='now: '+fb(byt.length?byt[byt.length-1]:0)+' /s';
}
async function loadUsers(){
  const {ok,j}=await api('/client');const list=(ok&&j.success&&j.data)?j.data:[];
  $('#uCount').textContent=list.length;const b=$('#uBody');b.innerHTML='';
  if(!list.length){b.innerHTML='<tr><td colspan="8"><div class="empty">No online users</div></td></tr>';return;}
  list.forEach(c=>{const tr=el('tr');tr.innerHTML=`<td class="mono">${esc(c.connectionId)}</td><td>${esc(c.nodeId)}</td>
    <td><span class="tag ${c.state==='Open'?'open':''}">${esc(c.state)}</span></td>
    <td class="mono">${esc(c.endpoint||'—')}</td><td class="mono">${esc(c.remoteIpAddress||'—')}</td>
    <td>${num(c.messagesSent)} / ${num(c.messagesReceived)}</td><td>${fb(c.bytesSent)} / ${fb(c.bytesReceived)}</td>
    <td><button class="sm" data-send="${esc(c.connectionId)}">Send</button> <button class="sm danger" data-kill="${esc(c.connectionId)}">Disconnect</button></td>`;b.appendChild(tr);});
  b.querySelectorAll('[data-send]').forEach(x=>x.onclick=()=>openSend(x.dataset.send));
  b.querySelectorAll('[data-kill]').forEach(x=>x.onclick=()=>killConn(x.dataset.kill));
}
async function loadRouting(){
  const {j:rj}=await api('/routes');const routes=(rj&&rj.success&&rj.data)?rj.data:{};
  const {j:ej}=await api('/endpoints');const eps=(ej&&ej.success&&ej.data)?ej.data:[];
  const {j:cj}=await api('/client');const clients=(cj&&cj.success&&cj.data)?cj.data:[];
  const epByConn={};clients.forEach(c=>{if(c.endpoint)epByConn[c.connectionId]=c.endpoint;});
  // topology: node -> count
  const byNode={};Object.values(routes).forEach(n=>byNode[n]=(byNode[n]||0)+1);
  const topo=$('#topo');topo.innerHTML='';
  const nodes=Object.keys(byNode);
  if(!nodes.length){topo.innerHTML='<div class="empty">No routes</div>';}
  nodes.forEach(n=>{const d=el('div','node');d.innerHTML=`<div class="id">${esc(n)}</div><div class="m"><span>connections</span><b>${byNode[n]}</b></div>`;topo.appendChild(d);});
  // routing table
  const entries=Object.entries(routes);$('#rCount').textContent=entries.length;
  const b=$('#rBody');b.innerHTML='';
  if(!entries.length){b.innerHTML='<tr><td colspan="3"><div class="empty">No routing links</div></td></tr>';return;}
  entries.forEach(([cid,node])=>{const tr=el('tr');tr.innerHTML=`<td class="mono">${esc(cid)}</td><td class="mono">${esc(node)}</td><td class="mono">${esc(epByConn[cid]||'—')}</td>`;b.appendChild(tr);});
}
async function loadCluster(){
  const {j:oj}=await api('/cluster/overview');const ov=(oj&&oj.success)?oj.data:null;
  const box=$('#clCards');box.innerHTML='';
  [['Total nodes',ov?ov.totalNodes:'—'],['Connected nodes',ov?ov.connectedNodes:'—'],['Leader',ov&&ov.isCurrentNodeLeader?(ov.currentNodeId+' (self)'):'—'],['Total connections',ov?num(ov.totalConnections):'—']]
    .forEach(([k,v])=>{const e=el('div','card');e.innerHTML=`<div class="k">${k}</div><div class="v ${String(v).length>8?'sm':''}">${esc(v)}</div>`;box.appendChild(e);});
  const {j:nj}=await api('/cluster/nodes');const nodes=(nj&&nj.success&&nj.data)?nj.data:[];
  const b=$('#clBody');b.innerHTML='';
  if(!nodes.length){b.innerHTML='<tr><td colspan="7"><div class="empty">No nodes</div></td></tr>';return;}
  nodes.forEach(n=>{const tr=el('tr');tr.innerHTML=`<td class="mono">${esc(n.nodeId)}</td>
    <td><span class="tag ${n.isConnected?'open':''}">${esc(n.state)}</span></td>
    <td>${n.isLeader?'<span class="tag leader">leader</span>':'follower'}</td>
    <td>${n.isConnected?'✓':'✗'}</td><td>${num(n.connectionCount)}</td><td>${esc(n.currentTerm??0)}</td><td>${esc(n.logLength??0)}</td>`;b.appendChild(tr);});
}
async function loadEndpoints(){
  const {ok,j}=await api('/endpoints');const list=(ok&&j.success&&j.data)?j.data:[];
  $('#epCount').textContent=list.length;const b=$('#epBody');b.innerHTML='';
  if(!list.length){b.innerHTML='<tr><td colspan="4"><div class="empty">No endpoints</div></td></tr>';return;}
  list.forEach(e=>{const tr=el('tr');tr.innerHTML=`<td>${esc(e.controller)}</td><td>${esc(e.action)}</td><td class="mono">${esc(e.target)}</td><td>${esc((e.methods||[]).join(', '))}</td>`;b.appendChild(tr);});
}

// ---- management ops ----
let sendId=null;
function openSend(id){sendId=id;$('#smId').value=id;$('#smContent').value='';$('#sendModal').classList.add('show');}
function closeSend(){$('#sendModal').classList.remove('show');}
async function doSend(){
  const {ok,j}=await api('/messages/send',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({connectionId:sendId,content:$('#smContent').value,messageType:$('#smType').value})});
  if(ok&&j.success){toast('Message sent','ok');closeSend();}else{toast('Send failed: '+((j&&j.error)||'error'),'err');}
}
async function killConn(id){if(!confirm('Disconnect '+id+'?'))return;
  const {ok,j}=await api('/client/'+encodeURIComponent(id),{method:'DELETE'});
  if(ok&&j.success){toast('Disconnected '+id,'ok');loadUsers();}else{toast('Disconnect failed: '+((j&&j.error)||'error'),'err');}}
async function doBroadcast(){const m=$('#bcMsg').value;if(!m){toast('Enter a message','err');return;}
  const {ok,j}=await api('/messages/broadcast',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({content:m,messageType:'Text'})});
  if(ok&&j.success){toast('Broadcast sent'+(j.data!=null?(' ('+j.data+')'):''),'ok');$('#bcMsg').value='';}else{toast('Broadcast failed: '+((j&&j.error)||'error'),'err');}}

function refreshAll(){loadHealth();
  if(view==='overview')loadOverview();else if(view==='users')loadUsers();else if(view==='routing')loadRouting();
  else if(view==='cluster')loadCluster();else if(view==='endpoints')loadEndpoints();}
let timer=null;
function setTimer(){if(timer)clearInterval(timer);const ms=+$('#interval').value;if(ms>0)timer=setInterval(refreshAll,ms);}
$('#interval').addEventListener('change',setTimer);
$('#sendModal').addEventListener('click',e=>{if(e.target.id==='sendModal')closeSend();});
window.addEventListener('resize',()=>{if(view==='overview')loadOverview();});
refreshAll();setTimer();
</script>
</body>
</html>
""";
    }
}
