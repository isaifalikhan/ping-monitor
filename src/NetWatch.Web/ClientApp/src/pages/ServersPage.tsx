import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import type { CollectionMethod, ServerListItem } from '../api/types';
import { SeverityBadge, StatusBadge, UtilBar } from '../components/Badges';
import { DataTable, type Column } from '../components/DataTable';
import { ErrorBox } from '../components/Modal';
import { fmtAgo, fmtDuration, fmtNum, toMs, typeLabel } from '../util/format';

export const methodLabel: Record<CollectionMethod, string> = {
  None: '—',
  WinRm: 'WinRM',
  Dcom: 'WMI/DCOM',
  Ssh: 'SSH',
  Snmp: 'SNMP',
};

/** "Microsoft Windows Server 2022 Standard" → "Windows Server 2022 Standard". */
function shortOs(os?: string | null): string | null {
  return os ? os.replace(/^Microsoft\s+/i, '') : null;
}

/** Days since a timestamp (for "last patch" age). */
function ageDays(utc?: string | null): number | null {
  const ms = toMs(utc);
  return Number.isFinite(ms) ? Math.floor((Date.now() - ms) / 86400e3) : null;
}

/** Servers, VMs and domain controllers: resources, disks, services, patch and reboot state. */
export function ServersPage() {
  const navigate = useNavigate();
  const [search, setSearch] = useState('');
  const [filter, setFilter] = useState<'all' | 'problems' | 'reboot' | 'unmanaged'>('all');
  const q = useQuery({ queryKey: ['servers'], queryFn: () => api.get<ServerListItem[]>('/api/servers'), refetchInterval: 30000 });

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (q.data ?? []).filter((s) => {
      if (term && !s.name.toLowerCase().includes(term) && !s.ipAddress.includes(term) && !(s.osName ?? '').toLowerCase().includes(term)) return false;
      switch (filter) {
        case 'problems':
          return s.status === 'Down' || s.status === 'Warning' || s.servicesRunning < s.servicesTotal || (s.worstDiskPct ?? 0) >= 85 || !!s.lastError;
        case 'reboot':
          return s.pendingReboot === true;
        case 'unmanaged':
          return !s.hasManagementCredential;
        default:
          return true;
      }
    });
  }, [q.data, search, filter]);

  const columns: Column<ServerListItem>[] = [
    { key: 'status', header: 'Status', render: (s) => <StatusBadge status={s.status} />, sort: (s) => ['Down', 'Warning', 'Unknown', 'Maintenance', 'Up'].indexOf(s.status), width: 120 },
    {
      key: 'name',
      header: 'Server',
      render: (s) => (
        <span className="nowrap">
          <strong>{s.name}</strong> <span className="mono muted">{s.ipAddress}</span>
        </span>
      ),
      sort: (s) => s.name,
    },
    { key: 'os', header: 'OS', render: (s) => <span className="muted nowrap" title={s.osName ?? ''}>{shortOs(s.osName) ?? typeLabel(s.type)}</span>, sort: (s) => s.osName ?? '' },
    { key: 'cpu', header: 'CPU', render: (s) => <UtilBar value={s.cpuPct} />, sort: (s) => s.cpuPct ?? -1, width: 130 },
    { key: 'mem', header: 'Memory', render: (s) => <UtilBar value={s.memPct} />, sort: (s) => s.memPct ?? -1, width: 130 },
    {
      key: 'disk',
      header: 'Fullest disk',
      render: (s) =>
        s.worstDiskPct != null ? (
          <div title={`Fullest volume: ${s.worstDisk ?? ''}`}>
            <UtilBar value={s.worstDiskPct} />
          </div>
        ) : (
          <span className="faint">—</span>
        ),
      sort: (s) => s.worstDiskPct ?? -1,
      width: 140,
    },
    {
      key: 'svc',
      header: 'Services',
      render: (s) =>
        s.servicesTotal === 0 ? (
          <span className="faint">—</span>
        ) : (
          <span className={`badge ${s.servicesRunning === s.servicesTotal ? 's-up' : 's-down'}`}>
            {s.servicesRunning}/{s.servicesTotal} running
          </span>
        ),
      sort: (s) => (s.servicesTotal === 0 ? 2 : s.servicesRunning === s.servicesTotal ? 1 : 0),
    },
    { key: 'load', header: 'Load (5m)', render: (s) => (s.load5 != null ? `${fmtNum(s.load5, 2)}${s.cpuCount ? ` / ${s.cpuCount} CPU` : ''}` : '—'), sort: (s) => (s.load5 ?? -1) / (s.cpuCount ?? 1), className: 'num' },
    { key: 'uptime', header: 'Uptime', render: (s) => (s.lastBootUtc ? fmtDuration((Date.now() - toMs(s.lastBootUtc)) / 1000) : '—'), sort: (s) => s.lastBootUtc ?? '' },
    {
      key: 'patch',
      header: 'Last patch',
      render: (s) => {
        const days = ageDays(s.lastPatchUtc);
        return days == null ? <span className="faint">—</span> : <span style={{ color: days > 60 ? 'var(--warn)' : undefined }}>{days} d ago</span>;
      },
      sort: (s) => s.lastPatchUtc ?? '',
    },
    { key: 'reboot', header: 'Reboot', render: (s) => (s.pendingReboot ? <span className="badge s-warning">pending</span> : ''), sort: (s) => (s.pendingReboot ? 0 : 1) },
    {
      key: 'method',
      header: 'Collected via',
      render: (s) =>
        !s.hasManagementCredential && s.method !== 'Snmp' ? (
          <span className="faint" title="Assign a Windows or SSH credential to collect disks, services and event logs">ping/SNMP only</span>
        ) : s.lastError ? (
          <span style={{ color: 'var(--crit)' }} title={s.lastError}>
            {s.method ? methodLabel[s.method] : '—'} failing
          </span>
        ) : (
          <span className="muted" title={s.lastSuccessUtc ? `last success ${fmtAgo(s.lastSuccessUtc)}` : ''}>
            {s.method ? methodLabel[s.method] : 'waiting'}
          </span>
        ),
      sort: (s) => (s.lastError ? 0 : 1),
    },
    { key: 'alert', header: 'Alert', render: (s) => (s.worstAlert ? <SeverityBadge severity={s.worstAlert} /> : ''), sort: (s) => ['Info', 'Warning', 'Major', 'Critical'].indexOf(s.worstAlert ?? '') },
  ];

  const all = q.data ?? [];
  const reboot = all.filter((s) => s.pendingReboot).length;
  const failing = all.filter((s) => s.lastError).length;
  const svcDown = all.filter((s) => s.servicesRunning < s.servicesTotal).length;

  return (
    <>
      <div className="page-head">
        <h1>Servers</h1>
        <span className="muted">
          {rows.length} of {all.length}
        </span>
        <span className="spacer" />
        <span className="muted" style={{ fontSize: 13 }}>
          {svcDown > 0 && <span style={{ color: 'var(--crit)' }}>{svcDown} with stopped services · </span>}
          {reboot} pending reboot · {failing > 0 ? <span style={{ color: 'var(--warn)' }}>{failing} collection errors</span> : '0 collection errors'}
        </span>
      </div>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input type="search" placeholder="Search name, IP, OS…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <div className="seg">
          {(
            [
              ['all', 'All'],
              ['problems', 'Problems'],
              ['reboot', 'Reboot pending'],
              ['unmanaged', 'No WinRM/SSH credential'],
            ] as const
          ).map(([k, label]) => (
            <button key={k} className={filter === k ? 'active' : ''} onClick={() => setFilter(k)}>
              {label}
            </button>
          ))}
        </div>
      </div>
      <ErrorBox error={q.error} />
      <div className="card">
        <DataTable
          rows={rows}
          columns={columns}
          rowKey={(s) => s.id}
          onRowClick={(s) => navigate(`/devices/${s.id}?tab=server`)}
          initialSort={{ key: 'status' }}
          maxHeight="calc(100vh - 230px)"
          empty={q.isLoading ? 'Loading…' : 'No servers match. Add devices of type Server, VM or Domain controller.'}
        />
      </div>
    </>
  );
}
