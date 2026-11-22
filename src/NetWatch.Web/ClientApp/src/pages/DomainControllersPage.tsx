import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, qs } from '../api/client';
import type { DcEventId, DcGroupChange, DcLockout, DcOverview, DcReplicationLink, DcSecurityEvent, DcSummary, DcTopEntry, PagedEvents, SavedSearch } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { SeverityBadge, StatusBadge } from '../components/Badges';
import { DataTable, type Column } from '../components/DataTable';
import { ErrorBox, Modal } from '../components/Modal';
import { fmtAgo, fmtDateTime, fmtNum } from '../util/format';
import { eventQueryParams, type EventQuery } from '../util/logic';

type Tab = 'overview' | 'events' | 'lockouts' | 'groups' | 'replication' | 'audit';

const TABS: { key: Tab; label: string }[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'events', label: 'Security events' },
  { key: 'lockouts', label: 'Account lockouts' },
  { key: 'groups', label: 'Group changes' },
  { key: 'replication', label: 'Replication' },
  { key: 'audit', label: 'Recommended audit policy' },
];

const PRESETS: { key: string; label: string; eventIds?: string; failedOnly?: boolean }[] = [
  { key: '', label: 'All collected events' },
  { key: 'failed', label: 'Failed logons (4625, 4771, 4776, 4768)', failedOnly: true },
  { key: 'logon', label: 'Successful logons (4624)', eventIds: '4624' },
  { key: 'lockout', label: 'Lockouts / unlocks (4740, 4767)', eventIds: '4740,4767' },
  { key: 'accounts', label: 'Account changes (4720–4743)', eventIds: '4720,4722,4723,4724,4725,4726,4738,4741,4743' },
  { key: 'groups', label: 'Group membership (4728–4757)', eventIds: '4728,4729,4732,4733,4756,4757' },
  { key: 'privileged', label: 'Admin logons (4672)', eventIds: '4672' },
  { key: 'policy', label: 'Audit policy / log cleared (4719, 1102)', eventIds: '4719,1102' },
  { key: 'directory', label: 'Directory changes (5136)', eventIds: '5136' },
];

const RANGES: { key: string; label: string; hours?: number }[] = [
  { key: '1h', label: 'Last hour', hours: 1 },
  { key: '24h', label: 'Last 24 hours', hours: 24 },
  { key: '7d', label: 'Last 7 days', hours: 168 },
  { key: '30d', label: 'Last 30 days', hours: 720 },
  { key: '90d', label: 'Last 90 days', hours: 2160 },
  { key: '1y', label: 'Last 365 days', hours: 8760 },
  { key: 'all', label: 'All stored' },
];

const LOGON_TYPES: Record<number, string> = { 2: 'interactive', 3: 'network', 4: 'batch', 5: 'service', 7: 'unlock', 8: 'network cleartext', 9: 'new credentials', 10: 'remote desktop', 11: 'cached' };


const EMPTY_QUERY: EventQuery = { deviceId: '', preset: '', eventIds: '', user: '', source: '', search: '', range: '24h' };

function skewColor(v?: number | null) {
  if (v == null) return undefined;
  const a = Math.abs(v);
  return a > 240 ? 'var(--crit)' : a > 30 ? 'var(--warn)' : 'var(--ok)';
}

// ------------------------------------------------------------------ overview

function TopTable({ title, rows, onPick }: { title: string; rows: DcTopEntry[]; onPick: (key: string) => void }) {
  return (
    <div className="card">
      <div className="card-body" style={{ paddingBottom: 0 }}>
        <strong>{title}</strong> <span className="muted">(last 24 h)</span>
      </div>
      <table className="data">
        <thead>
          <tr>
            <th>Name</th>
            <th className="num">Failures</th>
            <th>Last</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key} className="clickable" onClick={() => onPick(r.key)}>
              <td className="mono">{r.key}</td>
              <td className="num" style={{ color: r.count >= 20 ? 'var(--crit)' : r.count >= 5 ? 'var(--warn)' : undefined }}>
                {r.count}
              </td>
              <td className="muted nowrap">{fmtAgo(r.lastUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length === 0 && <div className="empty">No failed logons.</div>}
    </div>
  );
}

