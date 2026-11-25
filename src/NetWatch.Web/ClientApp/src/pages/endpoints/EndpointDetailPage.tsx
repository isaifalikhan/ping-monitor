import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, RotateCcw, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import type { EndpointChange, EndpointDetail, EndpointEventRow, EndpointReport, EpConnection, EpHotfix, EpPort, EpProcess, EpService, EpSoftware, EpUsb } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { SeverityBadge, UtilBar } from '../../components/Badges';
import { DataTable, type Column } from '../../components/DataTable';
import { Confirm, ErrorBox } from '../../components/Modal';
import { fmtAgo, fmtBytes, fmtDateTime, fmtDuration, toMs } from '../../util/format';
import { daysSince, ipText, problems, shortOs, YesNo } from './common';
import { ChangeTable } from './EndpointsPage';

type Tab = 'overview' | 'processes' | 'services' | 'software' | 'updates' | 'network' | 'users' | 'usb' | 'startup' | 'changes' | 'events';
const TABS: [Tab, string][] = [
  ['overview', 'Overview'],
  ['processes', 'Processes'],
  ['services', 'Services'],
  ['software', 'Software'],
  ['updates', 'Updates'],
  ['network', 'Network'],
  ['users', 'Users & admins'],
  ['usb', 'USB'],
  ['startup', 'Startup & shares'],
  ['changes', 'Changes'],
  ['events', 'Events'],
];

