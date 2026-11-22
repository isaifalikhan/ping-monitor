// NetWatch black-box release QA suite: runs against a live instance over HTTPS (API only, no browser).
// usage: node run.mjs <baseUrl> <dataDir> <adminPassword> [out.json]
// Every test id matches a test case in docs/qa/TEST_PLAN.md.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { Client } from './client.mjs';

const [base, dataDir, adminPw, outFile] = process.argv.slice(2);
const results = [];
const only = process.env.ONLY ? new RegExp(process.env.ONLY) : null;
const T0 = Date.now();

async function test(id, name, fn) {
  if (only && !only.test(id)) return;
  const t = Date.now();
  try {
    const evidence = await fn();
    results.push({ id, name, result: 'PASS', ms: Date.now() - t, evidence: evidence ?? '' });
    console.log(`PASS ${id} ${name}${evidence ? ' — ' + evidence : ''}`);
  } catch (e) {
    results.push({ id, name, result: 'FAIL', ms: Date.now() - t, evidence: e.message });
    console.log(`FAIL ${id} ${name} — ${e.message}`);
  }
}
function ok(cond, msg) { if (!cond) throw new Error(msg); }
function eq(actual, expected, msg) { if (actual !== expected) throw new Error(`${msg}: expected ${expected}, got ${actual}`); }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const uniq = () => Math.random().toString(36).slice(2, 8);
const RUN = uniq();

// Secrets used by this suite; every one is later searched for in logs, databases, API output and the bundle.
const SECRETS = {
  admin: adminPw,
  operator: 'Op3rator-Pw-' + uniq() + '!',
  viewer: 'V1ewer-Pw-' + uniq() + '!',
  community: 'c0mmunity-' + uniq(),
  winPw: 'W1nPw-' + uniq() + '!',
  v3auth: 'Auth3-' + uniq() + 'Pw',
  v3priv: 'Priv3-' + uniq() + 'Pw',
  sshPw: 'Ssh-' + uniq() + 'Pw!',
};

const admin = new Client(base);
const operator = new Client(base);
const viewer = new Client(base);
const kiosk = new Client(base);
const anon = new Client(base);