function DcCard({ dc }: { dc: DcSummary }) {
  return (
    <div className="card stat">
      <div className="label" style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
        <Link to={`/devices/${dc.deviceId}`}>{dc.name}</Link>
        <StatusBadge status={dc.status} />
      </div>
      <div className="muted" style={{ fontSize: 12, margin: '4px 0 8px' }}>
        {dc.domain ?? 'domain not read yet'}
        {dc.site ? ` · site ${dc.site}` : ''}
        {dc.isGlobalCatalog ? ' · GC' : ''}
      </div>
      <div className="breakdown" style={{ flexDirection: 'column', alignItems: 'flex-start', gap: 3 }}>
        <span>{dc.roles ? `FSMO: ${dc.roles}` : <span className="faint">No FSMO roles</span>}</span>
        <span>
          Clock vs PDC:{' '}
          <strong style={{ color: skewColor(dc.skewToPdcSec) }}>{dc.skewToPdcSec == null ? '—' : `${fmtNum(dc.skewToPdcSec, 0)} s`}</strong>
        </span>
        <span>
          Replication:{' '}
          {dc.replicationLinks === 0 ? (
            <span className="faint">not read yet</span>
          ) : dc.replicationFailing > 0 ? (
            <strong style={{ color: 'var(--crit)' }}>
              {dc.replicationFailing} of {dc.replicationLinks} failing
            </strong>
          ) : (
            <span style={{ color: 'var(--ok)' }}>{dc.replicationLinks} links OK</span>
          )}
        </span>
        {dc.servicesDown > 0 && <span style={{ color: 'var(--crit)' }}>{dc.servicesDown} AD service(s) not running</span>}
        {(dc.services?.length ?? 0) > 0 && (
          <span className="chips" aria-label="Services">
            {dc.services!.map((s) => (
              <span
                key={s.name}
                className={`badge ${s.state === 'Running' ? 's-up' : s.state === 'Unknown' ? 's-unknown' : 's-down'}`}
                title={`${s.displayName ?? s.name}: ${s.state}${s.lastChangeUtc ? ` since ${fmtDateTime(s.lastChangeUtc)}` : ''}`}
              >
                {s.name}
              </span>
            ))}
          </span>
        )}
        {(dc.ports?.length ?? 0) > 0 && (
          <span className="chips" aria-label="Service ports">
            {dc.ports!.map((p) => (
              <span key={p.port} className={`badge ${p.error ? 's-down' : 's-up'}`} title={p.error ? `TCP ${p.port}: ${p.error}` : `TCP ${p.port} answered in ${fmtNum(p.ms ?? 0, 1)} ms`}>
                {p.service} {p.port}
              </span>
            ))}
          </span>
        )}
        <span>
          24 h: {dc.events24h} events · <span style={{ color: dc.failedLogons24h > 0 ? 'var(--warn)' : undefined }}>{dc.failedLogons24h} failed logon(s)</span> ·{' '}
          <span style={{ color: dc.lockouts24h > 0 ? 'var(--crit)' : undefined }}>{dc.lockouts24h} lockout(s)</span> · {dc.groupChanges24h} group change(s)
        </span>
        {dc.securityLogError && <span style={{ color: 'var(--crit)' }}>Event logs: {dc.securityLogError}</span>}
        {dc.replicationError && <span style={{ color: 'var(--crit)' }}>Directory/clock: {dc.replicationError}</span>}
        <span className="faint">
          polled {fmtAgo(dc.lastPollUtc)} · replication checked {fmtAgo(dc.lastReplicationCheckUtc)}
        </span>
      </div>
    </div>
  );
}

