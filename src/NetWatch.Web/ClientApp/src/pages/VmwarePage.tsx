import { useQuery } from '@tanstack/react-query';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { Fragment, useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api, qs } from '../api/client';
import type { PagedEvents, Severity, VcenterSummary, VmwareAlarm, VmwareDatastore, VmwareEvent, VmwareHost, VmwareSnapshot, VmwareVm } from '../api/types';
import { SEVERITIES } from '../api/types';
import { SeverityBadge, UtilBar } from '../components/Badges';
import { DataTable, type Column } from '../components/DataTable';
import { ErrorBox } from '../components/Modal';
import { fmtAgo, fmtBytes, fmtDateTime, fmtDuration, fmtNum, toMs } from '../util/format';

type Tab = 'hosts' | 'vms' | 'datastores' | 'snapshots' | 'alarms' | 'events';

const TABS: { key: Tab; label: string }[] = [
  { key: 'hosts', label: 'Hosts & VMs' },
  { key: 'vms', label: 'Virtual machines' },
  { key: 'datastores', label: 'Datastores' },
  { key: 'snapshots', label: 'Snapshots' },
  { key: 'alarms', label: 'vCenter alarms' },
  { key: 'events', label: 'vCenter events' },
];

function StatusPill({ status }: { status?: string | null }) {
  const cls = status === 'red' ? 's-down' : status === 'yellow' ? 's-warning' : status === 'green' ? 's-up' : 's-unknown';
  return <span className={`badge ${cls}`}>{status ?? 'gray'}</span>;
}

function PowerPill({ state }: { state: string }) {
  const cls = state === 'poweredOn' ? 's-up' : state === 'suspended' ? 's-warning' : 's-unknown';
  return <span className={`badge ${cls}`}>{state === 'poweredOn' ? 'on' : state === 'poweredOff' ? 'off' : state}</span>;
}

function HostState({ h }: { h: VmwareHost }) {
  if (h.inMaintenance) return <span className="badge s-maintenance">maintenance</span>;
  return <span className={`badge ${h.connectionState === 'connected' ? 's-up' : 's-down'}`}>{h.connectionState}</span>;
}

/** "guestToolsNotRunning" → "not running". */
function toolsText(v: VmwareVm): string {
  if (v.powerState !== 'poweredOn') return '—';
  if (v.toolsVersionStatus === 'guestToolsNotInstalled') return 'not installed';
  return v.toolsRunningStatus === 'guestToolsRunning' ? (v.toolsVersionStatus === 'guestToolsNeedUpgrade' ? 'running (upgrade)' : 'running') : 'not running';
}

function VmLink({ v }: { v: { name: string; deviceId?: number | null } }) {
  return v.deviceId ? <Link to={`/devices/${v.deviceId}`} onClick={(e) => e.stopPropagation()}>{v.name}</Link> : <span>{v.name}</span>;
}

