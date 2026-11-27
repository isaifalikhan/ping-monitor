import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, KeyRound } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api, qs } from '../../api/client';
import type { EndpointChange, EndpointDeployment, EndpointListItem, EndpointSettings, FileHit, ProcessHit, SoftwareMachineRow, SoftwareSummaryRow } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { SeverityBadge, UtilBar } from '../../components/Badges';
import { DataTable, type Column } from '../../components/DataTable';
import { Confirm, ErrorBox, Modal } from '../../components/Modal';
import { fmtAgo, fmtBytes, fmtDateTime } from '../../util/format';
import { CHANGE_LABELS, ChangeBadge, daysSince, ipText, problems, shortOs } from './common';

type Tab = 'computers' | 'software' | 'processes' | 'files' | 'changes' | 'deployment';

/** Endpoint inventory pushed by the GPO scheduled-task script: computers, software, processes, changes, deployment. */
export function EndpointsPage() {
  const { can } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) || 'computers';
  const setTab = (t: Tab) => setParams(t === 'computers' ? {} : { tab: t }, { replace: true });
  const list = useQuery({ queryKey: ['endpoints'], queryFn: () => api.get<EndpointListItem[]>('/api/endpoints/'), refetchInterval: 60000 });
  const all = list.data ?? [];
  const stale = all.filter((e) => e.stale).length;
  const withProblems = all.filter((e) => problems(e).length > 0).length;

  const tabs: [Tab, string][] = [
    ['computers', `Computers (${all.length})`],
    ['software', 'Software'],
    ['processes', 'Processes'],
    ['files', 'Files'],
    ['changes', 'Changes'],
    ...(can('Admin') ? ([['deployment', 'Deployment']] as [Tab, string][]) : []),
  ];

  return (
    <>
      <div className="page-head">
        <h1>Endpoints</h1>
        <span className="muted">GPO inventory script · Windows Event Forwarding</span>
        <span className="spacer" />
        <span className="muted" style={{ fontSize: 13 }}>
          {all.length - stale} reporting · {stale > 0 ? <span style={{ color: 'var(--warn)' }}>{stale} not reporting</span> : '0 not reporting'} ·{' '}
          {withProblems > 0 ? <span style={{ color: 'var(--crit)' }}>{withProblems} with problems</span> : '0 with problems'}
        </span>
      </div>
      <div className="tabs">
        {tabs.map(([k, label]) => (
          <button key={k} className={tab === k ? 'active' : ''} onClick={() => setTab(k)}>
            {label}
          </button>
        ))}
      </div>
      <ErrorBox error={list.error} />
      {tab === 'computers' && <ComputersTab rows={all} loading={list.isLoading} onDeploy={can('Admin') ? () => setTab('deployment') : undefined} />}
      {tab === 'software' && <SoftwareTab />}
      {tab === 'processes' && <ProcessesTab />}
      {tab === 'files' && <FilesTab />}
      {tab === 'changes' && <ChangesTab machines={all} />}
      {tab === 'deployment' && can('Admin') && <DeploymentTab />}
    </>
  );
}