/** One machine's latest inventory, its change history and its forwarded events. */
export function EndpointDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const client = useQueryClient();
  const { can } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) || 'overview';
  const [confirm, setConfirm] = useState<'reset' | 'delete' | null>(null);
  const q = useQuery({ queryKey: ['endpoint', id], queryFn: () => api.get<EndpointDetail>(`/api/endpoints/${id}`), refetchInterval: 60000 });
  const reset = useMutation({ mutationFn: () => api.post(`/api/endpoints/${id}/reset`), onSuccess: () => { setConfirm(null); client.invalidateQueries({ queryKey: ['endpoint', id] }); } });
  const remove = useMutation({ mutationFn: () => api.del(`/api/endpoints/${id}`), onSuccess: () => { client.invalidateQueries({ queryKey: ['endpoints'] }); navigate('/endpoints'); } });

  if (q.isLoading) return <div className="empty">Loading…</div>;
  if (!q.data) return <ErrorBox error={q.error ?? new Error('Not found')} />;
  const { endpoint: e, device, report: r } = q.data;
  const issues = problems(e);

  return (
    <>
      <div className="page-head">
        <Link to="/endpoints" className="btn ghost" title="Back to endpoints">
          <ArrowLeft size={16} />
        </Link>
        <h1>{e.name}</h1>
        <span className="mono muted">{ipText(e.ip)}</span>
        {e.stale ? <span className="badge s-unknown">not reporting</span> : issues.length > 0 ? <span className="badge s-warning">problems</span> : <span className="badge s-up">OK</span>}
        {e.worstAlert && <SeverityBadge severity={e.worstAlert} />}
        <span className="spacer" />
        {device && (
          <Link className="btn" to={`/devices/${device.id}`}>
            Device page
          </Link>
        )}
        {can('Admin') && (
          <>
            <button className="btn" onClick={() => setConfirm('reset')} title="Forget the machine's token: its next report must carry the enrollment key">
              <RotateCcw size={14} /> Reset enrollment
            </button>
            <button className="btn danger" onClick={() => setConfirm('delete')}>
              <Trash2 size={14} /> Remove
            </button>
          </>
        )}
      </div>

      <div className="grid cols-4" style={{ marginBottom: 16 }}>
        <div className="card stat">
          <div className="label">Last inventory</div>
          <div className="value" style={{ fontSize: 22, color: e.stale ? 'var(--warn)' : undefined }}>{fmtAgo(e.lastSeenUtc)}</div>
          <div className="breakdown muted">
            {fmtDateTime(e.lastSeenUtc)} · since {fmtDateTime(e.firstSeenUtc)}
          </div>
        </div>
        <div className="card stat">
          <div className="label">User</div>
          <div className="value" style={{ fontSize: 18 }}>{e.user ?? '—'}</div>
          <div className="breakdown muted">{e.domain ?? 'workgroup'}</div>
        </div>
        <div className="card stat">
          <div className="label">Operating system</div>
          <div className="value" style={{ fontSize: 18 }}>{shortOs(e.os)}</div>
          <div className="breakdown muted">
            build {e.osBuild ?? '?'} {r?.system.osDisplayVersion ? `· ${r.system.osDisplayVersion}` : ''}
          </div>
        </div>
        <div className="card stat">
          <div className="label">Problems</div>
          <div className="value" style={{ fontSize: 18, color: issues.length ? 'var(--crit)' : 'var(--ok)' }}>{issues.length ? issues.length : 'None'}</div>
          <div className="breakdown muted">{issues.join(' · ')}</div>
        </div>
      </div>

      {e.errors && (
        <div className="card" style={{ marginBottom: 16, borderColor: 'var(--warn)' }}>
          <div className="card-body muted">
            <strong style={{ color: 'var(--warn)' }}>Not collected on the machine:</strong> {e.errors}
          </div>
        </div>
      )}

      <div className="tabs">
        {TABS.map(([k, label]) => (
          <button key={k} className={tab === k ? 'active' : ''} onClick={() => setParams(k === 'overview' ? {} : { tab: k }, { replace: true })}>
            {label}
            {k === 'processes' && r ? ` (${r.processes.length})` : ''}
            {k === 'software' && r ? ` (${r.software.length})` : ''}
          </button>
        ))}
      </div>

      {!r && tab !== 'changes' && tab !== 'events' ? (
        <div className="empty">No inventory stored yet.</div>
      ) : (
        <>
          {tab === 'overview' && r && <Overview r={r} />}
          {tab === 'processes' && r && <Processes rows={r.processes} />}
          {tab === 'services' && r && <Services rows={r.services} />}
          {tab === 'software' && r && <Software rows={r.software} />}
          {tab === 'updates' && r && <Updates r={r} />}
          {tab === 'network' && r && <Network r={r} />}
          {tab === 'users' && r && <Users r={r} />}
          {tab === 'usb' && r && <Usb rows={r.usb} />}
          {tab === 'startup' && r && <Startup r={r} />}
          {tab === 'changes' && <Changes id={e.id} />}
          {tab === 'events' && <Events id={e.id} hasDevice={!!device} />}
        </>
      )}

      {confirm === 'reset' && (
        <Confirm
          title={`Reset enrollment of ${e.name}?`}
          message="HIT SIEM forgets this machine's token. Its next report must carry the enrollment key (from hitsiem-endpoint.json) and gets a new token. Use it after reinstalling the machine."
          confirmLabel="Reset"
          busy={reset.isPending}
          onConfirm={() => reset.mutate()}
          onClose={() => setConfirm(null)}
        />
      )}
      {confirm === 'delete' && (
        <Confirm
          title={`Remove ${e.name}?`}
          message="Deletes the inventory and change history of this machine (and its Workstation device, if it was created from the inventory). If the GPO still runs the script on it, the machine enrolls again: remove it from the GPO first."
          confirmLabel="Remove"
          danger
          busy={remove.isPending}
          onConfirm={() => remove.mutate()}
          onClose={() => setConfirm(null)}
        />
      )}
    </>
  );
}