function HostsTab({ hosts, vms }: { hosts: VmwareHost[]; vms: VmwareVm[] }) {
  const [open, setOpen] = useState<Set<string>>(new Set());
  const byHost = useMemo(() => {
    const m = new Map<string, VmwareVm[]>();
    for (const v of vms) {
      const key = `${v.sourceDeviceId}|${v.hostName ?? ''}`;
      m.set(key, [...(m.get(key) ?? []), v]);
    }
    return m;
  }, [vms]);
  const toggle = (k: string) => setOpen((s) => {
    const n = new Set(s);
    if (n.has(k)) n.delete(k);
    else n.add(k);
    return n;
  });

  return (
    <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 290px)' }}>
      <table className="data">
        <thead>
          <tr>
            <th style={{ width: 30 }} />
            <th>Host / VM</th>
            <th>State</th>
            <th>Health</th>
            <th style={{ width: 140 }}>CPU</th>
            <th style={{ width: 140 }}>Memory</th>
            <th>VMs</th>
            <th>Model / guest OS</th>
            <th>Uptime</th>
          </tr>
        </thead>
        <tbody>
          {hosts.map((h) => {
            const key = `${h.sourceDeviceId}|${h.name}`;
            const children = byHost.get(key) ?? [];
            const isOpen = open.has(key);
            return (
              <Fragment key={h.id}>
                <tr className="clickable" onClick={() => toggle(key)}>
                  <td>{children.length > 0 && (isOpen ? <ChevronDown size={15} /> : <ChevronRight size={15} />)}</td>
                  <td>
                    <strong>{h.deviceId ? <Link to={`/devices/${h.deviceId}`} onClick={(e) => e.stopPropagation()}>{h.name}</Link> : h.name}</strong>{' '}
                    <span className="muted">{h.cluster}</span>
                  </td>
                  <td>
                    <HostState h={h} />
                  </td>
                  <td>
                    <StatusPill status={h.overallStatus} />
                    {h.sensorAlarms > 0 && <span className="badge s-down" style={{ marginLeft: 4 }}>{h.sensorAlarms} sensor alarm(s)</span>}
                  </td>
                  <td>
                    <UtilBar value={h.cpuPct} />
                  </td>
                  <td>
                    <UtilBar value={h.memPct} />
                  </td>
                  <td className="nowrap">
                    {h.vmsPoweredOn}/{h.vmCount} on
                  </td>
                  <td className="muted">{[h.vendor, h.model].filter(Boolean).join(' ')}</td>
                  <td className="muted nowrap">{h.bootTimeUtc ? fmtDuration((Date.now() - toMs(h.bootTimeUtc)) / 1000) : '—'}</td>
                </tr>
                {isOpen &&
                  children.map((v) => (
                    <tr key={v.id} style={{ background: 'var(--bg-subtle, transparent)' }}>
                      <td />
                      <td style={{ paddingLeft: 24 }}>
                        <VmLink v={v} /> <span className="mono muted">{v.ipAddress}</span>
                      </td>
                      <td>
                        <PowerPill state={v.powerState} />
                      </td>
                      <td>
                        <StatusPill status={v.overallStatus} />
                      </td>
                      <td>
                        <UtilBar value={v.cpuPct} />
                      </td>
                      <td>
                        <UtilBar value={v.memPct} />
                      </td>
                      <td className="muted nowrap">
                        {v.numCpu} vCPU · {fmtBytes((v.memoryMb ?? 0) * 1048576)}
                      </td>
                      <td className="muted">{v.guestOs}</td>
                      <td className="muted nowrap">{v.bootTimeUtc ? fmtDuration((Date.now() - toMs(v.bootTimeUtc)) / 1000) : '—'}</td>
                    </tr>
                  ))}
              </Fragment>
            );
          })}
        </tbody>
      </table>
      {hosts.length === 0 && <div className="empty">No ESXi hosts collected yet.</div>}
    </div>
  );
}