function ComputersTab({ rows, loading, onDeploy }: { rows: EndpointListItem[]; loading: boolean; onDeploy?: () => void }) {
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<'all' | 'problems' | 'stale' | 'firewall' | 'av' | 'patches' | 'reboot'>('all');

  const shown = useMemo(() => {
    const term = search.trim().toLowerCase();
    return rows.filter((e) => {
      if (term && ![e.name, e.ip, e.user, e.os, e.model, e.serial].some((v) => (v ?? '').toLowerCase().includes(term))) return false;
      switch (filter) {
        case 'problems':
          return problems(e).length > 0;
        case 'stale':
          return e.stale;
        case 'firewall':
          return !!e.firewallOff;
        case 'av':
          return !!e.avProblem;
        case 'patches':
          return (e.missingUpdates ?? 0) > 0 || (daysSince(e.lastPatchUtc) ?? 0) > 45;
        case 'reboot':
          return e.pendingReboot === true;
        default:
          return true;
      }
    });
  }, [rows, search, filter]);

  const columns: Column<EndpointListItem>[] = [
    {
      key: 'state',
      header: 'State',
      render: (e) => (e.stale ? <span className="badge s-unknown">not reporting</span> : problems(e).length > 0 ? <span className="badge s-warning">problems</span> : <span className="badge s-up">OK</span>),
      sort: (e) => (e.stale ? 0 : problems(e).length > 0 ? 1 : 2),
      width: 120,
    },
    {
      key: 'name',
      header: 'Computer',
      render: (e) => (
        <span className="nowrap">
          <strong>{e.name}</strong> <span className="mono muted">{ipText(e.ip)}</span>
        </span>
      ),
      sort: (e) => e.name,
    },
    { key: 'user', header: 'User', render: (e) => <span className="muted nowrap">{e.user ?? '—'}</span>, sort: (e) => e.user ?? '' },
    { key: 'os', header: 'OS', render: (e) => <span className="nowrap" title={e.osBuild ?? ''}>{shortOs(e.os)} <span className="faint">{e.osBuild}</span></span>, sort: (e) => `${e.os} ${e.osBuild}` },
    { key: 'model', header: 'Model', render: (e) => <span className="muted nowrap">{e.model ?? '—'}</span>, sort: (e) => e.model ?? '' },
    {
      key: 'fw',
      header: 'Firewall',
      render: (e) => (e.firewallOff ? <span className="badge s-down" title={`Off: ${e.firewallOff}`}>off: {e.firewallOff}</span> : <span className="badge s-up">on</span>),
      sort: (e) => (e.firewallOff ? 0 : 1),
    },
    {
      key: 'av',
      header: 'Antivirus',
      render: (e) => (e.avProblem ? <span className="badge s-down" title={e.avProblem}>problem</span> : <span className="badge s-up">OK</span>),
      sort: (e) => (e.avProblem ? 0 : 1),
    },
    {
      key: 'patch',
      header: 'Updates',
      render: (e) => {
        const age = daysSince(e.lastPatchUtc);
        return (
          <span className="nowrap">
            {(e.missingUpdates ?? 0) > 0 ? <span style={{ color: 'var(--crit)' }}>{e.missingUpdates} missing</span> : e.missingUpdates === 0 ? <span style={{ color: 'var(--ok)' }}>none missing</span> : <span className="faint">?</span>}
            {age != null && <span className="muted" style={{ color: age > 45 ? 'var(--warn)' : undefined }}> · {age} d</span>}
            {e.pendingReboot && <span className="badge s-warning" style={{ marginLeft: 6 }}>reboot</span>}
          </span>
        );
      },
      sort: (e) => (e.missingUpdates ?? -1) * 1000 + (daysSince(e.lastPatchUtc) ?? 0),
    },
    { key: 'disk', header: 'Fullest disk', render: (e) => <UtilBar value={e.worstDiskPct} />, sort: (e) => e.worstDiskPct ?? -1, width: 130 },
    { key: 'seen', header: 'Last report', render: (e) => <span title={fmtDateTime(e.lastSeenUtc)} style={{ color: e.stale ? 'var(--warn)' : undefined }}>{fmtAgo(e.lastSeenUtc)}</span>, sort: (e) => e.lastSeenUtc },
    { key: 'alert', header: 'Alert', render: (e) => (e.worstAlert ? <SeverityBadge severity={e.worstAlert} /> : ''), sort: (e) => ['Info', 'Warning', 'Major', 'Critical'].indexOf(e.worstAlert ?? '') },
  ];

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input type="search" placeholder="Search name, IP, user, OS, model, serial…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <div className="seg">
          {(
            [
              ['all', 'All'],
              ['problems', 'Problems'],
              ['stale', 'Not reporting'],
              ['firewall', 'Firewall off'],
              ['av', 'Antivirus'],
              ['patches', 'Updates'],
              ['reboot', 'Reboot pending'],
            ] as const
          ).map(([k, label]) => (
            <button key={k} className={filter === k ? 'active' : ''} onClick={() => setFilter(k)}>
              {label}
            </button>
          ))}
        </div>
        <span className="muted">
          {shown.length} of {rows.length}
        </span>
      </div>
      <div className="card">
        <DataTable
          rows={shown}
          columns={columns}
          rowKey={(e) => e.id}
          onRowClick={(e) => navigate(`/endpoints/${e.id}`)}
          initialSort={{ key: 'state' }}
          maxHeight="calc(100vh - 270px)"
          empty={
            loading ? (
              'Loading…'
            ) : rows.length === 0 ? (
              <span>
                No machine has reported yet.{' '}
                {onDeploy && (
                  <button className="btn link" onClick={onDeploy}>
                    Set up the GPO deployment
                  </button>
                )}
              </span>
            ) : (
              'No machine matches.'
            )
          }
        />
      </div>
    </>
  );
}