function OverviewTab({ overview, onFilter }: { overview?: DcOverview; onFilter: (q: Partial<EventQuery>) => void }) {
  const top = useQuery({ queryKey: ['dc', 'top'], queryFn: () => api.get<{ accounts: DcTopEntry[]; sources: DcTopEntry[] }>('/api/dc/top-failures?hours=24'), refetchInterval: 60000 });
  const dcs = overview?.controllers ?? [];
  return (
    <>
      {overview?.useForwardedEvents && (
        <div className="card card-body" style={{ marginBottom: 12 }}>
          Windows Event Forwarding is on: Security events come from this server’s <em>ForwardedEvents</em> log (last read {fmtAgo(overview.forwardedLastReadUtc)},{' '}
          {overview.forwardedEventsRead} events stored since start).
          {overview.forwardedError && <div style={{ color: 'var(--crit)', marginTop: 4 }}>{overview.forwardedError}</div>}
        </div>
      )}
      {dcs.length === 0 && (
        <div className="card card-body muted" style={{ marginBottom: 12 }}>
          No domain controller is monitored yet. Add a device of type <em>Domain controller</em> with a Windows credential of a domain account in “Event Log Readers” (see{' '}
          <em>Recommended audit policy</em>).
        </div>
      )}
      <div className="grid" style={{ marginBottom: 16, gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))' }}>
        {dcs.map((dc) => (
          <DcCard key={dc.deviceId} dc={dc} />
        ))}
      </div>
      <div className="grid cols-2">
        <TopTable title="Accounts with most failed logons" rows={top.data?.accounts ?? []} onPick={(user) => onFilter({ preset: 'failed', user, range: '24h' })} />
        <TopTable title="Sources with most failed logons" rows={top.data?.sources ?? []} onPick={(source) => onFilter({ preset: 'failed', source, range: '24h' })} />
      </div>
    </>
  );
}

// ------------------------------------------------------------------ security events

function SaveSearchDialog({ query, onClose }: { query: EventQuery; onClose: () => void }) {
  const { can } = useAuth();
  const client = useQueryClient();
  const [name, setName] = useState('');
  const [shared, setShared] = useState(false);
  const save = useMutation({
    mutationFn: () => api.post('/api/saved-searches', { page: 'dc-security', name, query, isShared: shared }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['saved-searches'] });
      onClose();
    },
  });
  return (
    <Modal
      title="Save search"
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Cancel</button>
          <button className="primary" disabled={!name.trim() || save.isPending} onClick={() => save.mutate()}>
            Save
          </button>
        </>
      }
    >
      <ErrorBox error={save.error} />
      <label className="field">
        <span>Name</span>
        <input autoFocus maxLength={100} value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Failed logons of service accounts" />
      </label>
      {can('Operator') && (
        <label className="check">
          <input type="checkbox" checked={shared} onChange={(e) => setShared(e.target.checked)} /> Share with all users
        </label>
      )}
    </Modal>
  );
}