function VmsTab({ vms }: { vms: VmwareVm[] }) {
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const rows = useMemo(() => {
    const t = search.trim().toLowerCase();
    return vms.filter((v) => !t || v.name.toLowerCase().includes(t) || (v.ipAddress ?? '').includes(t) || (v.hostName ?? '').toLowerCase().includes(t) || (v.guestOs ?? '').toLowerCase().includes(t));
  }, [vms, search]);
  const columns: Column<VmwareVm>[] = [
    { key: 'power', header: 'Power', render: (v) => <PowerPill state={v.powerState} />, sort: (v) => v.powerState, width: 80 },
    { key: 'name', header: 'VM', render: (v) => <span className="nowrap"><strong><VmLink v={v} /></strong> <span className="mono muted">{v.ipAddress}</span></span>, sort: (v) => v.name },
    { key: 'host', header: 'Host', render: (v) => <span className="muted">{v.hostName}</span>, sort: (v) => v.hostName ?? '' },
    { key: 'cpu', header: 'CPU', render: (v) => <UtilBar value={v.cpuPct} />, sort: (v) => v.cpuPct ?? -1, width: 120 },
    {
      key: 'ready',
      header: 'CPU ready',
      render: (v) => (v.cpuReadyPct == null ? '—' : <span style={{ color: v.cpuReadyPct > 10 ? 'var(--crit)' : v.cpuReadyPct > 5 ? 'var(--warn)' : undefined }}>{fmtNum(v.cpuReadyPct, 1)}%</span>),
      sort: (v) => v.cpuReadyPct ?? -1,
      className: 'num',
    },
    { key: 'mem', header: 'Memory', render: (v) => <UtilBar value={v.memPct} />, sort: (v) => v.memPct ?? -1, width: 120 },
    { key: 'balloon', header: 'Balloon', render: (v) => (v.balloonedMb ? `${v.balloonedMb} MB` : '—'), sort: (v) => v.balloonedMb ?? 0, className: 'num' },
    { key: 'disk', header: 'Guest disk', render: (v) => <UtilBar value={v.guestDiskMaxPct} />, sort: (v) => v.guestDiskMaxPct ?? -1, width: 120 },
    { key: 'tools', header: 'VMware Tools', render: (v) => <span style={{ color: toolsText(v).startsWith('not') ? 'var(--warn)' : undefined }}>{toolsText(v)}</span>, sort: (v) => toolsText(v) },
    { key: 'snap', header: 'Snapshots', render: (v) => (v.snapshotCount ? v.snapshotCount : ''), sort: (v) => v.snapshotCount, className: 'num' },
    { key: 'ds', header: 'Datastores', render: (v) => <span className="muted nowrap">{v.datastores}</span>, sort: (v) => v.datastores ?? '' },
  ];
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 10 }}>
        <input type="search" placeholder="Search VM, IP, host, guest OS…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <span className="muted">
          {rows.length} of {vms.length} · {vms.filter((v) => v.powerState === 'poweredOn').length} powered on
        </span>
      </div>
      <div className="card">
        <DataTable rows={rows} columns={columns} rowKey={(v) => v.id} onRowClick={(v) => v.deviceId && navigate(`/devices/${v.deviceId}`)} initialSort={{ key: 'name' }} maxHeight="calc(100vh - 330px)" empty="No VMs collected yet." />
      </div>
    </>
  );
}

