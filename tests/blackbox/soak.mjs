// Soak monitor: samples /api/system/status and process CPU/RAM/handles once a minute into a CSV.
// usage: node soak.mjs <baseUrl> <dataDir> <pid> <minutes> <out.csv>
import fs from 'node:fs'; import path from 'node:path'; import { execFileSync } from 'node:child_process';
import { Client } from './client.mjs';
const [base, dataDir, pid, minutes, out] = process.argv.slice(2);
const pw = process.env.NW_PASSWORD;
const c = new Client(base);
async function ensureLogin() {
  const me = await c.get('/api/auth/me');
  if (me.status === 200) return;
  const r = await c.login('admin', pw);
  if (r.status !== 200) throw new Error('login ' + r.status + ' ' + r.text);
}
function proc() {
  const ps = `$p=Get-Process -Id ${pid}; '{0}|{1}|{2}|{3}' -f $p.TotalProcessorTime.TotalSeconds,$p.WorkingSet64,$p.PrivateMemorySize64,$p.HandleCount`;
  const [cpu, ws, priv, h] = execFileSync('powershell', ['-NoProfile', '-Command', ps]).toString().trim().split('|');
  return { cpu: +cpu, ws: +ws, priv: +priv, handles: +h };
}
const dbSize = f => ['', '-wal'].reduce((s, x) => s + (fs.existsSync(path.join(dataDir, f + x)) ? fs.statSync(path.join(dataDir, f + x)).size : 0), 0);
fs.writeFileSync(out, 'utc,elapsedMin,cpuPctOneCore,workingSetMB,privateMB,handles,totalPolls,failedPolls,overruns,inFlight,queued,avgPollMs,maxPollMs,pendingWrites,droppedSamples,alertQueued,alertDropped,ingestQueued,recvDropped,syslog,traps,dbFiles\n');
const start = Date.now(); let prev = null;
const dbs = fs.readdirSync(dataDir).filter(f => f.endsWith('.db'));
while ((Date.now() - start) / 60000 < +minutes) {
  try {
    await ensureLogin();
    const s = (await c.get('/api/system/status')).json;
    const p = proc(); const now = Date.now();
    const cpuPct = prev ? (100 * (p.cpu - prev.cpu) / ((now - prev.t) / 1000)) : 0;
    prev = { cpu: p.cpu, t: now };
    const dbBytes = fs.readdirSync(dataDir, { recursive: true }).filter(f => /\.db(-wal)?$/.test(f)).reduce((a, f) => a + fs.statSync(path.join(dataDir, f)).size, 0);
    fs.appendFileSync(out, [new Date().toISOString(), ((now - start) / 60000).toFixed(1), cpuPct.toFixed(1), (p.ws / 1048576).toFixed(0), (p.priv / 1048576).toFixed(0), p.handles,
      s.polling.totalPolls, s.polling.failedPolls, s.polling.overruns, s.polling.inFlight, s.polling.queued, s.polling.avgDurationMs, s.polling.maxDurationMs,
      s.metrics.pendingWrites, s.metrics.droppedSamples, s.alertEngine.queued, s.alertEngine.dropped, s.receivers.queued, s.receivers.dropped, s.receivers.syslogReceived, s.receivers.trapsReceived, dbBytes].join(',') + '\n');
  } catch (e) { fs.appendFileSync(out, `# ${new Date().toISOString()} error ${e.message}\n`); }
  await new Promise(r => setTimeout(r, 60000));
}
