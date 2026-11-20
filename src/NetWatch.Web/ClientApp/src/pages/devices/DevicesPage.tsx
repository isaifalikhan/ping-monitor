import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Radar, Upload, Wrench } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../../api/client';
import type { DeviceListItem, DeviceStatus, DeviceType, Group } from '../../api/types';
import { DEVICE_TYPES } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { SeverityBadge, StatusBadge } from '../../components/Badges';
import { DataTable, type Column } from '../../components/DataTable';
import { ErrorBox, Modal } from '../../components/Modal';
import { fmtAgo, fmtMs, fmtPct, typeLabel } from '../../util/format';
import { groupOptions } from '../settings/GroupsTab';
import { DeviceForm } from './DeviceForm';
import { DiscoveryDialog } from './DiscoveryDialog';
import { ImportDialog } from './ImportDialog';

const STATUSES: DeviceStatus[] = ['Up', 'Warning', 'Down', 'Unknown', 'Maintenance'];

function MaintenanceDialog({ ids, onClose }: { ids: number[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [hours, setHours] = useState(2);
  const [error, setError] = useState<unknown>(null);
  const apply = async (enabled: boolean) => {
    try {
      await api.post('/api/devices/maintenance', {
        deviceIds: ids,
        enabled,
        untilUtc: enabled && hours > 0 ? new Date(Date.now() + hours * 3600e3).toISOString() : null,
      });
      await qc.invalidateQueries({ queryKey: ['devices'] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };
  return (
    <Modal
      title={`Maintenance mode — ${ids.length} device(s)`}
      onClose={onClose}
      footer={
        <>
          <button onClick={() => void apply(false)}>End maintenance</button>
          <button className="primary" onClick={() => void apply(true)}>
            Start maintenance
          </button>
        </>
      }
    >
      <ErrorBox error={error} />
      <div className="muted">Devices in maintenance are still polled, but raise no new alerts. For recurring maintenance use Settings → Maintenance windows.</div>
      <label className="field" style={{ maxWidth: 240 }}>
        <span>Duration (hours, 0 = until ended manually)</span>
        <input type="number" min={0} max={720} value={hours} onChange={(e) => setHours(Number(e.target.value))} />
      </label>
    </Modal>
  );
}

export function DevicesPage() {
  const navigate = useNavigate();
  const { can } = useAuth();
  const [params, setParams] = useSearchParams();
  const [search, setSearch] = useState('');
  const type = (params.get('type') as DeviceType | null) ?? '';
  const status = (params.get('status') as DeviceStatus | null) ?? '';
  const groupId = params.get('group') ?? '';
  const [dialog, setDialog] = useState<'add' | 'import' | 'discover' | 'maintenance' | null>(null);
  const [selected, setSelected] = useState<Set<string | number>>(new Set());

  const devices = useQuery({ queryKey: ['devices', 'all'], queryFn: () => api.get<DeviceListItem[]>('/api/devices'), refetchInterval: 30000 });
  const groups = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });

  const subtree = useMemo(() => {
    if (!groupId) return null;
    const all = groups.data ?? [];
    const ids = new Set([Number(groupId)]);
    let added = true;
    while (added) {
      added = false;
      for (const g of all) if (g.parentId && ids.has(g.parentId) && !ids.has(g.id)) {
        ids.add(g.id);
        added = true;
      }
    }
    return ids;
  }, [groupId, groups.data]);

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (devices.data ?? []).filter(
      (d) =>
        (!type || d.type === type) &&
        (!status || d.status === status) &&
        (!subtree || (d.groupId != null && subtree.has(d.groupId))) &&
        (!term ||
          d.name.toLowerCase().includes(term) ||
          d.ipAddress.includes(term) ||
          (d.vendor ?? '').toLowerCase().includes(term) ||
          (d.model ?? '').toLowerCase().includes(term) ||
          d.tags.some((t) => t.toLowerCase().includes(term))),
    );
  }, [devices.data, search, type, status, subtree]);

  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  const columns: Column<DeviceListItem>[] = [
    {
      key: 'status',
      header: 'Status',
      render: (d) => (
        <span title={d.maintenanceReason ?? (d.unreachableVia ? `Upstream ${d.unreachableVia} unreachable — alerts suppressed` : undefined)}>
          <StatusBadge status={d.status} />
        </span>
      ),
      sort: (d) => ['Down', 'Warning', 'Unknown', 'Maintenance', 'Up'].indexOf(d.status),
      width: 120,
    },
    {
      key: 'name',
      header: 'Name',
      render: (d) => (
        <span className="nowrap">
          <strong>{d.name}</strong>
          {d.unreachableVia && <span className="faint" style={{ fontSize: 12 }}> · behind {d.unreachableVia}</span>}
        </span>
      ),
      sort: (d) => d.name,
    },
    { key: 'ip', header: 'IP address', render: (d) => <span className="mono">{d.ipAddress}</span>, sort: (d) => d.ipAddress.split('.').map((p) => p.padStart(3, '0')).join('.') },
    { key: 'type', header: 'Type', render: (d) => typeLabel(d.type), sort: (d) => d.type },
    { key: 'vendor', header: 'Vendor / model', render: (d) => <span className="muted">{[d.vendor, d.model].filter(Boolean).join(' ') || '—'}</span>, sort: (d) => d.vendor ?? '' },
    { key: 'group', header: 'Group', render: (d) => <span className="muted">{d.groupPath ?? '—'}</span>, sort: (d) => d.groupPath ?? '' },
    { key: 'rtt', header: 'Latency', render: (d) => fmtMs(d.rttMs), sort: (d) => d.rttMs ?? -1, className: 'num' },
    { key: 'cpu', header: 'CPU', render: (d) => fmtPct(d.cpuPct), sort: (d) => d.cpuPct ?? -1, className: 'num' },
    { key: 'mem', header: 'Memory', render: (d) => fmtPct(d.memPct), sort: (d) => d.memPct ?? -1, className: 'num' },
    { key: 'alert', header: 'Alert', render: (d) => (d.worstAlert ? <SeverityBadge severity={d.worstAlert} /> : ''), sort: (d) => ['Info', 'Warning', 'Major', 'Critical'].indexOf(d.worstAlert ?? '') },
    { key: 'poll', header: 'Last poll', render: (d) => <span className="muted nowrap">{d.isEnabled ? fmtAgo(d.lastPollUtc) : 'disabled'}</span>, sort: (d) => d.lastPollUtc ?? '' },
  ];

  return (
    <>
      <div className="page-head">
        <h1>Devices</h1>
        <span className="muted">
          {rows.length} of {devices.data?.length ?? 0}
        </span>
        <span className="spacer" />
        {can('Admin') && (
          <div className="toolbar">
            {selected.size > 0 && (
              <button onClick={() => setDialog('maintenance')}>
                <Wrench size={16} /> Maintenance ({selected.size})
              </button>
            )}
            <button onClick={() => setDialog('discover')}>
              <Radar size={16} /> Discover
            </button>
            <button onClick={() => setDialog('import')}>
              <Upload size={16} /> Import CSV
            </button>
            <button className="primary" onClick={() => setDialog('add')}>
              <Plus size={16} /> Add device
            </button>
          </div>
        )}
      </div>

      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input type="search" placeholder="Search name, IP, vendor, tag…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select value={type} onChange={(e) => setParam('type', e.target.value)} aria-label="Type">
          <option value="">All types</option>
          {DEVICE_TYPES.map((t) => (
            <option key={t} value={t}>
              {typeLabel(t)}
            </option>
          ))}
        </select>
        <select value={status} onChange={(e) => setParam('status', e.target.value)} aria-label="Status">
          <option value="">All statuses</option>
          {STATUSES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
        <select value={groupId} onChange={(e) => setParam('group', e.target.value)} aria-label="Group">
          <option value="">All groups</option>
          {groupOptions(groups.data ?? []).map((g) => (
            <option key={g.id} value={g.id}>
              {g.label}
            </option>
          ))}
        </select>
      </div>

      <ErrorBox error={devices.error} />
      <div className="card">
        <DataTable
          rows={rows}
          columns={columns}
          rowKey={(d) => d.id}
          onRowClick={(d) => navigate(`/devices/${d.id}`)}
          initialSort={{ key: 'status' }}
          selectable={can('Admin')}
          selected={selected}
          onSelectedChange={setSelected}
          empty={devices.isLoading ? 'Loading…' : 'No devices match the filters.'}
          maxHeight="calc(100vh - 230px)"
        />
      </div>

      {dialog === 'add' && <DeviceForm onClose={() => setDialog(null)} onSaved={(id) => navigate(`/devices/${id}`)} />}
      {dialog === 'import' && <ImportDialog onClose={() => setDialog(null)} />}
      {dialog === 'discover' && <DiscoveryDialog onClose={() => setDialog(null)} />}
      {dialog === 'maintenance' && (
        <MaintenanceDialog
          ids={[...selected].map(Number)}
          onClose={() => {
            setDialog(null);
            setSelected(new Set());
          }}
        />
      )}
    </>
  );
}