function SoftwareTab() {
  const navigate = useNavigate();
  const [term, setTerm] = useState('');
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  useEffect(() => {
    const t = setTimeout(() => setQuery(term.trim()), 300);
    return () => clearTimeout(t);
  }, [term]);
  const q = useQuery({ queryKey: ['endpoint-software', query], queryFn: () => api.get<SoftwareSummaryRow[]>('/api/endpoints/software' + qs({ q: query })) });
  const machines = useQuery({
    queryKey: ['endpoint-software-machines', selected],
    queryFn: () => api.get<SoftwareMachineRow[]>('/api/endpoints/software/machines' + qs({ name: selected })),
    enabled: !!selected,
  });

  const columns: Column<SoftwareSummaryRow>[] = [
    { key: 'name', header: 'Program', render: (s) => <strong>{s.name}</strong>, sort: (s) => s.name },
    { key: 'publisher', header: 'Publisher', render: (s) => <span className="muted">{s.publisher ?? '—'}</span>, sort: (s) => s.publisher ?? '' },
    { key: 'machines', header: 'Computers', render: (s) => s.machines, sort: (s) => s.machines, className: 'num', width: 110 },
    { key: 'versions', header: 'Versions', render: (s) => <span className="muted" title={s.versions.join(', ')}>{s.versions.length > 3 ? `${s.versions.slice(0, 3).join(', ')} +${s.versions.length - 3}` : s.versions.join(', ') || '—'}</span> },
  ];
  const machineColumns: Column<SoftwareMachineRow>[] = [
    { key: 'name', header: 'Computer', render: (m) => <strong>{m.name}</strong>, sort: (m) => m.name },
    { key: 'version', header: 'Version', render: (m) => <span className="mono">{m.version || '—'}</span>, sort: (m) => m.version },
    { key: 'user', header: 'User', render: (m) => <span className="muted">{m.user ?? '—'}</span>, sort: (m) => m.user ?? '' },
    { key: 'seen', header: 'Last report', render: (m) => <span style={{ color: m.stale ? 'var(--warn)' : undefined }}>{fmtAgo(m.lastSeenUtc)}</span>, sort: (m) => m.lastSeenUtc },
  ];

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input type="search" placeholder="Find a program or publisher (e.g. AnyDesk, TeamViewer, Java)…" value={term} onChange={(e) => setTerm(e.target.value)} style={{ minWidth: 340 }} />
        <span className="muted">{q.data ? `${q.data.length} programs` : ''}</span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card">
        <DataTable rows={q.data ?? []} columns={columns} rowKey={(s) => s.name} onRowClick={(s) => setSelected(s.name)} initialSort={{ key: 'machines', desc: true }} maxHeight="calc(100vh - 290px)" empty={q.isLoading ? 'Loading…' : 'No programs.'} />
      </div>
      {selected && (
        <Modal title={selected} onClose={() => setSelected(null)} wide>
          <ErrorBox error={machines.error} />
          <DataTable rows={machines.data ?? []} columns={machineColumns} rowKey={(m) => `${m.id}-${m.version}`} onRowClick={(m) => navigate(`/endpoints/${m.id}?tab=software`)} initialSort={{ key: 'name' }} maxHeight="60vh" empty={machines.isLoading ? 'Loading…' : 'None.'} />
        </Modal>
      )}
    </>
  );
}