function DatastoresTab({ datastores }: { datastores: VmwareDatastore[] }) {
  return (
    <div className="card">
      <table className="data">
        <thead>
          <tr>
            <th>Datastore</th>
            <th>Type</th>
            <th style={{ width: 200 }}>Used</th>
            <th className="num">Free</th>
            <th className="num">Capacity</th>
            <th className="num">Provisioned</th>
            <th className="num">Hosts / VMs</th>
            <th>State</th>
          </tr>
        </thead>
        <tbody>
          {datastores.map((d) => {
            const provisioned = d.capacityBytes - d.freeBytes + (d.uncommittedBytes ?? 0);
            return (
              <tr key={d.id}>
                <td>
                  <strong>{d.name}</strong>
                </td>
                <td className="muted">{d.type}</td>
                <td>
                  <UtilBar value={d.usedPct} />
                </td>
                <td className="num">{fmtBytes(d.freeBytes)}</td>
                <td className="num">{fmtBytes(d.capacityBytes)}</td>
                <td className="num" title="Used + uncommitted (thin-provisioned) space">
                  {d.uncommittedBytes != null ? `${fmtBytes(provisioned)} (${fmtNum((100 * provisioned) / Math.max(1, d.capacityBytes), 0)}%)` : '—'}
                </td>
                <td className="num">
                  {d.hostCount} / {d.vmCount}
                </td>
                <td>{d.accessible ? <span className="badge s-up">accessible</span> : <span className="badge s-down">inaccessible</span>}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {datastores.length === 0 && <div className="empty">No datastores collected yet.</div>}
    </div>
  );
}

function SnapshotsTab({ snapshots }: { snapshots: VmwareSnapshot[] }) {
  const total = snapshots.reduce((n, s) => n + (s.sizeBytes ?? 0), 0);
  return (
    <>
      <div className="muted" style={{ marginBottom: 10 }}>
        {snapshots.length} snapshot(s), about {fmtBytes(total)} in total. Snapshots older than 7 days or larger than 50 GB raise alerts (rules “Old snapshot” and “Large snapshot”).
      </div>
      <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 320px)' }}>
        <table className="data">
          <thead>
            <tr>
              <th>VM</th>
              <th>Snapshot</th>
              <th>Created (PKT)</th>
              <th className="num">Age</th>
              <th className="num">Size</th>
              <th>Description</th>
            </tr>
          </thead>
          <tbody>
            {snapshots.map((s) => (
              <tr key={s.id}>
                <td>
                  <VmLink v={{ name: s.vmName, deviceId: s.vmDeviceId }} />
                </td>
                <td>
                  {s.name} {s.isCurrent && <span className="tag">current</span>}
                </td>
                <td className="muted nowrap">{fmtDateTime(s.createdUtc)}</td>
                <td className="num" style={{ color: s.ageDays > 7 ? 'var(--warn)' : undefined }}>
                  {fmtNum(s.ageDays, 0)} d
                </td>
                <td className="num" style={{ color: (s.sizeBytes ?? 0) > 50 * 1073741824 ? 'var(--warn)' : undefined }}>
                  {s.sizeBytes != null ? fmtBytes(s.sizeBytes) : '—'}
                </td>
                <td className="muted">{s.description}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {snapshots.length === 0 && <div className="empty">No snapshots. 👍</div>}
      </div>
    </>
  );
}

function AlarmsTab({ alarms }: { alarms: VmwareAlarm[] }) {
  return (
    <div className="card">
      <table className="data">
        <thead>
          <tr>
            <th>Status</th>
            <th>Alarm</th>
            <th>Object</th>
            <th>Triggered (PKT)</th>
            <th>Acknowledged in vCenter</th>
          </tr>
        </thead>
        <tbody>
          {alarms.map((a) => (
            <tr key={a.id}>
              <td>
                <StatusPill status={a.status} />
              </td>
              <td>{a.alarmName}</td>
              <td>{a.entityName ?? <span className="mono muted">{a.entityMoRef}</span>}</td>
              <td className="muted nowrap">{fmtDateTime(a.triggeredUtc)}</td>
              <td>{a.acknowledged ? 'yes' : 'no'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {alarms.length === 0 && <div className="empty">No triggered vCenter alarms.</div>}
    </div>
  );
}

function EventsTab() {
  const [severity, setSeverity] = useState<Severity | ''>('');
  const [search, setSearch] = useState('');
  const q = useQuery({
    queryKey: ['vmware', 'events', severity, search],
    queryFn: () => api.get<PagedEvents<VmwareEvent>>(`/api/vmware/events${qs({ minSeverity: severity || undefined, search: search || undefined, pageSize: 500 })}`),
    refetchInterval: 30000,
  });
  return (
    <>
      <div className="toolbar" style={{ marginBottom: 10 }}>
        <select value={severity} onChange={(e) => setSeverity(e.target.value as Severity | '')} aria-label="Severity">
          <option value="">All severities</option>
          {SEVERITIES.map((s) => (
            <option key={s} value={s}>
              {s} and above
            </option>
          ))}
        </select>
        <input type="search" placeholder="Search events…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <span className="muted">{q.data ? `${q.data.totalCapped ? '10,000+' : q.data.total} event(s)` : ''}</span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 330px)' }}>
        <table className="data">
          <thead>
            <tr>
              <th>Time (PKT)</th>
              <th>Severity</th>
              <th>Type</th>
              <th>Object</th>
              <th>User</th>
              <th>Message</th>
            </tr>
          </thead>
          <tbody>
            {(q.data?.items ?? []).map((e) => (
              <tr key={e.id}>
                <td className="muted nowrap">{fmtDateTime(e.timestampUtc)}</td>
                <td>
                  <SeverityBadge severity={e.severity} />
                </td>
                <td className="muted nowrap">{e.eventType}</td>
                <td className="nowrap">{e.entityName}</td>
                <td className="muted">{e.userName}</td>
                <td>{e.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {q.data?.items.length === 0 && <div className="empty">No vCenter events imported yet (history before the first poll is not imported).</div>}
      </div>
    </>
  );
}

/** VMware: vCenters, hosts → VMs, datastores, snapshots report, alarms and events. */
export function VmwarePage() {
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) ?? 'hosts';
  const summary = useQuery({ queryKey: ['vmware', 'summary'], queryFn: () => api.get<VcenterSummary[]>('/api/vmware/summary'), refetchInterval: 30000 });
  const hosts = useQuery({ queryKey: ['vmware', 'hosts'], queryFn: () => api.get<VmwareHost[]>('/api/vmware/hosts'), refetchInterval: 30000 });
  const vms = useQuery({ queryKey: ['vmware', 'vms'], queryFn: () => api.get<VmwareVm[]>('/api/vmware/vms'), refetchInterval: 30000 });
  const datastores = useQuery({ queryKey: ['vmware', 'datastores'], queryFn: () => api.get<VmwareDatastore[]>('/api/vmware/datastores'), refetchInterval: 60000 });
  const snapshots = useQuery({ queryKey: ['vmware', 'snapshots'], queryFn: () => api.get<VmwareSnapshot[]>('/api/vmware/snapshots'), refetchInterval: 60000 });
  const alarms = useQuery({ queryKey: ['vmware', 'alarms'], queryFn: () => api.get<VmwareAlarm[]>('/api/vmware/alarms'), refetchInterval: 30000 });

  const counts: Partial<Record<Tab, number>> = {
    hosts: hosts.data?.length,
    vms: vms.data?.length,
    datastores: datastores.data?.length,
    snapshots: snapshots.data?.length,
    alarms: alarms.data?.length,
  };

  return (
    <>
      <div className="page-head">
        <h1>VMware</h1>
      </div>
      <ErrorBox error={summary.error ?? hosts.error ?? vms.error} />
      {summary.data?.length === 0 && (
        <div className="card card-body muted" style={{ marginBottom: 12 }}>
          No vCenter or ESXi host is monitored yet. Add a device of type <em>vCenter</em> (or <em>Hypervisor</em> for a standalone ESXi host) and assign a vCenter credential with the
          read-only role.
        </div>
      )}
      <div className="grid cols-4" style={{ marginBottom: 16 }}>
        {(summary.data ?? []).map((s) => (
          <Link key={s.deviceId} to={`/devices/${s.deviceId}`} className="card stat" style={{ textDecoration: 'none', color: 'inherit' }}>
            <div className="label">{s.name}</div>
            <div className="value" style={{ fontSize: 20 }}>
              {s.vmsPoweredOn}/{s.vms} <span className="muted" style={{ fontSize: 13, fontWeight: 400 }}>VMs on</span>
            </div>
            <div className="breakdown">
              <span style={{ color: s.hostsConnected < s.hosts ? 'var(--crit)' : 'var(--ok)' }}>
                {s.hostsConnected}/{s.hosts} hosts connected
              </span>
              <span>{s.datastores} datastores</span>
              {s.snapshots > 0 && <span>{s.snapshots} snapshots</span>}
              {s.alarms > 0 && <span style={{ color: 'var(--warn)' }}>{s.alarms} alarms</span>}
              <span className="faint">polled {fmtAgo(s.lastPollUtc)}</span>
            </div>
          </Link>
        ))}
      </div>
      <div className="tabs">
        {TABS.map((t) => (
          <button key={t.key} className={tab === t.key ? 'active' : ''} onClick={() => setParams({ tab: t.key }, { replace: true })}>
            {t.label}
            {counts[t.key] ? ` (${counts[t.key]})` : ''}
          </button>
        ))}
      </div>
      {tab === 'hosts' && <HostsTab hosts={hosts.data ?? []} vms={vms.data ?? []} />}
      {tab === 'vms' && <VmsTab vms={vms.data ?? []} />}
      {tab === 'datastores' && <DatastoresTab datastores={datastores.data ?? []} />}
      {tab === 'snapshots' && <SnapshotsTab snapshots={snapshots.data ?? []} />}
      {tab === 'alarms' && <AlarmsTab alarms={alarms.data ?? []} />}
      {tab === 'events' && <EventsTab />}
    </>
  );
}