function EventsTab({ dcs, query, setQuery }: { dcs: DcSummary[]; query: EventQuery; setQuery: (q: EventQuery) => void }) {
  const client = useQueryClient();
  const [page, setPage] = useState(1);
  const [saving, setSaving] = useState(false);
  const [selected, setSelected] = useState<DcSecurityEvent | null>(null);
  const update = (patch: Partial<EventQuery>) => {
    setPage(1);
    setQuery({ ...query, ...patch });
  };

  const params = useMemo(() => eventQueryParams(query, PRESETS, RANGES, page, Date.now()), [query, page]);
  const q = useQuery({ queryKey: ['dc', 'events', params], queryFn: () => api.get<PagedEvents<DcSecurityEvent>>(`/api/dc/security-events${qs(params)}`), refetchInterval: 30000 });
  const searches = useQuery({ queryKey: ['saved-searches', 'dc-security'], queryFn: () => api.get<SavedSearch<EventQuery>[]>('/api/saved-searches?page=dc-security') });
  const remove = useMutation({ mutationFn: (id: number) => api.del(`/api/saved-searches/${id}`), onSuccess: () => void client.invalidateQueries({ queryKey: ['saved-searches'] }) });
  const [savedId, setSavedId] = useState('');

  const pick = (e: React.MouseEvent, patch: Partial<EventQuery>) => {
    e.stopPropagation();
    update(patch);
  };

  const columns: Column<DcSecurityEvent>[] = [
    { key: 'time', header: 'Time (PKT)', render: (e) => <span className="muted nowrap">{fmtDateTime(e.timestampUtc)}</span>, sort: (e) => e.timestampUtc, width: 160 },
    { key: 'sev', header: 'Severity', render: (e) => <SeverityBadge severity={e.severity} />, sort: (e) => e.severity, width: 90 },
    { key: 'dc', header: 'DC', render: (e) => <span className="nowrap">{e.deviceName}</span>, sort: (e) => e.deviceName ?? '', width: 110 },
    { key: 'id', header: 'Event', render: (e) => <span className="nowrap" title={e.eventName}><span className="mono">{e.eventId}</span> <span className="muted">{e.eventName}</span></span>, sort: (e) => e.eventId, width: 240 },
    {
      key: 'user',
      header: 'Account',
      render: (e) => {
        const u = e.targetUser ?? e.subjectUser;
        return u ? <a href="#" className="mono" onClick={(ev) => pick(ev, { user: u })}>{u}</a> : '—';
      },
      sort: (e) => e.targetUser ?? e.subjectUser ?? '',
      width: 140,
    },
    {
      key: 'src',
      header: 'Source',
      render: (e) => {
        const s = e.sourceIp ?? e.workstation;
        return s ? <a href="#" className="mono" onClick={(ev) => pick(ev, { source: s })}>{s}</a> : '—';
      },
      sort: (e) => e.sourceIp ?? e.workstation ?? '',
      width: 130,
    },
    { key: 'msg', header: 'Details', render: (e) => <span className="clip" title={e.message}>{e.message}</span> },
  ];

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 8, flexWrap: 'wrap' }}>
        <select value={query.deviceId} onChange={(e) => update({ deviceId: e.target.value })} aria-label="Domain controller">
          <option value="">All DCs</option>
          {dcs.map((d) => (
            <option key={d.deviceId} value={d.deviceId}>
              {d.name}
            </option>
          ))}
        </select>
        <select value={query.preset} onChange={(e) => update({ preset: e.target.value, eventIds: '' })} aria-label="Event type">
          {PRESETS.map((p) => (
            <option key={p.key} value={p.key}>
              {p.label}
            </option>
          ))}
        </select>
        <input style={{ width: 120 }} placeholder="Event IDs" title="Comma-separated event IDs (overrides the event type)" value={query.eventIds} onChange={(e) => update({ eventIds: e.target.value })} />
        <input type="search" style={{ width: 150 }} placeholder="Account" value={query.user} onChange={(e) => update({ user: e.target.value })} />
        <input type="search" style={{ width: 150 }} placeholder="Source IP / computer" value={query.source} onChange={(e) => update({ source: e.target.value })} />
        <input type="search" placeholder="Search text…" value={query.search} onChange={(e) => update({ search: e.target.value })} />
        <select value={query.range} onChange={(e) => update({ range: e.target.value })} aria-label="Time range">
          {RANGES.map((r) => (
            <option key={r.key} value={r.key}>
              {r.label}
            </option>
          ))}
        </select>
        <button onClick={() => update(EMPTY_QUERY)}>Reset</button>
      </div>
      <div className="toolbar" style={{ marginBottom: 10 }}>
        <select
          value={savedId}
          aria-label="Saved searches"
          onChange={(e) => {
            setSavedId(e.target.value);
            const s = searches.data?.find((x) => String(x.id) === e.target.value);
            if (s) update({ ...EMPTY_QUERY, ...s.query });
          }}
        >
          <option value="">Saved searches…</option>
          {(searches.data ?? []).map((s) => (
            <option key={s.id} value={s.id}>
              {s.name}
              {s.isShared ? ' (shared)' : ''}
            </option>
          ))}
        </select>
        {savedId && searches.data?.find((s) => String(s.id) === savedId)?.isOwn && (
          <button
            title="Delete this saved search"
            onClick={() => {
              remove.mutate(Number(savedId));
              setSavedId('');
            }}
          >
            <Trash2 size={14} />
          </button>
        )}
        <button onClick={() => setSaving(true)}>Save search…</button>
        <a
          className="btn"
          href={`/api/dc/security-events/export${qs({ ...params, page: undefined, pageSize: undefined })}`}
          download
          title="Download the events matching these filters as CSV (newest 100,000)"
        >
          <Download size={14} /> Export CSV
        </a>
        <span className="spacer" style={{ flex: 1 }} />
        <span className="muted">{q.data ? `${q.data.totalCapped ? '10,000+' : q.data.total} event(s)` : ''}</span>
        <button disabled={page <= 1} onClick={() => setPage(page - 1)}>
          ‹ Newer
        </button>
        <span className="muted">page {page}</span>
        <button disabled={!q.data || q.data.items.length < 500} onClick={() => setPage(page + 1)}>
          Older ›
        </button>
      </div>
      <ErrorBox error={q.error ?? remove.error} />
      <div className="card">
        <DataTable
          rows={q.data?.items ?? []}
          columns={columns}
          rowKey={(e) => e.id}
          onRowClick={setSelected}
          maxHeight="calc(100vh - 380px)"
          empty="No matching security events. Successful network logons (type 3) are not stored by default."
        />
      </div>
      {saving && <SaveSearchDialog query={query} onClose={() => setSaving(false)} />}
      {selected && (
        <Modal title={`Event ${selected.eventId}: ${selected.eventName}`} onClose={() => setSelected(null)} wide>
          <table className="data">
            <tbody>
              <tr><th>Time (PKT)</th><td>{fmtDateTime(selected.timestampUtc)}</td></tr>
              <tr><th>Domain controller</th><td><Link to={`/devices/${selected.deviceId}`}>{selected.deviceName}</Link></td></tr>
              <tr><th>Category / severity</th><td>{selected.category} · <SeverityBadge severity={selected.severity} /></td></tr>
              <tr><th>Target account</th><td className="mono">{selected.targetUser ?? '—'}</td></tr>
              <tr><th>Subject (by)</th><td className="mono">{selected.subjectUser ?? '—'}</td></tr>
              {selected.groupName && <tr><th>Group</th><td>{selected.groupName}</td></tr>}
              {selected.objectName && <tr><th>Object</th><td className="mono">{selected.objectName}</td></tr>}
              <tr><th>Source IP / workstation</th><td className="mono">{[selected.sourceIp, selected.workstation].filter(Boolean).join(' / ') || '—'}</td></tr>
              {selected.logonType != null && <tr><th>Logon type</th><td>{selected.logonType} ({LOGON_TYPES[selected.logonType] ?? 'other'})</td></tr>}
              {(selected.status || selected.subStatus) && <tr><th>Status / sub-status</th><td className="mono">{[selected.status, selected.subStatus].filter(Boolean).join(' / ')}</td></tr>}
              <tr><th>Details</th><td>{selected.message}</td></tr>
            </tbody>
          </table>
        </Modal>
      )}
    </>
  );
}