function ProcessesTab() {
  const navigate = useNavigate();
  const [term, setTerm] = useState('');
  const [query, setQuery] = useState('');
  useEffect(() => {
    const t = setTimeout(() => setQuery(term.trim()), 400);
    return () => clearTimeout(t);
  }, [term]);
  const q = useQuery({ queryKey: ['endpoint-processes', query], queryFn: () => api.get<ProcessHit[]>('/api/endpoints/processes' + qs({ q: query })), enabled: query.length >= 2 });
  const columns: Column<ProcessHit>[] = [
    { key: 'machine', header: 'Computer', render: (p) => <strong>{p.machine}</strong>, sort: (p) => p.machine },
    { key: 'name', header: 'Process', render: (p) => <span className="mono">{p.name}</span>, sort: (p) => p.name },
    { key: 'pid', header: 'PID', render: (p) => p.pid, sort: (p) => p.pid, className: 'num', width: 80 },
    { key: 'user', header: 'User', render: (p) => <span className="muted">{p.user ?? '—'}</span>, sort: (p) => p.user ?? '' },
    { key: 'cmd', header: 'Command line / path', render: (p) => <span className="mono muted" title={p.commandLine ?? p.path ?? ''}>{p.commandLine ?? p.path ?? '—'}</span> },
    { key: 'seen', header: 'Seen', render: (p) => fmtAgo(p.lastSeenUtc), sort: (p) => p.lastSeenUtc, width: 100 },
  ];
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input type="search" placeholder="Process name, path or command line (min. 2 characters)…" value={term} onChange={(e) => setTerm(e.target.value)} style={{ minWidth: 380 }} />
        <span className="muted">Running at each machine's last inventory.{q.data ? ` ${q.data.length} found.` : ''}</span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card">
        <DataTable rows={q.data ?? []} columns={columns} rowKey={(p) => `${p.endpointId}-${p.pid}-${p.name}`} onRowClick={(p) => navigate(`/endpoints/${p.endpointId}?tab=processes`)} initialSort={{ key: 'machine' }} maxHeight="calc(100vh - 290px)" empty={query.length < 2 ? 'Type a process name, e.g. anydesk, powershell, mimikatz.' : q.isLoading ? 'Searching…' : 'Not running anywhere.'} />
      </div>
    </>
  );
}