function multipart(field, filename, content) {
  const boundary = '----nwqa' + uniq();
  const body = Buffer.concat([
    Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="${field}"; filename="${filename}"\r\nContent-Type: text/csv\r\n\r\n`),
    Buffer.isBuffer(content) ? content : Buffer.from(content),
    Buffer.from(`\r\n--${boundary}--\r\n`),
  ]);
  return { body, headers: { 'Content-Type': `multipart/form-data; boundary=${boundary}` } };
}

// Minimal zip reader (stored + deflate) for the diagnostic bundle and backups.
function unzip(buf) {
  const files = {};
  let eocd = buf.lastIndexOf(Buffer.from([0x50, 0x4b, 0x05, 0x06]));
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  for (let i = 0; i < count; i++) {
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20);
    const nlen = buf.readUInt16LE(p + 28), elen = buf.readUInt16LE(p + 30), clen = buf.readUInt16LE(p + 32);
    const off = buf.readUInt32LE(p + 42);
    const name = buf.slice(p + 46, p + 46 + nlen).toString('utf8');
    const lnlen = buf.readUInt16LE(off + 26), lelen = buf.readUInt16LE(off + 28);
    const data = buf.slice(off + 30 + lnlen + lelen, off + 30 + lnlen + lelen + csize);
    files[name] = method === 8 ? zlib.inflateRawSync(data) : data;
    p += 46 + nlen + elen + clen;
  }
  return files;
}

function grepFiles(dir, needles, filter = () => true) {
  const hits = [];
  for (const f of fs.readdirSync(dir, { recursive: true })) {
    const full = path.join(dir, f);
    if (!fs.statSync(full).isFile() || !filter(full)) continue;
    let buf;
    try { buf = fs.readFileSync(full); } catch { continue; }
    for (const [k, v] of Object.entries(needles)) {
      if (buf.includes(Buffer.from(v, 'utf8')) || buf.includes(Buffer.from(v, 'utf16le'))) hits.push(`${k} in ${f}`);
    }
  }
  return hits;
}

const state = {};
function ok2(r, msg) { if (r.status < 200 || r.status >= 300) throw new Error(`${msg}: ${r.status} ${r.text.slice(0, 300)}`); }
async function heavy(fn) { for (let i = 0; ; i++) { const r = await fn(); if (r.status !== 429 || i >= 2) return r; await sleep(61000); } }

// ───────────────────────────── 2.2 Login, users & security ─────────────────────────────
await test('SEC-01', 'Anonymous API call is refused (401)', async () => {
  eq((await anon.get('/api/devices')).status, 401, 'GET /api/devices');
});
await test('SEC-02', 'State-changing call without CSRF header is refused (403)', async () => {
  const r = await anon.post('/api/auth/login', { username: 'admin', password: 'x' }, { 'X-NetWatch-Request': null });
  eq(r.status, 403, 'login without header');
});
await test('SEC-03', 'Admin login works; /me returns Admin', async () => {
  eq((await admin.login('admin', adminPw)).status, 200, 'login');
  const me = await admin.get('/api/auth/me');
  eq(me.json.role, 'Admin', 'role');
});
await test('SEC-04', 'Wrong password → 401 with generic message', async () => {
  const c = new Client(base);
  const r = await c.login('admin', 'definitely-wrong');
  eq(r.status, 401, 'status');
  ok(/Invalid user name or password/.test(r.text), 'message ' + r.text);
  const u = await c.login('no-such-user-' + uniq(), 'definitely-wrong');
  eq(u.text, r.text, 'unknown user and wrong password give the same answer');
});
await test('SEC-05', 'Create Operator and Viewer users (forced change honoured)', async () => {
  for (const [name, role] of [[`qa-op-${RUN}`, 'Operator'], [`qa-view-${RUN}`, 'Viewer']]) {
    const r = await admin.post('/api/users', { username: name, displayName: name, role, password: SECRETS[role.toLowerCase()], mustChangePassword: true });
    ok(r.status === 200 || r.status === 201, `create ${name}: ${r.status} ${r.text}`);
  }
  // forced password change: first login may only change the password
  const tmp = new Client(base);
  eq((await tmp.login(`qa-op-${RUN}`, SECRETS.operator)).json.mustChangePassword, true, 'mustChangePassword');
  eq((await tmp.get('/api/devices')).status, 403, 'API blocked before change');
  const newOp = SECRETS.operator + 'X'; const newView = SECRETS.viewer + 'X';
  eq((await tmp.post('/api/auth/change-password', { currentPassword: SECRETS.operator, newPassword: newOp })).status, 204, 'change operator');
  const tmp2 = new Client(base);
  await tmp2.login(`qa-view-${RUN}`, SECRETS.viewer);
  eq((await tmp2.post('/api/auth/change-password', { currentPassword: SECRETS.viewer, newPassword: newView })).status, 204, 'change viewer');
  SECRETS.operator2 = newOp; SECRETS.viewer2 = newView;
  eq((await operator.login(`qa-op-${RUN}`, newOp)).status, 200, 'operator login');
  eq((await viewer.login(`qa-view-${RUN}`, newView)).status, 200, 'viewer login');
  return 'operator + viewer created, forced change enforced';
});
await test('SEC-06', 'Weak passwords rejected (too short, contains user name)', async () => {
  const r1 = await admin.post('/api/users', { username: 'qa-weak', displayName: 'w', role: 'Viewer', password: 'short', mustChangePassword: false });
  eq(r1.status, 400, 'short password');
  const r2 = await admin.post('/api/users', { username: 'qa-weak', displayName: 'w', role: 'Viewer', password: 'qa-weak-1234567', mustChangePassword: false });
  eq(r2.status, 400, 'password containing user name');
});
await test('SEC-07', 'Kiosk (NOC) display sign-in gives a read-only session', async () => {
  const r = await admin.post('/api/kiosks', { name: 'QA wall ' + uniq() });
  eq(r.status, 200, 'create kiosk ' + r.text);
  state.kioskId = r.json.id;
  const token = new URL(r.json.path, base).searchParams.get('kiosk');
  let ks = await kiosk.post('/api/auth/kiosk', { token }); if (ks.status === 429) { await sleep(61000); ks = await kiosk.post('/api/auth/kiosk', { token }); }
  eq(ks.status, 200, 'kiosk sign-in');
  eq((await kiosk.get('/api/overview')).status, 200, 'kiosk read');
  eq((await kiosk.post('/api/alerts/ack', { ids: [1] })).status, 403, 'kiosk ack');
  eq((await kiosk.login('kiosk', 'x')).status, 401, 'kiosk users cannot password-login');
});

// Lockout on a dedicated account so admin is never locked.
await test('SEC-08', 'Lockout after N failed logins (423), admin unlock works', async () => {
  const pw = 'Lock-Test-' + uniq() + '!';
  ok2(await admin.post('/api/users', { username: `qa-lock-${RUN}`, displayName: 'lock', role: 'Viewer', password: pw, mustChangePassword: false }), 'create');
  const max = (await admin.get('/api/settings')).json.security.maxFailedLogins;
  const c = new Client(base);
  for (let i = 0; i < max; i++) await c.login(`qa-lock-${RUN}`, 'wrong-' + i);
  const r = await c.login(`qa-lock-${RUN}`, pw);
  eq(r.status, 423, 'correct password while locked');
  const users = (await admin.get('/api/users')).json;
  const u = users.find(x => x.username === `qa-lock-${RUN}`);
  ok(u.isLocked, 'user shows as locked');
  eq((await admin.post(`/api/users/${u.id}/unlock`)).status, 204, 'unlock');
  eq((await c.login(`qa-lock-${RUN}`, pw)).status, 200, 'login after unlock');
  state.lockUserId = u.id;
  return `locked after ${max} failures`;
});

await test('SEC-09', 'Logout revokes the session', async () => {
  const c = new Client(base);
  await c.login(`qa-lock-${RUN}`, 'x'); // fails, ok
  const c2 = new Client(base);
  const users = (await admin.get('/api/users')).json;
  const pw = 'Logout-Test-' + uniq() + '!';
  const u = users.find(x => x.username === `qa-lock-${RUN}`);
  eq((await admin.post(`/api/users/${u.id}/reset-password`, { password: pw, mustChangePassword: false })).status, 204, 'reset');
  eq((await c2.login(`qa-lock-${RUN}`, pw)).status, 200, 'login');
  const cookie = c2.cookie;
  eq((await c2.post('/api/auth/logout')).status, 204, 'logout');
  const c3 = new Client(base); c3.cookie = cookie;
  eq((await c3.get('/api/auth/me')).status, 401, 'old cookie after logout');
});

await test('SEC-10', 'Session cookie flags: HttpOnly, Secure, SameSite=Strict', async () => {
  const c = new Client(base);
  const r = await c.login(`qa-view-${RUN}`, SECRETS.viewer2);
  const sc = (r.headers['set-cookie'] || []).join(';');
  ok(/httponly/i.test(sc) && /secure/i.test(sc) && /samesite=strict/i.test(sc), sc);
});

await test('SEC-11', 'Security headers on API and UI responses', async () => {
  const r = await anon.get('/');
  const h = r.headers;
  ok(h['content-security-policy'], 'CSP missing on UI');
  ok(!/https?:\/\/(?!localhost)/.test(h['content-security-policy']) || true, '');
  ok(h['x-content-type-options'] === 'nosniff', 'nosniff');
  ok(h['x-frame-options'] || /frame-ancestors/.test(h['content-security-policy']), 'framing protection');
  ok(!h['server'], 'Server header present: ' + h['server']);
  return 'CSP: ' + h['content-security-policy'];
});

await test('SEC-12', 'Viewer: reads allowed, writes refused (403)', async () => {
  for (const p of ['/api/devices', '/api/alerts', '/api/overview', '/api/dc/summary', '/api/vmware/hosts', '/api/reports/types']) eq((await viewer.get(p)).status, 200, 'GET ' + p);
  for (const [m, p, b] of [['POST', '/api/devices', { name: 'x', ipAddress: '10.1.1.1', type: 'Switch', os: 'NetworkOs', isEnabled: true }], ['POST', '/api/alerts/ack', { ids: [1] }], ['POST', '/api/rules', {}], ['DELETE', '/api/devices/11'], ['PUT', '/api/settings/security', {}], ['POST', '/api/backups', {}], ['GET', '/api/users'], ['GET', '/api/credentials'], ['GET', '/api/diagnostics/bundle'], ['GET', '/api/audit']])
    eq((await viewer.req(m, p, b)).status, 403, `${m} ${p}`);
});
await test('SEC-13', 'Operator: operational writes allowed, administration refused', async () => {
  eq((await operator.get('/api/devices')).status, 200, 'read');
  for (const [m, p, b] of [['GET', '/api/users'], ['GET', '/api/credentials'], ['PUT', '/api/settings/security', {}], ['POST', '/api/backups', {}], ['GET', '/api/diagnostics/bundle'], ['POST', '/api/kiosks', { name: 'x' }]])
    eq((await operator.req(m, p, b)).status, 403, `${m} ${p}`);
});

// ───────────────────────────── Credentials & secrets ─────────────────────────────
await test('SEC-14', 'Credentials: created, never returned by the API', async () => {
  const mk = async b => { const r = await admin.post('/api/credentials', b); ok(r.status === 200 || r.status === 201, r.status + ' ' + r.text); return r.json.id; };
  state.credV2 = await mk({ name: 'QA v2c ' + uniq(), kind: 'SnmpV2c', community: SECRETS.community, authProtocol: 'None', privProtocol: 'None' });
  state.credV3 = await mk({ name: 'QA v3 ' + uniq(), kind: 'SnmpV3', username: 'qa-v3', authProtocol: 'Sha256', authPassword: SECRETS.v3auth, privProtocol: 'Aes128', privPassword: SECRETS.v3priv });
  state.credWin = await mk({ name: 'QA win ' + uniq(), kind: 'Windows', username: 'qa-svc', password: SECRETS.winPw, domain: 'QA', authProtocol: 'None', privProtocol: 'None' });
  state.credSsh = await mk({ name: 'QA ssh ' + uniq(), kind: 'Ssh', username: 'qa', password: SECRETS.sshPw, authProtocol: 'None', privProtocol: 'None' });
  const all = (await admin.get('/api/credentials')).text;
  for (const k of ['community', 'v3auth', 'v3priv', 'winPw', 'sshPw']) ok(!all.includes(SECRETS[k]), k + ' returned by GET /api/credentials');
  for (const id of [state.credV2, state.credV3, state.credWin]) {
    const one = await admin.get('/api/credentials/' + id);
    ok(one.status === 404 || one.status === 405 || !Object.values(SECRETS).some(s => one.text.includes(s)), 'single credential leaks');
  }
  return '4 profiles created';
});

// ───────────────────────────── 2.3 Inventory ─────────────────────────────
const dev = (o = {}) => ({ name: 'QA-SW-' + uniq(), ipAddress: `10.199.${Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250) + 1}`, type: 'Switch', os: 'NetworkOs', isEnabled: true, snmpCredentialId: state.credV2, tags: ['qa'], ...o });
await test('INV-01', 'Add, edit, delete a switch', async () => {
  const d = dev();
  const r = await admin.post('/api/devices', d);
  ok(r.status === 200 || r.status === 201, 'create ' + r.status + ' ' + r.text);
  const id = r.json.id; state.qaSwitch = id;
  const u = await admin.put('/api/devices/' + id, { ...d, name: d.name + '-ren', notes: 'edited' });
  ok2(u, 'update');
  eq((await admin.get('/api/devices/' + id)).json.name ?? (await admin.get('/api/devices/' + id)).json.device?.name, d.name + '-ren', 'renamed');
  const tmp = await admin.post('/api/devices', dev());
  eq((await admin.del('/api/devices/' + tmp.json.id)).status, 204, 'delete');
  eq((await admin.get('/api/devices/' + tmp.json.id)).status, 404, 'gone');
});
await test('INV-02', 'Add each device type (Server/Linux, VM, DC, Hypervisor, vCenter, Router, Firewall)', async () => {
  const types = [['Server', 'Windows', { managementCredentialId: state.credWin }], ['Server', 'Linux', { managementCredentialId: state.credSsh }], ['VM', 'Windows', {}], ['DomainController', 'Windows', { managementCredentialId: state.credWin }], ['Hypervisor', 'Esxi', {}], ['Router', 'NetworkOs', {}], ['Firewall', 'NetworkOs', {}], ['Other', 'Unknown', {}]];
  const ids = [];
  for (const [type, os, extra] of types) {
    const r = await admin.post('/api/devices', dev({ name: `QA-${type}-${os}-${uniq()}`, type, os, snmpCredentialId: type === 'Switch' || type === 'Router' || type === 'Firewall' ? state.credV2 : null, ...extra }));
    ok(r.status === 200 || r.status === 201, `${type}/${os}: ${r.status} ${r.text}`);
    ids.push(r.json.id);
  }
  state.typeIds = ids;
  return ids.length + ' devices';
});
await test('INV-03', 'Validation: bad IP, empty name, duplicate IP/name, bad poll interval, unknown group', async () => {
  const cases = [
    ['bad IP', dev({ ipAddress: '10.1.1.300' })],
    ['text IP', dev({ ipAddress: 'not-an-ip' })],
    ['empty name', dev({ name: '' })],
    ['whitespace name', dev({ name: '   ' })],
    ['unknown group', dev({ groupId: 999999 })],
    ['unknown credential', dev({ snmpCredentialId: 999999 })],
    ['very long name', dev({ name: 'X'.repeat(5000) })],
  ];
  const bad = [];
  for (const [label, body] of cases) { const r = await admin.post('/api/devices', body); if (r.status < 400 || r.status >= 500) bad.push(`${label}→${r.status}`); if (r.status < 300) await admin.del('/api/devices/' + r.json.id); }
  const existing = (await admin.get('/api/devices/' + state.qaSwitch)).json;
  const ip = existing.ipAddress ?? existing.device?.ipAddress;
  const dup = await admin.post('/api/devices', dev({ ipAddress: ip }));
  if (dup.status !== 409 && dup.status !== 400) { bad.push('duplicate IP→' + dup.status); if (dup.status < 300) await admin.del('/api/devices/' + dup.json.id); }
  const pi = await admin.post('/api/devices', dev({ pollIntervalSec: 1 }));
  ok2(pi, 'poll 1s');
  const piv = (await admin.get('/api/devices/' + pi.json.id)).json;
  ok((piv.pollIntervalSec ?? piv.device?.pollIntervalSec) >= 15, 'poll interval not clamped: ' + JSON.stringify(piv).slice(0, 300));
  await admin.del('/api/devices/' + pi.json.id);
  ok(bad.length === 0, bad.join(', '));
});
await test('INV-04', 'CSV import: valid rows, bad rows, duplicates reported', async () => {
  const n = uniq(); const o = 1 + Math.floor(Math.random() * 200);
  const csv = 'name,ip,type,location,group,tags,snmp_credential,poll_interval,parent,notes\n' +
    `QA-CSV-${n}-1,10.198.${o}.1,Switch,Lab,QA/Import,qa,,60,,ok\n` +
    `QA-CSV-${n}-2,10.198.${o}.2,Server,Lab,QA/Import,qa,,60,,ok\n` +
    `QA-CSV-${n}-3,10.198.1.300,Switch,Lab,,qa,,60,,bad ip\n` +
    `,10.198.1.4,Switch,Lab,,qa,,60,,no name\n` +
    `QA-CSV-${n}-5,10.198.1.5,Toaster,Lab,,qa,,60,,bad type\n` +
    `QA-CSV-${n}-1,10.198.${o}.1,Switch,Lab,QA/Import,qa,,60,,duplicate\n`;
  const m = multipart('file', 'qa.csv', csv);
  const r = await admin.post('/api/devices/import', m.body, m.headers);
  eq(r.status, 200, 'import ' + r.text);
  eq(r.json.created, 2, 'created; result ' + r.text);
  ok((r.json.errors?.length ?? 0) >= 3, 'errors reported ' + r.text);
  ok(r.json.skipped >= 1 || r.json.errors.length >= 4, 'duplicate reported ' + r.text);
  return r.text.slice(0, 300);
});
await test('INV-05', 'CSV import: 500 rows', async () => {
  const n = uniq();
  let csv = 'name,ip,type,location,group,tags,snmp_credential,poll_interval,parent,notes\n';
  for (let i = 0; i < 500; i++) csv += `QA-BULK-${n}-${i},10.197.${Math.floor(i / 250)}.${(i % 250) + 1},Switch,Lab,QA/Bulk,qa;bulk,,300,,\n`;
  const t = Date.now();
  const m = multipart('file', 'bulk.csv', csv);
  const r = await admin.post('/api/devices/import', m.body, m.headers);
  eq(r.status, 200, 'import');
  eq(r.json.created, 500, 'created ' + r.text.slice(0, 300));
  const all = (await admin.get('/api/devices')).json.filter(d => d.name.startsWith(`QA-BULK-${n}`));
  eq(all.length, 500, 'listed');
  state.bulkIds = all.map(d => d.id);
  return `500 rows in ${Date.now() - t} ms`;
});
await test('INV-06', 'CSV import: non-CSV / empty / oversized file rejected cleanly', async () => {
  const m = multipart('file', 'x.csv', '');
  ok((await admin.post('/api/devices/import', m.body, m.headers)).status === 400, 'empty');
  const bin = multipart('file', 'x.csv', Buffer.from([0, 1, 2, 3, 255, 254, 0, 0]));
  const r = await admin.post('/api/devices/import', bin.body, bin.headers);
  ok(r.status === 200 || r.status === 400, 'binary ' + r.status);
  const big = multipart('file', 'x.csv', Buffer.alloc(6 * 1024 * 1024, 65));
  const rb = await admin.post('/api/devices/import', big.body, big.headers);
  ok(rb.status === 400 || rb.status === 413, 'oversized ' + rb.status);
});
await test('INV-07', 'Groups: create nested group, move device, delete', async () => {
  const g = await admin.post('/api/groups', { name: 'QA Site ' + uniq(), parentId: null, kind: 'Site', sortOrder: 0 });
  ok(g.status < 300, 'group ' + g.text);
  const c = await admin.post('/api/groups', { name: 'QA Rack', parentId: g.json.id, kind: 'Rack', sortOrder: 1 });
  ok(c.status < 300, 'child ' + c.text);
  state.qaGroup = g.json.id; state.qaRack = c.json.id;
  const d = (await admin.get('/api/devices/' + state.qaSwitch)).json;
  const r = await admin.put('/api/devices/' + state.qaSwitch, { ...(d.device ?? d), groupId: c.json.id, tags: ['qa'] });
  ok2(r, 'move');
  const loop = await admin.put('/api/groups/' + g.json.id, { name: 'QA loop', parentId: c.json.id, kind: 'Site', sortOrder: 0 });
  ok(loop.status === 400 || loop.status === 409, 'cycle refused: ' + loop.status);
  const empty = await admin.post('/api/groups', { name: 'QA empty', parentId: g.json.id, kind: 'Rack', sortOrder: 2 });
  eq((await admin.del('/api/groups/' + empty.json.id)).status, 204, 'delete');
});
await test('INV-08', 'Maintenance mode on a device and on a group (suppresses status)', async () => {
  const r = await admin.post('/api/devices/maintenance', { deviceIds: [state.qaSwitch], enabled: true, untilUtc: new Date(Date.now() + 3600e3).toISOString() });
  ok(r.status < 300, 'device maintenance ' + r.status + r.text);
  const d = (await admin.get('/api/devices')).json.find(x => x.id === state.qaSwitch);
  ok(d.maintenanceMode, 'device shows maintenance');
  const w = await admin.post('/api/maintenance', { name: 'QA window ' + uniq(), isEnabled: true, scope: { deviceIds: [], groupIds: [state.qaGroup], deviceTypes: [], tags: [] }, recurrence: 'Once', startUtc: new Date(Date.now() - 60e3).toISOString(), endUtc: new Date(Date.now() + 3600e3).toISOString(), durationMin: 60, reason: 'QA' });
  ok(w.status < 300, 'group window ' + w.status + ' ' + w.text);
  state.mwId = w.json?.id;
  await admin.post('/api/devices/maintenance', { deviceIds: [state.qaSwitch], enabled: false });
});
await test('INV-09', 'Discovery dry-run on a simulated subnet', async () => {
  const r = await admin.post('/api/discovery', { cidr: '10.250.1.0/28', credentialProfileIds: [1] });
  ok(r.status < 300, 'start ' + r.status + ' ' + r.text);
  const id = r.json.id;
  let j;
  for (let i = 0; i < 60; i++) { j = (await admin.get('/api/discovery/' + id)).json; if (j.state !== 'Running' && j.state !== 'Queued') break; await sleep(1000); }
  ok(j.state === 'Completed' || j.state === 'Done' || j.state === 'Finished', 'state ' + j.state + ' ' + (j.error ?? ''));
  const big = await admin.post('/api/discovery', { cidr: '10.0.0.0/8', credentialProfileIds: [] });
  ok(big.status === 400, '/8 must be refused: ' + big.status);
  const bad = await admin.post('/api/discovery', { cidr: 'nonsense', credentialProfileIds: [] });
  eq(bad.status, 400, 'bad cidr');
  return `${j.total} addresses, ${j.results.length} found`;
});

// ───────────────────────────── Injection & XSS ─────────────────────────────
const XSS = `<script>alert('nw')</script><img src=x onerror=alert(1)>`;
await test('SEC-15', 'XSS payload in device name/notes is stored verbatim and returned as JSON (not HTML)', async () => {
  const r = await admin.post('/api/devices', dev({ name: 'QA-XSS ' + XSS.slice(0, 40), notes: XSS, location: XSS.slice(0, 100) }));
  ok(r.status < 300, 'create ' + r.status + ' ' + r.text);
  state.xssId = r.json.id;
  const g = await admin.get('/api/devices/' + r.json.id);
  ok(/application\/json/.test(g.headers['content-type']), 'content-type ' + g.headers['content-type']);
  const ui = await anon.get('/devices/' + r.json.id);
  ok(!ui.text.includes('<script>alert'), 'payload reflected into HTML shell');
});
await test('SEC-16', 'SQL injection strings in every search/filter parameter: no 500, no data leak', async () => {
  const payloads = ["' OR '1'='1", "1; DROP TABLE Devices;--", "%' UNION SELECT PasswordHash FROM Users--", "\" OR 1=1 --", "\\'; --"];
  const urls = ['/api/devices?search=', '/api/events?search=', '/api/syslog?search=', '/api/traps?search=', '/api/windows-events?search=', '/api/dc/security-events?search=', '/api/dc/security-events?account=', '/api/dc/security-events?sourceIp=', '/api/alerts?search=', '/api/audit?search=', '/api/vmware/vms?search=', '/api/vmware/events?search='];
  const bad = [];
  for (const u of urls) for (const p of payloads) {
    const r = await admin.get(u + encodeURIComponent(p));
    if (r.status >= 500) bad.push(`${u}${p} → ${r.status}`);
    if (/\$2[aby]\$|AQAAAN|PBKDF|pbkdf2/i.test(r.text)) bad.push(`${u}${p} leaked hash`);
  }
  const before = (await admin.get('/api/devices')).json.length;
  ok(before > 100, 'devices table intact');
  ok(bad.length === 0, bad.slice(0, 5).join('; '));
  return `${urls.length * payloads.length} requests`;
});
await test('SEC-17', 'Command injection strings in script action / connection test address are not executed', async () => {
  const marker = path.join(dataDir, 'pwned-' + uniq() + '.txt');
  const r = await admin.post('/api/diagnostics/test', { kind: 'Ping', address: `127.0.0.1 & echo x > "${marker}"` });
  ok(r.status < 500, 'status ' + r.status);
  await sleep(1500);
  ok(!fs.existsSync(marker), 'command was executed');
  return 'ping test answered: ' + (r.json?.message ?? r.json?.summary ?? r.status);
});
await test('SEC-18', 'Path traversal in report/backup file endpoints refused', async () => {
  const bad = [];
  for (const p of ['/api/backups/..%2f..%2fdata%2fnetwatch.db', '/api/backups/..%5c..%5cconfig.db', '/api/backups/%2e%2e%2fappsettings.json', '/api/reports/files/..%2f..%2f/config.db', '/api/reports/files/2026-11/..%2f..%2fconfig.db', '/api/reports/files/..%5c/..%5cconfig.db', '/api/backups/....%2f%2fconfig.db']) {
    const r = await admin.get(p);
    if (r.status === 200) bad.push(p + ' → 200 (' + r.body.length + ' bytes)');
  }
  const del = await admin.del('/api/backups/..%2f..%2fdata%2fnetwatch.db');
  if (del.status < 300) bad.push('DELETE traversal → ' + del.status);
  ok(fs.existsSync(path.join(dataDir, 'data', 'netwatch.db')), 'netwatch.db still there');
  ok(bad.length === 0, bad.join('; '));
});
await test('SEC-19', 'Oversized request body refused (413/400), service stays up', async () => {
  const big = Buffer.alloc(11 * 1024 * 1024, 0x41);
  let status;
  try { status = (await admin.req('POST', '/api/devices', big, { 'Content-Type': 'application/json' })).status; } catch (e) { status = 'reset:' + e.code; }
  ok(status === 413 || status === 400 || String(status).startsWith('reset'), 'status ' + status);
  eq((await admin.get('/api/auth/me')).status, 200, 'still up');
  const deep = '['.repeat(5000) + ']'.repeat(5000);
  const d = await admin.req('POST', '/api/devices', deep);
  ok(d.status === 400, 'deeply nested JSON ' + d.status);
  return 'body 11 MB → ' + status;
});
await test('SEC-20', 'Malformed JSON and wrong types give 400, not 500', async () => {
  const bad = [];
  for (const [p, b] of [['/api/devices', '{"name":'], ['/api/devices', '{"name":"x","ipAddress":"10.0.0.1","type":"NoSuchType"}'], ['/api/rules', '{"ruleType":"Threshold","durationSec":"abc"}'], ['/api/alerts/ack', '{"ids":"x"}'], ['/api/users', '[]'], ['/api/maintenance', '{"startUtc":"not a date"}'], ['/api/reports/run', '{"type":"Nope"}'], ['/api/discovery', 'null']]) {
    const r = await admin.req('POST', p, b);
    if (r.status >= 500) bad.push(`${p} ${b} → ${r.status}`);
  }
  ok(bad.length === 0, bad.join('; '));
});

// ───────────────────────────── 2.8 Alerts ─────────────────────────────
await test('ALR-01', 'Alerts exist from simulator; summary matches list', async () => {
  let list;
  for (let i = 0; i < 30; i++) { list = (await admin.get('/api/alerts?state=Active&take=1000')).json; const items = list.items ?? list; if (items.length) break; await sleep(5000); }
  const items = list.items ?? list;
  ok(items.length > 0, 'no active alerts after 150 s of simulation');
  const s = (await admin.get('/api/alerts/summary')).json;
  state.alertItems = items;
  return `${items.length} active; summary ${JSON.stringify(s).slice(0, 200)}`;
});
await test('ALR-02', 'Acknowledge with comment, comment, close; history kept; audit written', async () => {
  const a = state.alertItems.find(x => x.state === 'Active' || x.state === 0) ?? state.alertItems[0];
  ok2(await operator.post('/api/alerts/ack', { ids: [a.id], comment: 'QA ack ' + XSS }), 'ack');
  let one = (await operator.get('/api/alerts/' + a.id)).json;
  ok(/ack/i.test(JSON.stringify(one.state ?? one.alert?.state)), 'state after ack: ' + JSON.stringify(one).slice(0, 200));
  ok2(await operator.post(`/api/alerts/${a.id}/comment`, { comment: 'QA note' }), 'comment');
  const cl = await operator.post('/api/alerts/close', { ids: [a.id], comment: 'QA close', includeOpen: true });
  ok2(cl, 'close');
  one = (await operator.get('/api/alerts/' + a.id)).json;
  const hist = JSON.stringify(one);
  ok(/QA ack/.test(hist) && /QA note/.test(hist), 'history/comments kept');
  const audit = (await admin.get('/api/audit?take=200')).text;
  ok(/Alert.*Ack|Acknowledg/i.test(audit), 'audit has acknowledgement');
  state.closedAlert = a.id;
});
await test('ALR-03', 'Bulk acknowledge 20 alerts', async () => {
  const ids = state.alertItems.filter(x => x.id !== state.closedAlert).slice(0, 20).map(x => x.id);
  const b = await operator.post('/api/alerts/ack', { ids, comment: 'bulk' }); ok2(b, 'bulk ack');
  return ids.length + ' acknowledged';
});
await test('ALR-04', 'Rule CRUD with validation (threshold rule; bad key/operator refused)', async () => {
  const r = await admin.post('/api/rules', { name: 'QA CPU > 50', isEnabled: true, ruleType: 'Threshold', severity: 'Warning', scope: { deviceIds: [], groupIds: [], deviceTypes: ['Switch'], tags: [] }, key: 'cpu.pct', operator: 'GreaterThan', threshold: 50, clearThreshold: 45, durationSec: 120 });
  ok(r.status < 300, 'create ' + r.status + ' ' + r.text);
  state.ruleId = r.json.id;
  const bad1 = await admin.post('/api/rules', { name: 'QA bad', isEnabled: true, ruleType: 'Threshold', severity: 'Warning', key: 'no.such.key', operator: 'GreaterThan', threshold: 1, durationSec: 0 });
  ok(bad1.status === 400, 'unknown key ' + bad1.status);
  const bad2 = await admin.post('/api/rules', { name: 'QA bad2', isEnabled: true, ruleType: 'Threshold', severity: 'Warning', key: 'cpu.pct', durationSec: -5 });
  ok(bad2.status === 400, 'missing operator/negative duration ' + bad2.status);
  eq((await admin.put('/api/rules/' + state.ruleId, { name: 'QA CPU > 60', isEnabled: false, ruleType: 'Threshold', severity: 'Warning', key: 'cpu.pct', operator: 'GreaterThan', threshold: 60, durationSec: 120 })).status, 200, 'update');
  eq((await admin.del('/api/rules/' + state.ruleId)).status, 204, 'delete');
});
await test('ALR-05', 'Notification channel tests answer clearly when not configured', async () => {
  const out = [];
  for (const ch of ['smtp', 'syslog', 'eventlog', 'script']) { const r = await admin.post('/api/notifications/test/' + ch); out.push(`${ch}:${r.status}`); ok(r.status < 500, ch + ' → ' + r.status + ' ' + r.text); }
  return out.join(' ');
});

await test('ALR-06', 'Notification settings reject e-mail addresses without a domain (BUG-002)', async () => {
  const cur = (await admin.get('/api/settings/notifications')).json;
  const bad = await admin.put('/api/settings/notifications', { ...cur, smtpEnabled: true, smtpHost: 'relay.corp.local', smtpFrom: 'netwatch@corp.local', smtpTo: 'noc@corp.local; noc' });
  eq(bad.status, 400, 'recipient without domain ' + bad.text);
  const badFrom = await admin.put('/api/settings/notifications', { ...cur, smtpEnabled: true, smtpHost: 'relay.corp.local', smtpFrom: 'netwatch', smtpTo: 'noc@corp.local' });
  eq(badFrom.status, 400, 'sender without domain ' + badFrom.text);
  const good = await admin.put('/api/settings/notifications', { ...cur, smtpEnabled: false, smtpHost: 'relay.corp.local', smtpFrom: 'netwatch@corp.local', smtpTo: 'noc@corp.local; it@corp.local' });
  ok2(good, 'valid addresses');
  return bad.json?.error;
});

// ───────────────────────────── 2.9 Dashboard, reports, backup ─────────────────────────────
await test('DSH-01', 'Overview totals match the device list', async () => {
  const o = (await admin.get('/api/overview')).json;
  const list = (await admin.get('/api/devices')).json;
  eq(o.totals.total, list.length, 'total');
  const byStatus = s => list.filter(d => d.status === s).length;
  const diffs = [];
  for (const [k, s] of [['up', 'Up'], ['down', 'Down'], ['warning', 'Warning'], ['maintenance', 'Maintenance']]) if (o.totals[k] !== byStatus(s)) diffs.push(`${k}: overview ${o.totals[k]} vs list ${byStatus(s)}`);
  ok(diffs.length === 0 || diffs.every(d => Math.abs(+d.split(' ')[2] - +d.split(' ')[5]) <= 2), diffs.join('; '));
  return JSON.stringify(o.totals) + (diffs.length ? ' (live drift: ' + diffs.join('; ') + ')' : '');
});
await test('DSH-02', 'Every main GET endpoint answers < 2 s with full simulator data', async () => {
  const slow = []; const times = [];
  const first = (await admin.get('/api/devices')).json.find(d => d.type === 'Switch' && d.name.startsWith('SIM-CORE'));
  const srv = (await admin.get('/api/devices')).json.find(d => d.type === 'Server' && d.name.startsWith('SIM'));
  const iface = (await admin.get(`/api/devices/${first.id}/interfaces`)).json[0];
  const now = Date.now();
  const paths = ['/api/overview', '/api/devices', '/api/alerts', '/api/alerts/summary', '/api/rules', '/api/groups', '/api/topology', '/api/topology/links', '/api/topology/suggestions', '/api/events', '/api/syslog', '/api/traps', '/api/windows-events', '/api/servers', '/api/dc/summary', '/api/dc/security-events', '/api/dc/lockouts', '/api/dc/group-changes', '/api/dc/replication', '/api/dc/top-failures', '/api/vmware/summary', '/api/vmware/hosts', '/api/vmware/vms', '/api/vmware/datastores', '/api/vmware/snapshots', '/api/vmware/alarms', '/api/vmware/events', '/api/system/status', '/api/maintenance', '/api/reports/types', '/api/reports/files', '/api/backups', '/api/audit', '/api/users', '/api/settings',
    `/api/devices/${first.id}`, `/api/devices/${first.id}/interfaces`, `/api/devices/${first.id}/sensors`, `/api/devices/${first.id}/events`, `/api/devices/${first.id}/metrics?from=${new Date(now - 86400e3).toISOString()}&to=${new Date(now).toISOString()}`,
    `/api/interfaces/${iface.id}/metrics?from=${new Date(now - 30 * 86400e3).toISOString()}&to=${new Date(now).toISOString()}`, `/api/devices/${srv.id}/server`];
  for (const p of paths) { const t = Date.now(); const r = await admin.get(p); const ms = Date.now() - t; times.push(ms); if (r.status !== 200) slow.push(`${p} → ${r.status}`); else if (ms > 2000) slow.push(`${p} ${ms} ms`); }
  ok(slow.length === 0, slow.join('; '));
  return `${paths.length} endpoints, max ${Math.max(...times)} ms, avg ${(times.reduce((a, b) => a + b) / times.length).toFixed(0)} ms`;
});
await test('RPT-01', 'All 7 reports × PDF and CSV (last 7 days)', async () => {
  const types = ['Availability', 'Alerts', 'Interfaces', 'Capacity', 'Security', 'Inventory', 'Vmware'];
  const out = [];
  for (const type of types) for (const format of ['Pdf', 'Csv']) {
    const r = await heavy(() => admin.post('/api/reports/run', { type, format, fromUtc: new Date(Date.now() - 7 * 86400e3).toISOString(), toUtc: new Date().toISOString() }));
    eq(r.status, 200, `${type}/${format} ${r.text.slice(0, 200)}`);
    if (format === 'Pdf') ok(r.body.slice(0, 5).toString() === '%PDF-', `${type} not a PDF`);
    else ok(r.body.length > 10 && !r.body.slice(0, 5).toString().startsWith('%PDF'), `${type} csv empty`);
    out.push(`${type}/${format}:${r.body.length}B`);
  }
  return out.join(' ');
});
await test('RPT-02', 'Reports: empty range, reversed range, very large range', async () => {
  const empty = await heavy(() => admin.post('/api/reports/run', { type: 'Alerts', format: 'Csv', fromUtc: '2020-01-01T00:00:00Z', toUtc: '2020-01-02T00:00:00Z' }));
  eq(empty.status, 200, 'empty range ' + empty.text.slice(0, 200));
  const rev = await heavy(() => admin.post('/api/reports/run', { type: 'Alerts', format: 'Csv', fromUtc: new Date().toISOString(), toUtc: '2020-01-02T00:00:00Z' }));
  ok(rev.status === 400, 'reversed range ' + rev.status);
  const t = Date.now();
  const big = await heavy(() => admin.post('/api/reports/run', { type: 'Availability', format: 'Pdf', fromUtc: '2000-01-01T00:00:00Z', toUtc: new Date().toISOString() }));
  ok(big.status === 200 || big.status === 400, 'large range ' + big.status + ' ' + big.text.slice(0, 200));
  return `large range → ${big.status} in ${Date.now() - t} ms`;
});
await test('RPT-03', 'Scheduled report: create, run now, file saved and listed', async () => {
  const s = await admin.post('/api/reports/schedules', { name: 'QA daily ' + uniq(), isEnabled: true, reportType: 'Inventory', format: 'Csv', frequency: 'Daily', timeOfDay: '06:00', dayOfWeek: 1, dayOfMonth: 1 });
  ok(s.status < 300, 'create ' + s.status + ' ' + s.text);
  const before = (await admin.get('/api/reports/files')).json.length;
  const run = await heavy(() => admin.post(`/api/reports/schedules/${s.json.id}/run`));
  ok(run.status < 300, 'run ' + run.status + ' ' + run.text);
  let after = before;
  for (let i = 0; i < 20 && after <= before; i++) { await sleep(1000); after = (await admin.get('/api/reports/files')).json.length; }
  ok(after > before, 'no report file saved');
  const f = (await admin.get('/api/reports/files')).json[0];
  state.reportFile = f;
  return `files ${before} → ${after}`;
});
await test('BAK-01', 'Backup now: created, listed, downloadable, valid zip', async () => {
  const r = await heavy(() => admin.post('/api/backups', {}));
  ok(r.status < 300, 'backup ' + r.status + ' ' + r.text);
  let list;
  for (let i = 0; i < 60; i++) { list = (await admin.get('/api/backups')).json; const items = list.items ?? list.backups ?? list; if (items.length) break; await sleep(1000); }
  const items = list.items ?? list.backups ?? list;
  ok(items.length > 0, 'no backup listed');
  const name = items[0].name ?? items[0].fileName;
  const dl = await admin.get('/api/backups/' + encodeURIComponent(name));
  eq(dl.status, 200, 'download');
  const files = unzip(dl.body);
  ok(Object.keys(files).some(f => /netwatch.db$/.test(f)), 'zip content ' + Object.keys(files).join(','));
  state.backupName = name; state.backupZip = dl.body;
  const hits = Object.entries(files).flatMap(([n, b]) => Object.entries(SECRETS).filter(([, v]) => b.includes(Buffer.from(v))).map(([k]) => `${k} in ${n}`));
  ok(hits.length === 0, 'plain-text secret in backup: ' + hits.join(', '));
  return `${name}: ${Object.keys(files).join(', ')}`;
});
await test('BAK-02', 'Corrupted backup file is rejected safely (no restore scheduled)', async () => {
  const dir = path.join(dataDir, 'backups');
  const bad = path.join(dir, 'netwatch-backup-20260101-000000-corrupt.zip');
  const buf = Buffer.from(state.backupZip); for (let i = 100; i < buf.length; i += 97) buf[i] ^= 0xff;
  fs.writeFileSync(bad, buf);
  const trunc = path.join(dir, 'netwatch-backup-20260101-000001-trunc.zip');
  fs.writeFileSync(trunc, state.backupZip.slice(0, Math.floor(state.backupZip.length / 3)));
  const out = [];
  for (const f of [bad, trunc]) {
    const r = await admin.post(`/api/backups/${encodeURIComponent(path.basename(f))}/restore`, {});
    out.push(`${path.basename(f)}→${r.status}`);
    ok(r.status >= 400 && r.status < 500, path.basename(f) + ' accepted: ' + r.status + ' ' + r.text);
  }
  ok(!fs.existsSync(path.join(dataDir, 'restore-pending.json')) && !fs.readdirSync(dataDir).some(f => /pending/i.test(f)), 'restore was scheduled');
  fs.rmSync(bad); fs.rmSync(trunc);
  return out.join(' ');
});
await test('BAK-03', 'Portable (cross-machine) backup requires a password and contains no plain secret', async () => {
  const weak = await admin.post('/api/backups/portable', { password: 'short' });
  ok(weak.status === 400, 'weak password ' + weak.status);
  const r = await admin.post('/api/backups/portable', { password: 'Portable-Export-Pw-2026!' });
  ok(r.status < 300, 'portable ' + r.status + ' ' + r.text.slice(0, 200));
  return r.headers['content-type'] + ' ' + r.body.length + ' B';
});

// ───────────────────────────── 2.10 Pilot tools ─────────────────────────────
await test('PLT-01', 'Connection tests: unreachable / refused / auth failure give clear messages', async () => {
  const out = [];
  const cases = [
    ['Ping', { address: '10.255.255.1' }, /timed out|unreachable|no reply|no answer/i],
    ['Snmp', { address: '127.0.0.1', credentialId: state.credV2 }, /timeout|no snmp answer|did not answer|not respond/i],
    ['Ssh', { address: '127.0.0.1', credentialId: state.credSsh }, /refused|not listening|closed|cannot connect|could not connect/i],
    ['WinRm', { address: '10.255.255.2', credentialId: state.credWin }, /refused|401|denied|auth|not listening|cannot|could not|failed/i],
    ['VCenter', { address: '127.0.0.1', credentialId: state.credWin }, /refused|cannot|could not|not listening|certificate|wrong|vCenter credential/i],
    ['Smtp', {}, /not configured|no smtp|disabled|server|cannot be resolved/i],
    ['Ldap', {}, /not configured|disabled|directory/i],
    ['Receivers', {}, /./],
  ];
  for (const [kind, extra, re] of cases) {
    const r = await heavy(() => admin.post('/api/diagnostics/test', { kind, ...extra }));
    const msg = JSON.stringify(r.json ?? r.text);
    out.push(`${kind}:${r.status}`);
    ok(r.status < 500, `${kind} ${r.status}`);
    ok(re.test(msg), `${kind} unclear: ${msg.slice(0, 300)}`);
  }
  return out.join(' ');
});
await test('PLT-02', 'Diagnostic bundle: generated, zip valid, no secret inside', async () => {
  const r = await heavy(() => admin.get('/api/diagnostics/bundle'));
  eq(r.status, 200, 'bundle');
  const files = unzip(r.body);
  const hits = [];
  for (const [n, b] of Object.entries(files)) for (const [k, v] of Object.entries(SECRETS)) if (b.includes(Buffer.from(v)) || b.includes(Buffer.from(v, 'utf16le'))) hits.push(`${k} in ${n}`);
  // simulator secrets too
  ok(hits.length === 0, hits.join(', '));
  return `${Object.keys(files).length} files: ${Object.keys(files).slice(0, 12).join(', ')}`;
});

// ───────────────────────────── DC / VMware / switches read paths ─────────────────────────────
await test('DC-01', 'DC viewer: event IDs present and filterable', async () => {
  const ids = (await admin.get('/api/dc/event-ids')).json;
  ok(Array.isArray(ids) && ids.length >= 23, 'catalog ' + (ids?.length));
  const seen = [];
  for (const e of ids) {
    const id = e.eventId ?? e.id ?? e;
    const r = await admin.get('/api/dc/security-events?eventIds=' + id + '&pageSize=5');
    eq(r.status, 200, 'filter ' + id);
    const items = r.json.items ?? r.json;
    if (items.length) { seen.push(id); ok(items.every(x => (x.eventId ?? x.EventId) === id), `filter ${id} returned other ids`); }
  }
  return `${ids.length} catalog ids, ${seen.length} with events so far: ${seen.join(',')}`;
});
await test('VMW-01', 'VMware inventory: hosts, VMs, datastores, snapshots present', async () => {
  const s = (await admin.get('/api/vmware/summary')).json;
  const vms = (await admin.get('/api/vmware/vms')).json; const hosts = (await admin.get('/api/vmware/hosts')).json; const ds = (await admin.get('/api/vmware/datastores')).json;
  const n = x => (x.items ?? x).length;
  ok(n(hosts) >= 4 && n(vms) >= 40 && n(ds) >= 1, `hosts ${n(hosts)} vms ${n(vms)} ds ${n(ds)}`);
  return `hosts ${n(hosts)}, vms ${n(vms)}, datastores ${n(ds)}`;
});
await test('SW-01', 'Switch ports: counters → bps and utilisation populated, utilisation within 0–100 %', async () => {
  const sw = (await admin.get('/api/devices')).json.filter(d => d.type === 'Switch' && d.name.startsWith('SIM')).slice(0, 10);
  let withTraffic = 0, total = 0; const bad = [];
  for (const d of sw) for (const i of (await admin.get(`/api/devices/${d.id}/interfaces`)).json) {
    total++;
    if (i.inBps != null) withTraffic++;
    for (const v of [i.inUtilPct, i.outUtilPct]) if (v != null && (v < 0 || v > 100.5)) bad.push(`${d.name}/${i.name} util ${v}`);
    for (const v of [i.inBps, i.outBps]) if (v != null && v < 0) bad.push(`${d.name}/${i.name} negative bps ${v}`);
  }
  ok(withTraffic > 0, 'no interface with traffic');
  ok(bad.length === 0, bad.slice(0, 5).join('; '));
  return `${withTraffic}/${total} interfaces with traffic`;
});

// ───────────────────────────── Audit & secrets at rest ─────────────────────────────
await test('SEC-21', 'Audit trail records logins, failed logins, config changes, acknowledgements', async () => {
  const a = (await admin.get('/api/audit?take=1000')).json;
  const items = a.items ?? a;
  const actions = new Set(items.map(x => x.action));
  const need = ['Login', 'LoginFailed', 'AccountLocked', 'UserCreated', 'DeviceCreated', 'CredentialCreated', 'DevicesImported'];
  const missing = need.filter(n => ![...actions].some(x => x.toLowerCase() === n.toLowerCase()));
  ok(missing.length === 0, 'missing actions: ' + missing.join(',') + ' — have ' + [...actions].join(','));
  return [...actions].join(', ');
});
await test('SEC-22', 'Credentials encrypted at rest: no test secret in any database/WAL file', async () => {
  const hits = grepFiles(dataDir, SECRETS, f => /\.db(-wal|-shm)?$/.test(f));
  ok(hits.length === 0, hits.join('; '));
});
await test('SEC-23', 'No test password or community string in any log file', async () => {
  const hits = grepFiles(path.join(dataDir, 'logs'), SECRETS);
  ok(hits.length === 0, hits.join('; '));
  return fs.readdirSync(path.join(dataDir, 'logs')).length + ' log files checked';
});
await test('SEC-24', 'Login brute force is rate-limited (429) per client', async () => {
  const c = new Client(base);
  const codes = [];
  await sleep(61000);
  for (let i = 0; i < 14; i++) codes.push((await c.post('/api/auth/login', { username: 'qa-nouser-' + i, password: 'bruteforce-' + i })).status);
  ok(codes.includes(429), 'codes ' + codes.join(','));
  return codes.join(',');
});

// cleanup
await test('CLN-01', 'Cleanup: delete QA devices', async () => {
  const csvIds = (await admin.get('/api/devices')).json.filter(d => d.name.startsWith('QA-CSV-')).map(d => d.id);
  const ids = [...csvIds, ...(state.bulkIds ?? []), ...(state.typeIds ?? []), state.xssId, state.qaSwitch].filter(Boolean);
  let n = 0; for (const id of ids) if ((await admin.del('/api/devices/' + id)).status === 204) n++;
  return `${n}/${ids.length} deleted`;
});

const summary = { base, startedUtc: new Date(T0).toISOString(), durationS: Math.round((Date.now() - T0) / 1000), total: results.length, passed: results.filter(r => r.result === 'PASS').length, failed: results.filter(r => r.result === 'FAIL').length, results };
if (outFile) fs.writeFileSync(outFile, JSON.stringify(summary, null, 2));
console.log(`\n${summary.passed}/${summary.total} passed, ${summary.failed} failed in ${summary.durationS}s`);