// ------------------------------------------------------------------ lockouts

function LockoutsTab({ onFilter }: { onFilter: (q: Partial<EventQuery>) => void }) {
  const [days, setDays] = useState(7);
  const [user, setUser] = useState('');
  const q = useQuery({ queryKey: ['dc', 'lockouts', days, user], queryFn: () => api.get<DcLockout[]>(`/api/dc/lockouts${qs({ days, user: user || undefined })}`), refetchInterval: 30000 });
  const rows = q.data ?? [];
  const stillLocked = rows.filter((r) => !r.unlockedUtc).length;
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 10 }}>
        <select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Period">
          {[1, 7, 30, 90].map((d) => (
            <option key={d} value={d}>
              Last {d} day{d > 1 ? 's' : ''}
            </option>
          ))}
        </select>
        <input type="search" placeholder="Account…" value={user} onChange={(e) => setUser(e.target.value)} />
        <span className="muted">
          {rows.length} lockout(s){stillLocked > 0 ? ` · ${stillLocked} without a recorded unlock` : ''}
        </span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 330px)' }}>
        <table className="data">
          <thead>
            <tr>
              <th>Locked out (PKT)</th>
              <th>Account</th>
              <th>Reported by</th>
              <th>Caller computer</th>
              <th className="num">Failures (30 min before)</th>
              <th>Last failure from</th>
              <th>Unlocked</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} className="clickable" onClick={() => r.user && onFilter({ user: r.user, preset: 'failed', range: '7d' })} title="Show the failed logons of this account">
                <td className="muted nowrap">{fmtDateTime(r.timestampUtc)}</td>
                <td className="mono">
                  <strong>{r.user}</strong>
                </td>
                <td>{r.deviceName}</td>
                <td className="mono">{r.callerComputer ?? '—'}</td>
                <td className="num">{r.failuresBefore}</td>
                <td className="mono">{r.lastFailureSource ?? '—'}</td>
                <td className="nowrap">
                  {r.unlockedUtc ? (
                    <>
                      {fmtDateTime(r.unlockedUtc)} <span className="muted">by {r.unlockedBy ?? '?'}</span>
                    </>
                  ) : (
                    <span className="muted">not recorded (auto-unlock or still locked)</span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && <div className="empty">No account lockouts in this period.</div>}
      </div>
    </>
  );
}