function FilesTab() {
  const navigate = useNavigate();
  const [term, setTerm] = useState('');
  const [ext, setExt] = useState('');
  const [sha, setSha] = useState('');
  const [minMb, setMinMb] = useState('');
  const [folder, setFolder] = useState('');
  const [query, setQuery] = useState<Record<string, string>>({});
  useEffect(() => {
    const t = setTimeout(() => setQuery({ q: term.trim(), ext: ext.trim(), sha256: sha.trim(), minMb: minMb.trim(), folder }), 400);
    return () => clearTimeout(t);
  }, [term, ext, sha, minMb, folder]);
  const active = !!(query.q || query.ext || query.sha256 || query.minMb);
  const q = useQuery({ queryKey: ['endpoint-files', query], queryFn: () => api.get<FileHit[]>('/api/endpoints/files' + qs(query)), enabled: active });
  const columns: Column<FileHit>[] = [
    { key: 'machine', header: 'Computer', render: (h) => <strong>{h.machine}</strong>, sort: (h) => h.machine },
    { key: 'name', header: 'File', render: (h) => <span className="mono" title={h.file.path}>{h.file.name}</span>, sort: (h) => h.file.name },
    { key: 'where', header: 'Where', render: (h) => <span className="muted">{h.file.user} / {h.file.folder}</span>, sort: (h) => `${h.file.user}/${h.file.folder}` },
    { key: 'size', header: 'Size', render: (h) => fmtBytes(h.file.size), sort: (h) => h.file.size, className: 'num', width: 100 },
    { key: 'mod', header: 'Modified', render: (h) => (h.file.modifiedUtc ? fmtDateTime(h.file.modifiedUtc) : '—'), sort: (h) => h.file.modifiedUtc ?? '', width: 170 },
    { key: 'sha', header: 'SHA-256', render: (h) => <span className="mono muted" title={h.file.sha256 ?? 'not hashed'}>{h.file.sha256 ? h.file.sha256.slice(0, 16) + '…' : '—'}</span> },
    { key: 'path', header: 'Path', render: (h) => <span className="mono muted" title={h.file.path}>{h.file.path}</span> },
  ];
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12, flexWrap: 'wrap' }}>
        <input type="search" placeholder="Name or path contains…" value={term} onChange={(e) => setTerm(e.target.value)} style={{ minWidth: 260 }} />
        <input placeholder="Extension (exe, pst, iso…)" value={ext} onChange={(e) => setExt(e.target.value)} style={{ width: 170 }} />
        <input placeholder="Min. size (MB)" value={minMb} onChange={(e) => setMinMb(e.target.value.replace(/[^0-9.]/g, ''))} style={{ width: 120 }} />
        <input placeholder="SHA-256 (exact)" value={sha} onChange={(e) => setSha(e.target.value)} style={{ width: 260 }} className="mono" />
        <select value={folder} onChange={(e) => setFolder(e.target.value)}>
          <option value="">All folders</option>
          {['Desktop', 'Downloads', 'Documents', 'Startup', 'System'].map((f) => (
            <option key={f} value={f}>
              {f}
            </option>
          ))}
        </select>
        <span className="muted">{q.data ? `${q.data.length} files${q.data.length >= 2000 ? ' (first 2,000, largest first)' : ''}` : ''}</span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card">
        <DataTable
          rows={q.data ?? []}
          columns={columns}
          rowKey={(h) => `${h.file.endpointId}|${h.file.path}`}
          onRowClick={(h) => navigate(`/endpoints/${h.file.endpointId}?tab=files`)}
          initialSort={{ key: 'size', desc: true }}
          maxHeight="calc(100vh - 290px)"
          empty={!active ? "Search the files in every user's Desktop, Downloads and Documents: by name, extension, size or SHA-256 (e.g. find a malware hash on all PCs)." : q.isLoading ? 'Searching…' : 'No file matches.'}
        />
      </div>
    </>
  );
}

export function ChangeTable({ rows, machines, loading, maxHeight }: { rows: EndpointChange[]; machines?: Map<number, string>; loading: boolean; maxHeight?: string }) {
  const navigate = useNavigate();
  const columns: Column<EndpointChange>[] = [
    { key: 'ts', header: 'Time', render: (c) => <span className="nowrap">{fmtDateTime(c.timestampUtc)}</span>, sort: (c) => c.timestampUtc, width: 170 },
    ...(machines ? [{ key: 'machine', header: 'Computer', render: (c: EndpointChange) => <strong>{machines.get(c.endpointId) ?? `#${c.endpointId}`}</strong>, sort: (c: EndpointChange) => machines.get(c.endpointId) ?? '' }] : []),
    { key: 'kind', header: 'Change', render: (c) => <ChangeBadge kind={c.kind} />, sort: (c) => c.kind, width: 190 },
    { key: 'item', header: 'What', render: (c) => <span>{c.item}</span>, sort: (c) => c.item },
    { key: 'detail', header: 'Details', render: (c) => <span className="muted" title={c.detail ?? ''}>{c.detail ?? ''}</span> },
  ];
  return (
    <DataTable
      rows={rows}
      columns={columns}
      rowKey={(c) => c.id}
      onRowClick={machines ? (c) => navigate(`/endpoints/${c.endpointId}?tab=changes`) : undefined}
      initialSort={{ key: 'ts', desc: true }}
      maxHeight={maxHeight ?? 'calc(100vh - 290px)'}
      empty={loading ? 'Loading…' : 'No changes in this period.'}
    />
  );
}