function Overview({ r }: { r: EndpointReport }) {
  const s = r.system;
  const memUsed = s.memoryTotalBytes && s.memoryFreeBytes != null ? Math.round((100 * (s.memoryTotalBytes - s.memoryFreeBytes)) / s.memoryTotalBytes) : null;
  const av = r.antivirus;
  return (
    <div className="grid cols-2" style={{ gap: 16 }}>
      <div className="card">
        <div className="card-head">
          <h3>Hardware</h3>
        </div>
        <div className="card-body">
          <dl className="kv">
            <dt>Model</dt>
            <dd>
              {s.manufacturer} {s.model}
            </dd>
            <dt>Serial number</dt>
            <dd className="mono">{s.serialNumber ?? '—'}</dd>
            <dt>BIOS</dt>
            <dd>{s.biosVersion ?? '—'}</dd>
            <dt>CPU</dt>
            <dd>
              {s.cpuName ?? '—'} <span className="muted">{s.cpuCores ? `${s.cpuCores} cores / ${s.cpuLogical} threads` : ''}</span>
            </dd>
            <dt>CPU load</dt>
            <dd style={{ maxWidth: 200 }}>
              <UtilBar value={s.cpuLoadPct} />
            </dd>
            <dt>Memory</dt>
            <dd>
              {fmtBytes(s.memoryTotalBytes)} <span className="muted">{memUsed != null ? `· ${memUsed} % used` : ''}</span>
            </dd>
            <dt>Secure Boot / TPM</dt>
            <dd>
              <YesNo value={s.secureBoot} /> / <YesNo value={s.tpmPresent} />
            </dd>
          </dl>
        </div>
      </div>
      <div className="card">
        <div className="card-head">
          <h3>Operating system</h3>
        </div>
        <div className="card-body">
          <dl className="kv">
            <dt>OS</dt>
            <dd>
              {s.osName} <span className="muted">{s.osArchitecture}</span>
            </dd>
            <dt>Version</dt>
            <dd>
              {s.osVersion} · build {s.osBuild} {s.osDisplayVersion ? `(${s.osDisplayVersion})` : ''}
            </dd>
            <dt>Role</dt>
            <dd>{s.productType === 1 ? 'Workstation' : s.productType === 2 ? 'Domain controller' : s.productType === 3 ? 'Server' : '—'}</dd>
            <dt>Domain</dt>
            <dd>{s.partOfDomain ? s.domain : 'not domain-joined (workgroup)'}</dd>
            <dt>Installed</dt>
            <dd>{s.installDateUtc ? fmtDateTime(s.installDateUtc) : '—'}</dd>
            <dt>Last boot</dt>
            <dd>
              {s.lastBootUtc ? `${fmtDateTime(s.lastBootUtc)} (up ${fmtDuration((Date.now() - toMs(s.lastBootUtc)) / 1000)})` : '—'}
            </dd>
            <dt>Time zone</dt>
            <dd>{s.timeZone ?? '—'}</dd>
            <dt>Collected</dt>
            <dd className="muted">
              script {r.scriptVersion} in {r.durationSec ?? '?'} s
            </dd>
          </dl>
        </div>
      </div>
      <div className="card">
        <div className="card-head">
          <h3>Firewall</h3>
        </div>
        <div className="card-body flush">
          <table className="data">
            <thead>
              <tr>
                <th>Profile</th>
                <th>State</th>
                <th>Inbound</th>
                <th>Outbound</th>
              </tr>
            </thead>
            <tbody>
              {r.firewall.map((f) => (
                <tr key={f.name ?? ''}>
                  <td>{f.name}</td>
                  <td>{f.enabled ? <span className="badge s-up">on</span> : f.enabled === false ? <span className="badge s-down">off</span> : '—'}</td>
                  <td className="muted">{f.defaultInbound ?? '—'}</td>
                  <td className="muted">{f.defaultOutbound ?? '—'}</td>
                </tr>
              ))}
              {r.firewall.length === 0 && (
                <tr>
                  <td colSpan={4} className="faint">
                    Not collected
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
      <div className="card">
        <div className="card-head">
          <h3>Antivirus</h3>
        </div>
        <div className="card-body">
          {!av ? (
            <span className="faint">Not collected</span>
          ) : (
            <dl className="kv">
              {av.products.map((p) => (
                <FragmentKv key={p.name ?? ''} label={p.name ?? 'Product'}>
                  {p.enabled ? <span className="badge s-up">enabled</span> : <span className="badge s-down">disabled</span>} {p.upToDate === false && <span className="badge s-warning">out of date</span>}
                </FragmentKv>
              ))}
              {av.defender && (
                <>
                  <dt>Defender service</dt>
                  <dd>
                    <YesNo value={av.defender.serviceEnabled} />
                  </dd>
                  <dt>Real-time protection</dt>
                  <dd>
                    <YesNo value={av.defender.realTimeProtection} />
                  </dd>
                  <dt>Signatures</dt>
                  <dd>
                    {av.defender.signatureVersion} <span className="muted">{av.defender.signatureAgeDays != null ? `· ${av.defender.signatureAgeDays} days old` : ''}</span>
                  </dd>
                  <dt>Tamper protection</dt>
                  <dd>
                    <YesNo value={av.defender.tamperProtected} />
                  </dd>
                  <dt>Last quick scan</dt>
                  <dd>{av.defender.lastQuickScanUtc ? fmtAgo(av.defender.lastQuickScanUtc) : '—'}</dd>
                </>
              )}
            </dl>
          )}
        </div>
      </div>
      <div className="card" style={{ gridColumn: '1 / -1' }}>
        <div className="card-head">
          <h3>Disks</h3>
        </div>
        <div className="card-body flush">
          <table className="data">
            <thead>
              <tr>
                <th>Drive</th>
                <th>Label</th>
                <th>File system</th>
                <th>Size</th>
                <th>Free</th>
                <th style={{ width: 160 }}>Used</th>
                <th>BitLocker</th>
              </tr>
            </thead>
            <tbody>
              {r.disks.map((d) => (
                <tr key={d.drive ?? ''}>
                  <td className="mono">{d.drive}</td>
                  <td>{d.label}</td>
                  <td className="muted">{d.fileSystem}</td>
                  <td>{fmtBytes(d.sizeBytes)}</td>
                  <td>{fmtBytes(d.freeBytes)}</td>
                  <td>
                    <UtilBar value={d.sizeBytes ? (100 * (d.sizeBytes - (d.freeBytes ?? 0))) / d.sizeBytes : null} />
                  </td>
                  <td>{d.bitLocker === 'On' ? <span className="badge s-up">on</span> : d.bitLocker === 'Off' ? <span className="badge s-warning">off</span> : <span className="faint">—</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

function FragmentKv({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt>{label}</dt>
      <dd>{children}</dd>
    </>
  );
}

function Filtered<T>({ rows, columns, rowKey, placeholder, match, initialSort }: { rows: T[]; columns: Column<T>[]; rowKey: (r: T, i: number) => string | number; placeholder: string; match: (r: T) => string; initialSort?: { key: string; desc?: boolean } }) {
  const [term, setTerm] = useState('');
  const keyed = useMemo(() => rows.map((r, i) => ({ r, k: rowKey(r, i) })), [rows, rowKey]);
  const t = term.trim().toLowerCase();
  const shown = t ? keyed.filter((x) => match(x.r).toLowerCase().includes(t)) : keyed;
  const cols: Column<{ r: T; k: string | number }>[] = columns.map((c) => ({ ...c, render: (x) => c.render(x.r), sort: c.sort ? (x) => c.sort!(x.r) : undefined }));
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input type="search" placeholder={placeholder} value={term} onChange={(e) => setTerm(e.target.value)} style={{ minWidth: 300 }} />
        <span className="muted">
          {shown.length} of {rows.length}
        </span>
      </div>
      <div className="card">
        <DataTable rows={shown} columns={cols} rowKey={(x) => x.k} initialSort={initialSort} maxHeight="calc(100vh - 420px)" empty="Nothing." />
      </div>
    </>
  );
}

function Processes({ rows }: { rows: EpProcess[] }) {
  const columns: Column<EpProcess>[] = [
    { key: 'name', header: 'Process', render: (p) => <span className="mono">{p.name}</span>, sort: (p) => p.name ?? '' },
    { key: 'pid', header: 'PID', render: (p) => p.pid, sort: (p) => p.pid, className: 'num', width: 80 },
    { key: 'ppid', header: 'Parent', render: (p) => p.parentPid ?? '', sort: (p) => p.parentPid ?? 0, className: 'num', width: 80 },
    { key: 'user', header: 'User', render: (p) => <span className="muted">{p.user ?? '—'}</span>, sort: (p) => p.user ?? '' },
    { key: 'mem', header: 'Memory', render: (p) => fmtBytes(p.memoryBytes), sort: (p) => p.memoryBytes ?? 0, className: 'num', width: 100 },
    { key: 'cpu', header: 'CPU time', render: (p) => (p.cpuSec != null ? fmtDuration(p.cpuSec) : '—'), sort: (p) => p.cpuSec ?? 0, className: 'num', width: 100 },
    { key: 'start', header: 'Started', render: (p) => (p.startUtc ? fmtAgo(p.startUtc) : '—'), sort: (p) => p.startUtc ?? '', width: 110 },
    { key: 'company', header: 'Company', render: (p) => <span className="muted">{p.company ?? ''}</span>, sort: (p) => p.company ?? '' },
    { key: 'cmd', header: 'Command line / path', render: (p) => <span className="mono muted" title={p.commandLine ?? p.path ?? ''}>{p.commandLine ?? p.path ?? ''}</span> },
  ];
  return <Filtered rows={rows} columns={columns} rowKey={(p, i) => `${p.pid}-${i}`} placeholder="Filter name, user, path, command line…" match={(p) => `${p.name} ${p.user} ${p.path} ${p.commandLine} ${p.company}`} initialSort={{ key: 'mem', desc: true }} />;
}

function Services({ rows }: { rows: EpService[] }) {
  const columns: Column<EpService>[] = [
    { key: 'name', header: 'Service', render: (s) => <strong>{s.displayName ?? s.name}</strong>, sort: (s) => s.displayName ?? s.name ?? '' },
    { key: 'key', header: 'Name', render: (s) => <span className="mono muted">{s.name}</span>, sort: (s) => s.name ?? '' },
    { key: 'state', header: 'State', render: (s) => <span className={`badge ${s.state === 'Running' ? 's-up' : s.startMode === 'Auto' ? 's-down' : 's-unknown'}`}>{s.state}</span>, sort: (s) => s.state ?? '' },
    { key: 'mode', header: 'Start', render: (s) => s.startMode, sort: (s) => s.startMode ?? '' },
    { key: 'acct', header: 'Account', render: (s) => <span className="muted">{s.account}</span>, sort: (s) => s.account ?? '' },
    { key: 'path', header: 'Path', render: (s) => <span className="mono muted" title={s.path ?? ''}>{s.path}</span> },
  ];
  return <Filtered rows={rows} columns={columns} rowKey={(s, i) => `${s.name}-${i}`} placeholder="Filter services…" match={(s) => `${s.name} ${s.displayName} ${s.path} ${s.account}`} initialSort={{ key: 'name' }} />;
}

function Software({ rows }: { rows: EpSoftware[] }) {
  const columns: Column<EpSoftware>[] = [
    { key: 'name', header: 'Program', render: (s) => <strong>{s.name}</strong>, sort: (s) => s.name ?? '' },
    { key: 'version', header: 'Version', render: (s) => <span className="mono">{s.version}</span>, sort: (s) => s.version ?? '' },
    { key: 'publisher', header: 'Publisher', render: (s) => <span className="muted">{s.publisher}</span>, sort: (s) => s.publisher ?? '' },
    { key: 'date', header: 'Installed', render: (s) => installDate(s.installDate), sort: (s) => s.installDate ?? '' },
    { key: 'scope', header: 'For', render: (s) => <span className="muted">{s.scope === 'Machine' ? 'all users' : s.scope?.replace(/^User:/, '')}</span>, sort: (s) => s.scope ?? '' },
    { key: 'arch', header: 'Arch', render: (s) => <span className="muted">{s.arch}</span>, sort: (s) => s.arch ?? '' },
  ];
  return <Filtered rows={rows} columns={columns} rowKey={(s, i) => `${s.name}-${i}`} placeholder="Filter programs or publishers…" match={(s) => `${s.name} ${s.publisher} ${s.version}`} initialSort={{ key: 'name' }} />;
}

function installDate(d?: string | null) {
  if (!d) return <span className="faint">—</span>;
  const m = /^(\d{4})(\d{2})(\d{2})$/.exec(d);
  return m ? `${m[1]}-${m[2]}-${m[3]}` : d;
}

function Updates({ r }: { r: EndpointReport }) {
  const p = r.patches;
  const hotfixColumns: Column<EpHotfix>[] = [
    { key: 'id', header: 'Update', render: (h) => <span className="mono">{h.id}</span>, sort: (h) => h.id ?? '' },
    { key: 'desc', header: 'Type', render: (h) => h.description, sort: (h) => h.description ?? '' },
    { key: 'date', header: 'Installed', render: (h) => (h.installedUtc ? fmtDateTime(h.installedUtc).slice(0, 10) : '—'), sort: (h) => h.installedUtc ?? '' },
  ];
  const age = daysSince(r.hotfixes.map((h) => h.installedUtc).filter(Boolean).sort().pop() ?? p?.lastInstallUtc);
  return (
    <div className="grid" style={{ gap: 16 }}>
      <div className="card">
        <div className="card-head">
          <h3>Patch state</h3>
        </div>
        <div className="card-body">
          {!p ? (
            <span className="faint">Not collected</span>
          ) : (
            <dl className="kv">
              <dt>Missing</dt>
              <dd>
                {p.missingSecurity == null ? (
                  <span className="faint">unknown</span>
                ) : (
                  <>
                    <span style={{ color: (p.missingSecurity ?? 0) + (p.missingCritical ?? 0) > 0 ? 'var(--crit)' : 'var(--ok)' }}>
                      {p.missingSecurity} security, {p.missingCritical} critical
                    </span>{' '}
                    <span className="muted">· {p.missingOther} other</span>
                  </>
                )}
              </dd>
              <dt>Last update installed</dt>
              <dd>
                {p.lastInstallUtc ? fmtDateTime(p.lastInstallUtc) : '—'} {age != null && <span className="muted" style={{ color: age > 45 ? 'var(--warn)' : undefined }}>({age} days ago)</span>}
              </dd>
              <dt>Last Windows Update scan</dt>
              <dd>{p.lastSearchUtc ? fmtDateTime(p.lastSearchUtc) : '—'}</dd>
              <dt>Update source</dt>
              <dd>{p.wsusServer ?? 'Windows Update (no WSUS policy)'}</dd>
              <dt>Reboot pending</dt>
              <dd>
                <YesNo value={p.pendingReboot} good={false} />
              </dd>
              {p.error && (
                <>
                  <dt>Error</dt>
                  <dd style={{ color: 'var(--warn)' }}>{p.error}</dd>
                </>
              )}
            </dl>
          )}
          <p className="muted" style={{ marginBottom: 0 }}>
            “Missing” is the result of the machine's own last Windows Update / WSUS scan, read offline; it is only as current as that scan.
          </p>
        </div>
      </div>
      {p && p.missing.length > 0 && (
        <div className="card">
          <div className="card-head">
            <h3>Missing updates ({p.missing.length})</h3>
          </div>
          <div className="card-body flush">
            <table className="data">
              <thead>
                <tr>
                  <th>Update</th>
                  <th>KB</th>
                  <th>Category</th>
                  <th>Severity</th>
                </tr>
              </thead>
              <tbody>
                {p.missing.map((m, i) => (
                  <tr key={i}>
                    <td>{m.title}</td>
                    <td className="mono">{m.kb}</td>
                    <td>{m.category}</td>
                    <td>{m.severity ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <div className="card">
        <div className="card-head">
          <h3>Installed updates ({r.hotfixes.length})</h3>
        </div>
        <DataTable rows={r.hotfixes} columns={hotfixColumns} rowKey={(h) => h.id ?? ''} initialSort={{ key: 'date', desc: true }} maxHeight="50vh" empty="None reported." />
      </div>
    </div>
  );
}

function Network({ r }: { r: EndpointReport }) {
  const listenColumns: Column<EpPort>[] = [
    { key: 'proto', header: 'Protocol', render: (p) => p.protocol, sort: (p) => p.protocol ?? '', width: 90 },
    { key: 'addr', header: 'Address', render: (p) => <span className="mono">{p.address}</span>, sort: (p) => p.address ?? '' },
    { key: 'port', header: 'Port', render: (p) => p.port, sort: (p) => p.port, className: 'num', width: 90 },
    { key: 'proc', header: 'Process', render: (p) => <span className="mono">{p.process ?? p.pid}</span>, sort: (p) => p.process ?? '' },
  ];
  const connColumns: Column<EpConnection>[] = [
    { key: 'local', header: 'Local', render: (c) => <span className="mono">{c.local}:{c.localPort}</span>, sort: (c) => `${c.local}:${c.localPort}` },
    { key: 'remote', header: 'Remote', render: (c) => <span className="mono">{c.remote}:{c.remotePort}</span>, sort: (c) => `${c.remote}:${c.remotePort}` },
    { key: 'state', header: 'State', render: (c) => c.state, sort: (c) => c.state ?? '' },
    { key: 'proc', header: 'Process', render: (c) => <span className="mono">{c.process ?? c.pid}</span>, sort: (c) => c.process ?? '' },
  ];
  return (
    <div className="grid" style={{ gap: 16 }}>
      <div className="card">
        <div className="card-head">
          <h3>Adapters</h3>
        </div>
        <div className="card-body flush">
          <table className="data">
            <thead>
              <tr>
                <th>Adapter</th>
                <th>MAC</th>
                <th>IP addresses</th>
                <th>Gateway</th>
                <th>DNS</th>
                <th>DHCP</th>
              </tr>
            </thead>
            <tbody>
              {r.adapters.map((a, i) => (
                <tr key={i}>
                  <td>{a.description}</td>
                  <td className="mono">{a.mac}</td>
                  <td className="mono">{a.ips.join(', ')}</td>
                  <td className="mono">{a.gateways.join(', ')}</td>
                  <td className="mono">{a.dns.join(', ')}</td>
                  <td>
                    <YesNo value={a.dhcp} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
      <div className="grid cols-2" style={{ gap: 16 }}>
        <div className="card">
          <div className="card-head">
            <h3>Listening ports ({r.listening.length})</h3>
          </div>
          <DataTable rows={r.listening} columns={listenColumns} rowKey={(p) => `${p.protocol}-${p.address}-${p.port}`} initialSort={{ key: 'port' }} maxHeight="50vh" empty="None reported." />
        </div>
        <div className="card">
          <div className="card-head">
            <h3>Established connections ({r.connections.length})</h3>
          </div>
          <DataTable rows={r.connections} columns={connColumns} rowKey={(c) => `${c.local}-${c.localPort}-${c.remote}-${c.remotePort}`} initialSort={{ key: 'remote' }} maxHeight="50vh" empty="None reported (or not collected)." />
        </div>
      </div>
    </div>
  );
}

function Users({ r }: { r: EndpointReport }) {
  return (
    <div className="grid cols-2" style={{ gap: 16 }}>
      <SimpleTable title="Logged-on users" head={['User', 'Session', 'State', 'Logon']} rows={r.sessions.map((s) => [s.user, s.session, s.state, s.logonUtc ? fmtDateTime(s.logonUtc) : ''])} />
      <SimpleTable title="Local administrators" head={['Member', 'Type', 'Source']} rows={r.localAdmins.map((a) => [a.name, a.type, a.source])} />
      <SimpleTable
        title="Local user accounts"
        head={['User', 'Enabled', 'Password required']}
        rows={r.localUsers.map((u) => [u.name, <YesNo key="e" value={u.enabled} />, <YesNo key="p" value={u.passwordRequired} />])}
      />
    </div>
  );
}

function Usb({ rows }: { rows: EpUsb[] }) {
  const columns: Column<EpUsb>[] = [
    { key: 'name', header: 'Device', render: (u) => <strong>{u.name ?? '—'}</strong>, sort: (u) => u.name ?? '' },
    { key: 'kind', header: 'Kind', render: (u) => (u.storage ? <span className="badge s-warning">storage</span> : <span className="muted">{u.class}</span>), sort: (u) => (u.storage ? 0 : 1) },
    { key: 'present', header: 'Now', render: (u) => (u.present ? <span className="badge s-up">connected</span> : <span className="muted">history</span>), sort: (u) => (u.present ? 0 : 1) },
    { key: 'serial', header: 'Serial', render: (u) => <span className="mono">{u.serial}</span>, sort: (u) => u.serial ?? '' },
    { key: 'id', header: 'Device ID', render: (u) => <span className="mono muted" title={u.deviceId ?? ''}>{u.deviceId}</span> },
  ];
  return (
    <>
      <p className="muted" style={{ marginTop: 0 }}>
        Connected USB devices, plus every USB storage device this machine has ever seen (registry history), so sticks plugged in and out between two inventories are listed too.
      </p>
      <Filtered rows={rows} columns={columns} rowKey={(u, i) => `${u.deviceId}-${i}`} placeholder="Filter USB devices…" match={(u) => `${u.name} ${u.deviceId} ${u.class}`} initialSort={{ key: 'kind' }} />
    </>
  );
}

function Startup({ r }: { r: EndpointReport }) {
  return (
    <div className="grid" style={{ gap: 16 }}>
      <SimpleTable title="Startup programs" head={['Name', 'Command', 'Location', 'User']} rows={r.startup.map((s) => [s.name, <span key="c" className="mono muted">{s.command}</span>, s.location, s.user])} />
      <SimpleTable title="Shared folders" head={['Share', 'Path', 'Description']} rows={r.shares.map((s) => [s.name, <span key="p" className="mono">{s.path}</span>, s.description])} />
    </div>
  );
}

function SimpleTable({ title, head, rows }: { title: string; head: string[]; rows: React.ReactNode[][] }) {
  return (
    <div className="card">
      <div className="card-head">
        <h3>
          {title} ({rows.length})
        </h3>
      </div>
      <div className="card-body flush">
        <table className="data">
          <thead>
            <tr>
              {head.map((h) => (
                <th key={h}>{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr key={i}>
                {row.map((c, j) => (
                  <td key={j}>{c ?? ''}</td>
                ))}
              </tr>
            ))}
            {rows.length === 0 && (
              <tr>
                <td colSpan={head.length} className="faint">
                  None
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function Changes({ id }: { id: number }) {
  const [days, setDays] = useState(90);
  const q = useQuery({ queryKey: ['endpoint-changes-one', id, days], queryFn: () => api.get<EndpointChange[]>(`/api/endpoints/${id}/changes?days=${days}`) });
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
          {[7, 30, 90, 180, 365].map((d) => (
            <option key={d} value={d}>
              Last {d} days
            </option>
          ))}
        </select>
      </div>
      <ErrorBox error={q.error} />
      <div className="card">
        <ChangeTable rows={q.data ?? []} loading={q.isLoading} maxHeight="calc(100vh - 420px)" />
      </div>
    </>
  );
}

function Events({ id, hasDevice }: { id: number; hasDevice: boolean }) {
  const q = useQuery({ queryKey: ['endpoint-events', id], queryFn: () => api.get<EndpointEventRow[]>(`/api/endpoints/${id}/events`), enabled: hasDevice, refetchInterval: 60000 });
  const columns: Column<EndpointEventRow>[] = [
    { key: 'ts', header: 'Time', render: (e) => <span className="nowrap">{fmtDateTime(e.timestampUtc)}</span>, sort: (e) => e.timestampUtc, width: 170 },
    { key: 'log', header: 'Log', render: (e) => e.logName, sort: (e) => e.logName, width: 120 },
    { key: 'id', header: 'Event', render: (e) => e.eventId, sort: (e) => e.eventId, className: 'num', width: 80 },
    { key: 'msg', header: 'Message', render: (e) => <span title={e.message}>{e.message}</span> },
  ];
  if (!hasDevice) return <div className="empty">This machine is not linked to a device, so no events are matched to it.</div>;
  return (
    <>
      <p className="muted" style={{ marginTop: 0 }}>
        Events forwarded by Windows Event Forwarding (latest 300). Set up WEF with the deployment kit (Endpoints → Deployment) to see process starts, logon failures, USB devices, Defender
        detections and more here.
      </p>
      <ErrorBox error={q.error} />
      <div className="card">
        <DataTable rows={q.data ?? []} columns={columns} rowKey={(e) => e.id} initialSort={{ key: 'ts', desc: true }} maxHeight="calc(100vh - 440px)" empty={q.isLoading ? 'Loading…' : 'No forwarded events from this machine.'} />
      </div>
    </>
  );
}