// ------------------------------------------------------------------ group changes

function GroupsTab() {
  const [days, setDays] = useState(30);
  const [privilegedOnly, setPrivilegedOnly] = useState(false);
  const q = useQuery({
    queryKey: ['dc', 'groups', days, privilegedOnly],
    queryFn: () => api.get<DcGroupChange[]>(`/api/dc/group-changes${qs({ days, privilegedOnly: privilegedOnly || undefined })}`),
    refetchInterval: 60000,
  });
  const rows = q.data ?? [];
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 10 }}>
        <select value={days} onChange={(e) => setDays(Number(e.target.value))} aria-label="Period">
          {[1, 7, 30, 90, 365].map((d) => (
            <option key={d} value={d}>
              Last {d} day{d > 1 ? 's' : ''}
            </option>
          ))}
        </select>
        <label className="check">
          <input type="checkbox" checked={privilegedOnly} onChange={(e) => setPrivilegedOnly(e.target.checked)} /> Privileged groups only
        </label>
        <span className="muted">
          {rows.length} change(s) · {rows.filter((r) => r.privileged && r.added).length} addition(s) to privileged groups
        </span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 330px)' }}>
        <table className="data">
          <thead>
            <tr>
              <th>Time (PKT)</th>
              <th>Change</th>
              <th>Group</th>
              <th>Member</th>
              <th>Changed by</th>
              <th>DC</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id} style={r.privileged && r.added ? { background: 'color-mix(in srgb, var(--crit) 10%, transparent)' } : undefined}>
                <td className="muted nowrap">{fmtDateTime(r.timestampUtc)}</td>
                <td className="nowrap">
                  {r.added ? <span className={`badge ${r.privileged ? 's-down' : 's-warning'}`}>added</span> : <span className="badge s-unknown">removed</span>}{' '}
                  <span className="mono faint">{r.eventId}</span>
                </td>
                <td>
                  {r.group} {r.privileged && <span className="tag">privileged</span>}
                </td>
                <td className="mono">{r.member}</td>
                <td className="mono">{r.changedBy}</td>
                <td>{r.deviceName}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {rows.length === 0 && <div className="empty">No group membership changes in this period.</div>}
      </div>
    </>
  );
}

// ------------------------------------------------------------------ replication