function ChangesTab({ machines }: { machines: EndpointListItem[] }) {
  const [days, setDays] = useState(7);
  const [kind, setKind] = useState('');
  const [hideUpdates, setHideUpdates] = useState(true);
  const q = useQuery({ queryKey: ['endpoint-changes', days, kind], queryFn: () => api.get<EndpointChange[]>('/api/endpoints/changes' + qs({ days, kind })), refetchInterval: 60000 });
  const names = useMemo(() => new Map(machines.map((m) => [m.id, m.name])), [machines]);
  const rows = (q.data ?? []).filter((c) => !hideUpdates || kind || !['updateInstalled', 'softwareUpdated', 'enrolled', 'portClosed', 'fileCreated', 'fileModified', 'fileDeleted'].includes(c.kind));
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
          {[1, 7, 30, 90, 180].map((d) => (
            <option key={d} value={d}>
              Last {d === 1 ? '24 hours' : `${d} days`}
            </option>
          ))}
        </select>
        <select value={kind} onChange={(e) => setKind(e.target.value)}>
          <option value="">All changes</option>
          {Object.entries(CHANGE_LABELS).map(([k, label]) => (
            <option key={k} value={k}>
              {label}
            </option>
          ))}
        </select>
        {!kind && (
          <label className="check">
            <input type="checkbox" checked={hideUpdates} onChange={(e) => setHideUpdates(e.target.checked)} /> Hide routine changes (updates, version upgrades, ordinary file changes)
          </label>
        )}
        <span className="muted">{rows.length} changes</span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card">
        <ChangeTable rows={rows} machines={names} loading={q.isLoading} />
      </div>
    </>
  );
}

function DeploymentTab() {
  const client = useQueryClient();
  const q = useQuery({ queryKey: ['endpoint-deployment'], queryFn: () => api.get<EndpointDeployment>('/api/endpoints/deployment'), refetchInterval: 30000 });
  const [server, setServer] = useState(() => window.location.origin);
  const [form, setForm] = useState<EndpointSettings | null>(null);
  const [rotate, setRotate] = useState(false);
  const [showKey, setShowKey] = useState(false);
  useEffect(() => {
    if (q.data && !form) setForm(q.data.settings);
  }, [q.data, form]);
  const save = useMutation({
    mutationFn: (s: EndpointSettings) => api.put<EndpointSettings>('/api/settings/endpoints', s),
    onSuccess: (s) => {
      setForm(s);
      client.invalidateQueries({ queryKey: ['endpoint-deployment'] });
    },
  });
  const rotateKey = useMutation({
    mutationFn: () => api.post('/api/endpoints/enrollment-key/rotate'),
    onSuccess: () => {
      setRotate(false);
      client.invalidateQueries({ queryKey: ['endpoint-deployment'] });
    },
  });
  const d = q.data;
  const localhost = /\/\/(localhost|127\.|\[::1\])/i.test(server);
  const set = <K extends keyof EndpointSettings>(k: K, v: EndpointSettings[K]) => setForm((f) => (f ? { ...f, [k]: v } : f));

  return (
    <div className="grid" style={{ gap: 16 }}>
      <ErrorBox error={q.error} />
      {d && (
        <div className="grid cols-4">
          <div className="card stat">
            <div className="label">Machines</div>
            <div className="value">{d.machines}</div>
            <div className="breakdown muted">
              {d.reporting} reporting · {d.stale} not reporting
            </div>
          </div>
          <div className="card stat">
            <div className="label">Reports since start</div>
            <div className="value">{d.reportsAccepted}</div>
            <div className="breakdown muted">
              {d.reportsRefused} refused · last {d.lastReportUtc ? fmtAgo(d.lastReportUtc) : 'never'}
            </div>
          </div>
          <div className="card stat">
            <div className="label">Forwarded endpoint events</div>
            <div className="value">{d.forwardedEndpointEvents}</div>
            <div className="breakdown muted">{d.forwardedError ? <span style={{ color: 'var(--crit)' }}>{d.forwardedError}</span> : d.forwardedLastReadUtc ? `ForwardedEvents read ${fmtAgo(d.forwardedLastReadUtc)}` : 'not read yet'}</div>
          </div>
          <div className="card stat">
            <div className="label">Script</div>
            <div className="value" style={{ fontSize: 18 }}>HitSiemInventory.ps1</div>
            <div className="breakdown muted mono" title={`SHA-256 ${d.scriptSha256}`}>SHA-256 {d.scriptSha256.slice(0, 16)}…</div>
          </div>
        </div>
      )}

      <div className="card">
        <div className="card-head">
          <h3>1 · Download the deployment kit</h3>
        </div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>
            Nothing is installed on the machines. A GPO scheduled task runs the script from NETLOGON as SYSTEM every {form?.intervalMin ?? 30} minutes; it reads the machine with local WMI and
            sends the inventory here over HTTPS. The kit contains the script, its configuration (this server's address, certificate thumbprint and the enrollment key), the WEF subscription and
            step-by-step GPO instructions (README.txt).
          </p>
          <div className="toolbar">
            <label className="field" style={{ minWidth: 380 }}>
              <span>Address the machines use to reach HIT SIEM</span>
              <input value={server} onChange={(e) => setServer(e.target.value)} placeholder="https://hitsiem.corp.local:8443" />
              {localhost && <span className="hint" style={{ color: 'var(--warn)' }}>localhost only works on this server: use its DNS name.</span>}
            </label>
            <a className="btn primary" href={'/api/endpoints/kit' + qs({ server })} download>
              <Download size={15} /> Download kit (.zip)
            </a>
          </div>
          {d && (
            <dl className="kv" style={{ marginTop: 12 }}>
              <dt>Certificate</dt>
              <dd>
                {d.certificateSubject ?? '—'} <span className="mono muted">{d.certificateThumbprint ?? 'no thumbprint (only CA-trusted certificates accepted)'}</span>
              </dd>
              <dt>Enrollment key</dt>
              <dd>
                <span className="mono">{showKey ? d.enrollmentKey : '•'.repeat(16)}</span>{' '}
                <button className="btn link" onClick={() => setShowKey((s) => !s)}>
                  {showKey ? 'Hide' : 'Show'}
                </button>{' '}
                <button className="btn" onClick={() => setRotate(true)}>
                  <KeyRound size={14} /> Change key
                </button>
                <div className="hint muted">New machines need the key once; then each machine uses its own token. Enrolled machines keep working after the key is changed.</div>
              </dd>
            </dl>
          )}
          <p className="muted">
            The certificate thumbprint is pinned in the kit. After replacing the HIT SIEM certificate (Settings → System), download a new kit and copy hitsiem-endpoint.json to NETLOGON again.
          </p>
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <h3>2 · Create the GPO</h3>
        </div>
        <div className="card-body">
          <ol style={{ margin: 0, paddingLeft: 20, lineHeight: 1.7 }}>
            <li>
              Copy <span className="mono">HitSiemInventory.ps1</span> and <span className="mono">hitsiem-endpoint.json</span> to <span className="mono">\\&lt;domain&gt;\NETLOGON\HitSiem\</span>.
            </li>
            <li>
              Group Policy Management → new GPO <em>HIT SIEM Endpoint Inventory</em>, linked to the computer OUs.
            </li>
            <li>
              Computer Configuration → Preferences → Control Panel Settings → Scheduled Tasks → New → Scheduled Task (At least Windows 7): run as <span className="mono">NT AUTHORITY\System</span>,
              highest privileges, daily trigger repeating every {form?.intervalMin ?? 30} minutes plus “At startup”, action{' '}
              <span className="mono">powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "\\&lt;domain&gt;\NETLOGON\HitSiem\HitSiemInventory.ps1"</span>.
            </li>
            <li>
              For live events (malware, new local admin, USB stick, suspicious command lines, log cleared): set the Event Forwarding subscription manager to this server, create the subscription
              with <span className="mono">wecutil cs HitSiem-Endpoints-Subscription.xml</span> and enable the audit policies listed in README.txt.
            </li>
            <li>
              Test on one PC: <span className="mono">gpupdate /force</span>, then <span className="mono">schtasks /run /tn "HIT SIEM Inventory"</span>. It appears under Computers within a minute.
            </li>
          </ol>
        </div>
      </div>

      {form && (
        <div className="card">
          <div className="card-head">
            <h3>Settings</h3>
            <span className="spacer" />
            <button className="btn primary" disabled={save.isPending} onClick={() => save.mutate(form)}>
              Save
            </button>
          </div>
          <div className="card-body">
            <ErrorBox error={save.error} />
            {save.isSuccess && <div className="muted" style={{ marginBottom: 8 }}>Saved.</div>}
            <div className="form-grid">
              <label className="check">
                <input type="checkbox" checked={form.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Accept inventory reports
              </label>
              <label className="check">
                <input type="checkbox" checked={form.autoCreateDevices} onChange={(e) => set('autoCreateDevices', e.target.checked)} /> Create a Workstation device for unknown machines
              </label>
              <label className="check">
                <input type="checkbox" checked={form.collectCommandLines} onChange={(e) => set('collectCommandLines', e.target.checked)} /> Collect process command lines
              </label>
              <label className="check">
                <input type="checkbox" checked={form.collectConnections} onChange={(e) => set('collectConnections', e.target.checked)} /> Collect established network connections
              </label>
              <label className="check">
                <input type="checkbox" checked={form.readForwardedEvents} onChange={(e) => set('readForwardedEvents', e.target.checked)} /> Read forwarded endpoint events (WEF)
              </label>
              <label className="check">
                <input type="checkbox" checked={form.storeProcessStarts} onChange={(e) => set('storeProcessStarts', e.target.checked)} /> Store every process start (4688; off = only suspicious ones)
              </label>
              <label className="check">
                <input type="checkbox" checked={form.collectFiles} onChange={(e) => set('collectFiles', e.target.checked)} /> List files in users' Desktop, Downloads and Documents (names, sizes, dates, SHA-256; no contents)
              </label>
              <label className="check" title="Lets admins quarantine a file on a PC. This sends commands to the machines; each action is confirmed and logged.">
                <input type="checkbox" checked={form.allowRemediation} onChange={(e) => set('allowRemediation', e.target.checked)} /> Allow file quarantine actions (sends commands to PCs — off = inventory only)
              </label>
              <NumberField label="Files per machine (max.)" hint="the list stops there" min={1000} max={200000} value={form.maxFilesPerMachine} onChange={(v) => set('maxFilesPerMachine', v)} />
              <NumberField label="Hash files up to (MB)" hint="0 = no hashes" min={0} max={4096} value={form.hashMaxMb} onChange={(v) => set('hashMaxMb', v)} />
              <NumberField label="Run every (minutes)" hint="written into README; set the same in the GPO trigger" min={5} max={1440} value={form.intervalMin} onChange={(v) => set('intervalMin', v)} />
              <NumberField label="Not reporting after (hours)" min={1} max={720} value={form.staleHours} onChange={(v) => set('staleHours', v)} />
              <NumberField label="Antivirus signatures too old after (days)" min={1} max={365} value={form.avSignatureMaxAgeDays} onChange={(v) => set('avSignatureMaxAgeDays', v)} />
              <NumberField label="Keep changes (days)" min={7} max={3650} value={form.changeRetentionDays} onChange={(v) => set('changeRetentionDays', v)} />
              <NumberField label="Forwarded events per 30 s cycle" min={100} max={50000} value={form.maxForwardedPerCycle} onChange={(v) => set('maxForwardedPerCycle', v)} />
            </div>
          </div>
        </div>
      )}

      {rotate && (
        <Confirm
          title="Change the enrollment key?"
          message="Machines that are already enrolled keep working with their own token. New machines (and machines reset in HIT SIEM) need the new key: download the kit again and replace hitsiem-endpoint.json in NETLOGON."
          confirmLabel="Change key"
          danger
          busy={rotateKey.isPending}
          onConfirm={() => rotateKey.mutate()}
          onClose={() => setRotate(false)}
        />
      )}
    </div>
  );
}

function NumberField({ label, hint, min, max, value, onChange }: { label: string; hint?: string; min: number; max: number; value: number; onChange: (v: number) => void }) {
  return (
    <label className="field">
      <span>{label}</span>
      <input type="number" min={min} max={max} value={value} onChange={(e) => onChange(Number(e.target.value))} />
      <span className="hint">
        {hint ? `${hint} ` : ''}({min}–{max})
      </span>
    </label>
  );
}