function ReplicationTab() {
  const q = useQuery({ queryKey: ['dc', 'replication'], queryFn: () => api.get<DcReplicationLink[]>('/api/dc/replication'), refetchInterval: 30000 });
  const links = q.data ?? [];
  const dcs = [...new Set(links.map((l) => l.deviceName))].sort();
  const partners = [...new Set(links.map((l) => l.partner))].sort();
  const cell = (dc: string, partner: string) => links.filter((l) => l.deviceName === dc && l.partner === partner);
  return (
    <>
      <ErrorBox error={q.error} />
      <div className="muted" style={{ marginBottom: 10 }}>
        Inbound replication per destination DC (rows) and source partner (columns), per naming context. A link is failing when its last attempt returned an error; the alert “AD replication
        failing” is raised after 15 minutes.
      </div>
      <div className="card table-wrap" style={{ marginBottom: 16 }}>
        <table className="data">
          <thead>
            <tr>
              <th>Destination ↓ / source →</th>
              {partners.map((p) => (
                <th key={p}>{p}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {dcs.map((dc) => (
              <tr key={dc}>
                <th>{dc}</th>
                {partners.map((p) => {
                  const c = cell(dc, p);
                  if (c.length === 0) return <td key={p} className="faint">—</td>;
                  const failing = c.filter((l) => l.lastResult !== 0 && l.consecutiveFailures > 0);
                  return (
                    <td key={p} title={c.map((l) => `${l.namingContext}: ${l.lastResult === 0 ? 'OK' : l.lastMessage}`).join('\n')}>
                      {failing.length > 0 ? (
                        <span className="badge s-down">
                          {failing.length}/{c.length} failing
                        </span>
                      ) : (
                        <span className="badge s-up">{c.length} OK</span>
                      )}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
        {links.length === 0 && <div className="empty">No replication data yet (read every few minutes over LDAP).</div>}
      </div>
      <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 480px)' }}>
        <table className="data">
          <thead>
            <tr>
              <th>Destination</th>
              <th>Source</th>
              <th>Naming context</th>
              <th>Status</th>
              <th>Last success (PKT)</th>
              <th>Last attempt</th>
              <th className="num">Failures</th>
            </tr>
          </thead>
          <tbody>
            {links.map((l) => (
              <tr key={l.id}>
                <td>
                  <Link to={`/devices/${l.deviceId}`}>{l.deviceName}</Link>
                </td>
                <td>{l.partner}</td>
                <td className="muted">{l.namingContext}</td>
                <td>{l.lastResult === 0 ? <span className="badge s-up">OK</span> : <span className="badge s-down" title={l.lastMessage ?? ''}>{l.lastMessage ?? `error ${l.lastResult}`}</span>}</td>
                <td className="muted nowrap">{l.lastSuccessUtc ? fmtDateTime(l.lastSuccessUtc) : 'never'}</td>
                <td className="muted nowrap">{fmtAgo(l.lastAttemptUtc)}</td>
                <td className="num">{l.consecutiveFailures || ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

// ------------------------------------------------------------------ audit policy help

const AUDIT_POLICY: [string, string, string, string][] = [
  ['Account Logon', 'Audit Credential Validation', 'Success, Failure', '4776'],
  ['Account Logon', 'Audit Kerberos Authentication Service', 'Success, Failure', '4768, 4771'],
  ['Account Management', 'Audit User Account Management', 'Success, Failure', '4720, 4722–4726, 4738, 4740, 4767'],
  ['Account Management', 'Audit Computer Account Management', 'Success', '4741, 4743'],
  ['Account Management', 'Audit Security Group Management', 'Success', '4728, 4729, 4732, 4733, 4756, 4757'],
  ['Logon/Logoff', 'Audit Logon', 'Success, Failure', '4624, 4625'],
  ['Logon/Logoff', 'Audit Account Lockout', 'Failure', '4625 (lockout status)'],
  ['Logon/Logoff', 'Audit Special Logon', 'Success', '4672'],
  ['Policy Change', 'Audit Audit Policy Change', 'Success, Failure', '4719'],
  ['DS Access', 'Audit Directory Service Changes', 'Success', '5136 (needs auditing entries (SACL) on the objects)'],
];

function AuditTab() {
  return (
    <div className="card card-body" style={{ maxWidth: 1000, lineHeight: 1.55 }}>
      <h3 style={{ marginTop: 0 }}>Recommended audit policy for domain controllers</h3>
      <p>
        HIT SIEM can only show what the domain controllers record. Configure these <strong>Advanced Audit Policy</strong> settings in the <em>Default Domain Controllers Policy</em> (or a
        dedicated GPO linked to the Domain Controllers OU): <em>Computer Configuration → Policies → Windows Settings → Security Settings → Advanced Audit Policy Configuration → Audit Policies</em>.
        Also enable <em>Security Options → “Audit: Force audit policy subcategory settings to override audit policy category settings”</em>.
      </p>
      <table className="data" style={{ marginBottom: 16 }}>
        <thead>
          <tr>
            <th>Category</th>
            <th>Subcategory</th>
            <th>Setting</th>
            <th>Events HIT SIEM uses</th>
          </tr>
        </thead>
        <tbody>
          {AUDIT_POLICY.map(([cat, sub, setting, events]) => (
            <tr key={sub}>
              <td className="muted">{cat}</td>
              <td>{sub}</td>
              <td>{setting}</td>
              <td className="mono">{events}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p>
        <strong>Log cleared (1102)</strong> is always recorded. Check the effective policy on a DC with <code>auditpol /get /category:*</code> after <code>gpupdate /force</code>.
      </p>
      <h4>Security log size</h4>
      <p>
        A busy DC writes thousands of events per minute. Set the Security log to at least <strong>1 GB</strong> (4 GB on large domains) with “Overwrite events as needed”, so events are not
        overwritten between two HIT SIEM polls (<em>Event Log → Maximum security log size</em> in the same GPO).
      </p>
      <h4>Least-privilege collection account</h4>
      <ul>
        <li>
          A normal domain user (no Domain Admin rights), member of <strong>Event Log Readers</strong> (built-in domain local group, reads the Security log remotely) and{' '}
          <strong>Remote Management Users</strong> (WinRM) — or <em>Distributed COM Users</em> when DCOM is used.
        </li>
        <li>WMI permission “Remote Enable” on <code>root\cimv2</code> for services, disks and the clock (WMI Control → Security).</li>
        <li>Replication status is read over LDAP (port 389, signed and sealed); every authenticated user can read it.</li>
        <li>Firewall: WinRM 5985/5986 (or RPC/DCOM 135 + dynamic ports), Remote Event Log Management, LDAP 389.</li>
      </ul>
      <h4>Windows Event Forwarding (optional)</h4>
      <p>
        Instead of reading each DC remotely, the DCs can push their Security events to the HIT SIEM server: create a source-initiated subscription on the HIT SIEM server (Event Viewer →
        Subscriptions) for the event IDs above with destination log <em>ForwardedEvents</em>, point the DCs to it via the GPO <em>Windows Components → Event Forwarding → Configure target
        Subscription Manager</em>, add the <em>Network Service</em> account to the DCs’ Event Log Readers group, then enable <em>Settings → General → Domain controllers → Use Windows Event
        Forwarding</em>. The HIT SIEM service account needs to read the local ForwardedEvents log (local Event Log Readers group).
      </p>
    </div>
  );
}

// ------------------------------------------------------------------ page

/** Domain controllers: AD security events, lockouts, privileged group changes, replication and audit policy help. */
export function DomainControllersPage() {
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) ?? 'overview';
  const [query, setQuery] = useState<EventQuery>(EMPTY_QUERY);
  const overview = useQuery({ queryKey: ['dc', 'summary'], queryFn: () => api.get<DcOverview>('/api/dc/summary'), refetchInterval: 30000 });
  useQuery({ queryKey: ['dc', 'event-ids'], queryFn: () => api.get<DcEventId[]>('/api/dc/event-ids'), staleTime: Infinity });
  const dcs = overview.data?.controllers ?? [];
  const go = (t: Tab) => setParams({ tab: t }, { replace: true });
  const filter = (patch: Partial<EventQuery>) => {
    setQuery({ ...EMPTY_QUERY, ...patch });
    go('events');
  };
  const failing = dcs.reduce((n, d) => n + d.replicationFailing, 0);
  const lockouts = dcs.reduce((n, d) => n + d.lockouts24h, 0);

  return (
    <>
      <div className="page-head">
        <h1>Domain controllers</h1>
      </div>
      <ErrorBox error={overview.error} />
      <div className="tabs">
        {TABS.map((t) => (
          <button key={t.key} className={tab === t.key ? 'active' : ''} onClick={() => go(t.key)}>
            {t.label}
            {t.key === 'lockouts' && lockouts > 0 ? ` (${lockouts})` : ''}
            {t.key === 'replication' && failing > 0 ? ` (${failing} failing)` : ''}
          </button>
        ))}
      </div>
      {tab === 'overview' && <OverviewTab overview={overview.data} onFilter={filter} />}
      {tab === 'events' && <EventsTab dcs={dcs} query={query} setQuery={setQuery} />}
      {tab === 'lockouts' && <LockoutsTab onFilter={filter} />}
      {tab === 'groups' && <GroupsTab />}
      {tab === 'replication' && <ReplicationTab />}
      {tab === 'audit' && <AuditTab />}
    </>
  );
}
