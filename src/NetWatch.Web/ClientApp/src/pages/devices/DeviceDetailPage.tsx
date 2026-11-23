import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowLeft, Pencil, RefreshCw, Star, Trash2, Wrench } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api, qs } from '../../api/client';
import type { Alert, DeviceDetail, DeviceEvent, NetInterface, Paged, Sensor, Series } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { SeverityBadge, StatusBadge, UtilBar } from '../../components/Badges';
import { DataTable, type Column } from '../../components/DataTable';
import { MetricChart } from '../../components/MetricChart';
import { Confirm, ErrorBox } from '../../components/Modal';
import { rangeFor, TimeRange, type TimeRangeValue } from '../../components/TimeRange';
import { useLiveEvent } from '../../live/LiveContext';
import { useLive } from '../../live/LiveContext';
import { fmtAgo, fmtBps, fmtDateTime, fmtDuration, fmtMs, fmtNum, fmtPct, fmtSpeed, ifStatusText, typeLabel } from '../../util/format';
import { AlertDetailDialog } from '../alerts/AlertsPage';
import { DeviceForm } from './DeviceForm';
import { MacTablePanel } from './MacTablePanel';
import { PortDetail } from './PortDetail';
import { ServerPanel } from './ServerPanel';
import { VmwarePanel } from './VmwarePanel';

interface LiveMetrics {
  deviceId: number;
  ts: number;
  status: string;
  rttMs?: number | null;
  lossPct?: number | null;
  cpuPct?: number | null;
  memPct?: number | null;
  tempC?: number | null;
  interfaces: { id: number; operStatus: number; inBps?: number | null; outBps?: number | null; inUtilPct?: number | null; outUtilPct?: number | null }[];
}

type Tab = 'overview' | 'server' | 'interfaces' | 'macs' | 'sensors' | 'events' | 'alerts';

const TAB_LABELS: Partial<Record<Tab, string>> = { macs: 'MAC addresses' };
const MAC_TABLE_TYPES = ['Switch', 'Router', 'Other'];

const SERVER_TYPES = ['Server', 'VM', 'DomainController', 'Hypervisor'];

function useRangeKey(range: TimeRangeValue) {
  // Refresh "relative" ranges every minute so graphs keep moving.
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (range.key === 'custom') return;
    const t = window.setInterval(() => setTick((x) => x + 1), 60000);
    return () => window.clearInterval(t);
  }, [range.key]);
  return useMemo(() => {
    if (range.key === 'custom') return range;
    void tick;
    return rangeFor(range.key);
  }, [range, tick]);
}

function DeviceCharts({ deviceId, range }: { deviceId: number; range: TimeRangeValue }) {
  const r = useRangeKey(range);
  const q = useQuery({
    queryKey: ['metrics', 'device', deviceId, r.key, r.from.getTime(), r.to.getTime()],
    queryFn: () => api.get<Series>(`/api/devices/${deviceId}/metrics${qs({ from: r.from.toISOString(), to: r.to.toISOString() })}`),
  });
  const hasCpu = (q.data?.avg.cpuPct ?? []).some((v) => v !== null);
  const hasTemp = (q.data?.avg.tempC ?? []).some((v) => v !== null);
  return (
    <div className="grid cols-2">
      <div className="card">
        <div className="card-head">
          <h3>Latency</h3>
          <span className="spacer" />
          <span className="faint">{q.data?.resolution === 'Raw' ? 'raw samples' : q.data?.resolution === 'FiveMinute' ? '5-minute averages' : 'hourly averages'}</span>
        </div>
        <div className="card-body">
          <MetricChart data={q.data} series={[{ column: 'rttMs', label: 'RTT', area: true }]} formatValue={(v) => fmtMs(v)} showPeak />
        </div>
      </div>
      <div className="card">
        <div className="card-head">
          <h3>Availability &amp; packet loss</h3>
        </div>
        <div className="card-body">
          <MetricChart
            data={q.data}
            series={[
              { column: 'lossPct', label: 'Loss %', color: 'var(--crit)' },
              { column: 'up', label: 'Up %', scale: 100, color: 'var(--ok)' },
            ]}
            formatValue={(v) => `${v.toFixed(0)}%`}
            yMax={100}
          />
        </div>
      </div>
      {hasCpu && (
        <div className="card">
          <div className="card-head">
            <h3>CPU &amp; memory</h3>
          </div>
          <div className="card-body">
            <MetricChart
              data={q.data}
              series={[
                { column: 'cpuPct', label: 'CPU %' },
                { column: 'memPct', label: 'Memory %' },
              ]}
              formatValue={(v) => `${v.toFixed(0)}%`}
              yMax={100}
              threshold={90}
              showPeak
            />
          </div>
        </div>
      )}
      {hasTemp && (
        <div className="card">
          <div className="card-head">
            <h3>Temperature</h3>
          </div>
          <div className="card-body">
            <MetricChart data={q.data} series={[{ column: 'tempC', label: '°C', color: 'var(--major)' }]} formatValue={(v) => `${v.toFixed(0)} °C`} />
          </div>
        </div>
      )}
    </div>
  );
}

function InterfaceChart({ iface, range }: { iface: NetInterface; range: TimeRangeValue }) {
  const r = useRangeKey(range);
  const q = useQuery({
    queryKey: ['metrics', 'iface', iface.id, r.key, r.from.getTime(), r.to.getTime()],
    queryFn: () => api.get<Series>(`/api/interfaces/${iface.id}/metrics${qs({ from: r.from.toISOString(), to: r.to.toISOString() })}`),
  });
  return (
    <div className="grid cols-2">
      <div className="card">
        <div className="card-head">
          <h3>
            {iface.name} traffic <span className="muted">({fmtSpeed(iface.speedBps)}bps)</span>
          </h3>
        </div>
        <div className="card-body">
          <MetricChart
            data={q.data}
            series={[
              { column: 'inBps', label: 'In', area: true },
              { column: 'outBps', label: 'Out' },
            ]}
            formatValue={fmtBps}
            showPeak
          />
        </div>
      </div>
      <div className="card">
        <div className="card-head">
          <h3>{iface.name} errors &amp; discards</h3>
        </div>
        <div className="card-body">
          <MetricChart
            data={q.data}
            series={[
              { column: 'inErrPs', label: 'In errors/s', color: 'var(--crit)' },
              { column: 'outErrPs', label: 'Out errors/s', color: 'var(--major)' },
              { column: 'inDiscPs', label: 'In discards/s', color: 'var(--warn)' },
            ]}
            formatValue={(v) => v.toFixed(2)}
          />
        </div>
      </div>
    </div>
  );
}

export function DeviceDetailPage() {
  const { id } = useParams();
  const deviceId = Number(id);
  const navigate = useNavigate();
  const qc = useQueryClient();
  const { can } = useAuth();
  const { joinDevice } = useLive();
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) ?? 'overview';
  const selectedIf = params.get('if') ? Number(params.get('if')) : null;
  const [range, setRange] = useState<TimeRangeValue>(() => rangeFor('1h'));
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [live, setLive] = useState<LiveMetrics | null>(null);
  const [alertId, setAlertId] = useState<number | null>(null);

  const device = useQuery({ queryKey: ['device', deviceId], queryFn: () => api.get<DeviceDetail>(`/api/devices/${deviceId}`), refetchInterval: 60000 });
  const interfaces = useQuery({ queryKey: ['device', deviceId, 'interfaces'], queryFn: () => api.get<NetInterface[]>(`/api/devices/${deviceId}/interfaces`) });
  const sensors = useQuery({ queryKey: ['device', deviceId, 'sensors'], queryFn: () => api.get<Sensor[]>(`/api/devices/${deviceId}/sensors`), enabled: tab === 'sensors' || tab === 'overview' });
  const events = useQuery({ queryKey: ['device', deviceId, 'events'], queryFn: () => api.get<DeviceEvent[]>(`/api/devices/${deviceId}/events?limit=300`), enabled: tab === 'events' });
  const alerts = useQuery({
    queryKey: ['alerts', 'device', deviceId],
    queryFn: () => api.get<Paged<Alert>>(`/api/alerts?deviceId=${deviceId}&pageSize=200`),
    enabled: tab === 'alerts' || tab === 'overview',
  });

  useEffect(() => joinDevice(deviceId), [deviceId, joinDevice]);
  useLiveEvent<LiveMetrics>('deviceMetrics', (m) => {
    if (m.deviceId !== deviceId) return;
    setLive(m);
  });

  // Merge live per-poll values into the interface list.
  const ifRows = useMemo(() => {
    const byId = new Map(live?.interfaces.map((i) => [i.id, i]) ?? []);
    return (interfaces.data ?? []).map((i) => {
      const l = byId.get(i.id);
      return l ? { ...i, operStatus: l.operStatus, inBps: l.inBps, outBps: l.outBps, inUtilPct: l.inUtilPct, outUtilPct: l.outUtilPct } : i;
    });
  }, [interfaces.data, live]);

  const setTab = (t: Tab) => {
    const next = new URLSearchParams(params);
    next.set('tab', t);
    if (t !== 'interfaces') next.delete('if');
    setParams(next, { replace: true });
  };

  const selectIf = (ifId: number | null) => {
    const next = new URLSearchParams(params);
    if (ifId) next.set('if', String(ifId));
    else next.delete('if');
    setParams(next, { replace: true });
  };

  const run = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
    } catch (e) {
      setError(e);
    }
  };

  const toggleImportant = (iface: NetInterface) =>
    run(async () => {
      await api.put(`/api/interfaces/${iface.id}`, { isImportant: !iface.isImportant });
      await qc.invalidateQueries({ queryKey: ['device', deviceId, 'interfaces'] });
    });

  const d = device.data;
  if (device.isError) return <ErrorBox error={device.error} />;
  if (!d) return <div className="empty">Loading…</div>;
  const isServer = SERVER_TYPES.includes(d.type) || d.os === 'Windows' || d.os === 'Linux';

  const status = d.status;
  const ifColumns: Column<NetInterface>[] = [
    {
      key: 'imp',
      header: <Star size={14} />,
      render: (i) =>
        can('Admin') ? (
          <button
            className="ghost icon"
            title={i.isImportant ? 'Important port (raises port-down alerts) — click to unmark' : 'Mark as important (uplink/server port)'}
            onClick={(e) => {
              e.stopPropagation();
              void toggleImportant(i);
            }}
          >
            <Star size={15} fill={i.isImportant ? 'var(--warn)' : 'none'} color={i.isImportant ? 'var(--warn)' : 'var(--text-faint)'} />
          </button>
        ) : i.isImportant ? (
          <Star size={15} fill="var(--warn)" color="var(--warn)" />
        ) : null,
      sort: (i) => (i.isImportant ? 0 : 1),
      width: 40,
    },
    { key: 'idx', header: '#', render: (i) => i.ifIndex, sort: (i) => i.ifIndex, className: 'num', width: 50 },
    { key: 'name', header: 'Name', render: (i) => <strong>{i.name}</strong>, sort: (i) => i.name },
    { key: 'alias', header: 'Description', render: (i) => <span className="muted">{i.alias || i.description}</span>, sort: (i) => i.alias ?? i.description ?? '' },
    {
      key: 'status',
      header: 'Status',
      render: (i) => (
        <span className={`badge ${i.adminStatus !== 1 ? 's-unknown' : i.operStatus === 1 ? 's-up' : i.isImportant ? 's-down' : 's-unknown'}`}>
          {i.adminStatus !== 1 ? 'admin down' : ifStatusText(i.operStatus)}
        </span>
      ),
      sort: (i) => i.operStatus,
    },
    {
      key: 'speed',
      header: 'Speed',
      render: (i) => {
        const slow = i.operStatus === 1 && (i.expectedSpeedBps ?? 0) > 0 && i.speedBps < (i.expectedSpeedBps ?? 0);
        const text = <span style={{ color: slow ? 'var(--warn)' : undefined }} title={i.expectedSpeedBps ? `Expected ${fmtSpeed(i.expectedSpeedBps)}bps` : 'No expected speed set'}>{fmtSpeed(i.speedBps)}</span>;
        return can('Admin') ? (
          <button
            className="ghost"
            style={{ padding: '0 4px' }}
            title="Set the expected speed (alert when an important port negotiates lower)"
            onClick={(e) => {
              e.stopPropagation();
              const value = window.prompt(`Expected speed of ${i.name} in Mbit/s (0 = no check)`, i.expectedSpeedBps ? String(i.expectedSpeedBps / 1e6) : String(i.speedBps / 1e6));
              if (value === null || !/^\d+(\.\d+)?$/.test(value.trim())) return;
              void api.put(`/api/interfaces/${i.id}`, { expectedSpeedBps: Math.round(Number(value) * 1e6) }).then(() => void qc.invalidateQueries({ queryKey: ['device'] }));
            }}
          >
            {text}
            {i.expectedSpeedBps ? <span className="faint"> / {fmtSpeed(i.expectedSpeedBps)}</span> : null}
          </button>
        ) : (
          text
        );
      },
      sort: (i) => i.speedBps,
      className: 'num',
    },
    { key: 'duplex', header: 'Duplex', render: (i) => (i.duplex === 2 && i.operStatus === 1 ? <span className="badge s-down">half</span> : <span className="muted">{i.duplex === 3 ? 'full' : '—'}</span>), sort: (i) => i.duplex ?? 0 },
    { key: 'flaps', header: 'Flaps 24 h', render: (i) => <span style={{ color: (i.flapCount24h ?? 0) >= 4 ? 'var(--warn)' : undefined }}>{i.flapCount24h ?? 0}</span>, sort: (i) => i.flapCount24h ?? 0, className: 'num' },
    { key: 'in', header: 'In', render: (i) => fmtBps(i.inBps), sort: (i) => i.inBps ?? -1, className: 'num' },
    { key: 'out', header: 'Out', render: (i) => fmtBps(i.outBps), sort: (i) => i.outBps ?? -1, className: 'num' },
    { key: 'util', header: 'Utilization', render: (i) => <UtilBar value={i.inUtilPct == null && i.outUtilPct == null ? null : Math.max(i.inUtilPct ?? 0, i.outUtilPct ?? 0)} />, sort: (i) => Math.max(i.inUtilPct ?? -1, i.outUtilPct ?? -1), width: 160 },
    { key: 'err', header: 'Errors/s', render: (i) => fmtNum(i.errorsPs, 2), sort: (i) => i.errorsPs ?? -1, className: 'num' },
  ];

  const openAlerts = (alerts.data?.items ?? []).filter((a) => a.state === 'Active' || a.state === 'Acknowledged');
  const selectedIface = ifRows.find((i) => i.id === selectedIf);

  return (
    <>
      <div className="page-head">
        <button className="ghost icon" onClick={() => navigate(-1)} aria-label="Back">
          <ArrowLeft size={18} />
        </button>
        <h1>{d.name}</h1>
        <StatusBadge status={status} />
        <span className="mono muted">{d.ipAddress}</span>
        <span className="muted">{typeLabel(d.type)}</span>
        <span className="spacer" />
        <div className="toolbar">
          {can('Operator') && (
            <button onClick={() => void run(() => api.post(`/api/devices/${deviceId}/poll-now`))} title="Poll this device now">
              <RefreshCw size={16} /> Poll now
            </button>
          )}
          {can('Admin') && (
            <>
              <button
                onClick={() =>
                  void run(async () => {
                    await api.post('/api/devices/maintenance', { deviceIds: [deviceId], enabled: d.maintenanceReason !== 'Maintenance mode', untilUtc: null });
                    await device.refetch();
                  })
                }
              >
                <Wrench size={16} /> {d.maintenanceReason === 'Maintenance mode' ? 'End maintenance' : 'Maintenance'}
              </button>
              <button onClick={() => setEditing(true)}>
                <Pencil size={16} /> Edit
              </button>
              <button className="danger" onClick={() => setConfirmDelete(true)}>
                <Trash2 size={16} /> Delete
              </button>
            </>
          )}
        </div>
      </div>
      <ErrorBox error={error} />
      {(d.maintenanceReason || d.unreachableVia) && (
        <div className="card card-body" style={{ marginBottom: 12, borderLeft: `3px solid ${d.unreachableVia ? 'var(--crit)' : 'var(--maint)'}` }}>
          {d.maintenanceReason && (
            <div>
              <Wrench size={14} /> {d.maintenanceReason}
              {d.maintenanceUntilUtc ? ` until ${fmtDateTime(d.maintenanceUntilUtc)}` : ''} — no new alerts are raised for this device.
            </div>
          )}
          {d.unreachableVia && (
            <div>
              Upstream device <strong>{d.unreachableVia}</strong> is unreachable: alerts for this device are suppressed because it is probably a consequence.
            </div>
          )}
        </div>
      )}

      <div className="grid cols-4" style={{ marginBottom: 16 }}>
        <div className="card stat">
          <div className="label">Latency / loss</div>
          <div className="value" style={{ fontSize: 22 }}>
            {fmtMs(live?.rttMs ?? d.rttMs)} <span className="muted" style={{ fontSize: 14 }}>{fmtPct(live?.lossPct ?? d.lossPct)} loss</span>
          </div>
        </div>
        <div className="card stat">
          <div className="label">CPU / memory</div>
          <div className="value" style={{ fontSize: 22 }}>
            {fmtPct(live?.cpuPct ?? d.cpuPct)} <span className="muted" style={{ fontSize: 14 }}>/ {fmtPct(live?.memPct ?? d.memPct)}</span>
          </div>
        </div>
        <div className="card stat">
          <div className="label">Uptime</div>
          <div className="value" style={{ fontSize: 22 }}>
            {fmtDuration(d.uptimeSec)}
          </div>
          <div className="breakdown muted">Status since {fmtDateTime(d.statusSinceUtc)}</div>
        </div>
        <div className="card stat">
          <div className="label">Last poll</div>
          <div className="value" style={{ fontSize: 22 }}>
            {live ? fmtAgo(live.ts) : fmtAgo(d.lastPollUtc)}
          </div>
          <div className="breakdown muted">every {d.pollIntervalSec}s</div>
        </div>
      </div>

      <div className="tabs">
        {(['overview', ...(isServer ? ['server'] : []), 'interfaces', ...(MAC_TABLE_TYPES.includes(d.type) ? ['macs'] : []), 'sensors', 'events', 'alerts'] as Tab[]).map((t) => (
          <button key={t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>
            {TAB_LABELS[t] ?? t[0].toUpperCase() + t.slice(1)}
            {t === 'interfaces' && interfaces.data ? ` (${interfaces.data.length})` : ''}
            {t === 'alerts' && openAlerts.length > 0 ? ` (${openAlerts.length})` : ''}
          </button>
        ))}
      </div>

      {(tab === 'overview' || tab === 'interfaces' || tab === 'server') && (
        <div className="toolbar" style={{ marginBottom: 12 }}>
          <TimeRange value={range} onChange={setRange} />
        </div>
      )}

      {tab === 'overview' && (
        <div className="grid" style={{ gap: 16 }}>
          {openAlerts.length > 0 && (
            <div className="card">
              <div className="card-head">
                <h3>Open alerts</h3>
              </div>
              <div className="card-body flush">
                <table className="data">
                  <tbody>
                    {openAlerts.map((a) => (
                      <tr key={a.id} className="clickable" onClick={() => setAlertId(a.id)}>
                        <td style={{ width: 90 }}>
                          <SeverityBadge severity={a.severity} />
                        </td>
                        <td>{a.title}</td>
                        <td className="muted">{a.message}</td>
                        <td className="muted nowrap">{a.state === 'Acknowledged' ? `ack by ${a.ackByUsername}` : fmtAgo(a.firstSeenUtc)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
          <VmwarePanel device={d} />
          <DeviceCharts deviceId={deviceId} range={range} />
          <div className="card">
            <div className="card-head">
              <h3>Details</h3>
            </div>
            <div className="card-body">
              <dl className="kv">
                <dt>Vendor / model</dt>
                <dd>{[d.vendor, d.model].filter(Boolean).join(' ') || '—'}</dd>
                <dt>Serial number</dt>
                <dd className="mono">{d.serialNumber ?? '—'}</dd>
                <dt>Firmware</dt>
                <dd>{d.firmware ?? '—'}</dd>
                <dt>sysName</dt>
                <dd>{d.sysName ?? '—'}</dd>
                <dt>sysDescr</dt>
                <dd className="muted">{d.sysDescription ?? '—'}</dd>
                <dt>sysObjectID</dt>
                <dd className="mono">{d.sysObjectId ?? '—'}</dd>
                <dt>Group</dt>
                <dd>{d.groupPath ?? '—'}</dd>
                <dt>Location</dt>
                <dd>{d.location ?? '—'}</dd>
                <dt>Upstream device</dt>
                <dd>{d.parentDeviceId ? <Link to={`/devices/${d.parentDeviceId}`}>{d.parentDeviceName}</Link> : '—'}</dd>
                <dt>SNMP credential</dt>
                <dd>{d.credentials.find((c) => c.purpose === 'Snmp')?.profileName ?? 'none (ping only)'}</dd>
                <dt>Windows/SSH credential</dt>
                <dd>{d.credentials.find((c) => c.purpose === 'Management')?.profileName ?? '—'}</dd>
                {d.watchedServices.length > 0 && (
                  <>
                    <dt>Watched services</dt>
                    <dd>{d.watchedServices.join(', ')}</dd>
                  </>
                )}
                <dt>Tags</dt>
                <dd>{d.tags.length ? d.tags.map((t) => <span key={t} className="tag">{t}</span>) : '—'}</dd>
                <dt>Maintenance</dt>
                <dd>{d.maintenanceMode ? `${d.maintenanceReason ?? 'yes'}${d.maintenanceUntilUtc ? ` until ${fmtDateTime(d.maintenanceUntilUtc)}` : ''}` : 'no'}</dd>
                <dt>Notes</dt>
                <dd style={{ whiteSpace: 'pre-wrap' }}>{d.notes ?? '—'}</dd>
              </dl>
            </div>
          </div>
        </div>
      )}

      {tab === 'server' && <ServerPanel device={d} range={range} />}

      {tab === 'macs' && <MacTablePanel deviceId={deviceId} />}

      {tab === 'interfaces' && (
        <div className="grid" style={{ gap: 16 }}>
          {selectedIface && <PortDetail deviceId={deviceId} iface={selectedIface} onClose={() => selectIf(null)} />}
          {selectedIface && <InterfaceChart iface={selectedIface} range={range} />}
          <div className="card">
            <DataTable
              rows={ifRows}
              columns={ifColumns}
              rowKey={(i) => i.id}
              onRowClick={(i) => selectIf(i.id === selectedIf ? null : i.id)}
              rowClassName={(i) => (i.id === selectedIf ? 'selected' : undefined)}
              initialSort={{ key: 'idx' }}
              maxHeight={selectedIface ? '45vh' : '65vh'}
              empty={d.credentials.some((c) => c.purpose === 'Snmp') ? 'No interfaces discovered yet (waiting for the first SNMP poll).' : 'Assign an SNMP credential to collect interfaces.'}
            />
          </div>
          <div className="faint" style={{ fontSize: 12 }}>
            Click a row to show the port details, its MAC addresses and graphs. ★ marks important ports (uplinks, server ports): only these raise port-down alerts by default.
          </div>
        </div>
      )}

      {tab === 'sensors' && (
        <div className="card">
          <table className="data">
            <thead>
              <tr>
                <th>Type</th>
                <th>Name</th>
                <th>State</th>
                <th className="num">Value</th>
                <th>Updated</th>
              </tr>
            </thead>
            <tbody>
              {(sensors.data ?? []).map((s) => (
                <tr key={s.id}>
                  <td>{s.kind}</td>
                  <td>{s.name}</td>
                  <td>
                    <span className={`badge ${s.lastState === 'Ok' ? 's-up' : s.lastState === 'Warning' ? 's-warning' : s.lastState === 'Critical' ? 's-down' : 's-unknown'}`}>{s.lastState}</span>
                  </td>
                  <td className="num">{s.lastValue != null ? `${fmtNum(s.lastValue, 1)} ${s.unit ?? ''}` : '—'}</td>
                  <td className="muted">{fmtAgo(s.lastSeenUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {sensors.data?.length === 0 && <div className="empty">No hardware sensors reported.</div>}
        </div>
      )}

      {tab === 'events' && (
        <div className="toolbar" style={{ marginBottom: 10 }}>
          <span className="muted">Status, port, service and inventory changes. Also:</span>
          <Link to={`/events?tab=syslog&deviceId=${deviceId}`}>Syslog</Link>
          <Link to={`/events?tab=traps&deviceId=${deviceId}`}>SNMP traps</Link>
          {d.os === 'Windows' && <Link to={`/events?tab=windows&deviceId=${deviceId}`}>Windows event logs</Link>}
        </div>
      )}
      {tab === 'events' && (
        <div className="card table-wrap" style={{ maxHeight: '70vh' }}>
          <table className="data">
            <thead>
              <tr>
                <th>Time (PKT)</th>
                <th>Severity</th>
                <th>Type</th>
                <th>Message</th>
              </tr>
            </thead>
            <tbody>
              {(events.data ?? []).map((e) => (
                <tr key={e.id}>
                  <td className="nowrap muted">{fmtDateTime(e.timestampUtc)}</td>
                  <td>
                    <SeverityBadge severity={e.severity} />
                  </td>
                  <td className="muted">{e.kind}</td>
                  <td>{e.message}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {events.data?.length === 0 && <div className="empty">No events recorded.</div>}
        </div>
      )}

      {tab === 'alerts' && (
        <div className="card table-wrap" style={{ maxHeight: '70vh' }}>
          <table className="data">
            <thead>
              <tr>
                <th>Severity</th>
                <th>State</th>
                <th>Alert</th>
                <th>First seen</th>
                <th>Resolved</th>
              </tr>
            </thead>
            <tbody>
              {(alerts.data?.items ?? []).map((a) => (
                <tr key={a.id} className="clickable" onClick={() => setAlertId(a.id)}>
                  <td>
                    <SeverityBadge severity={a.severity} />
                  </td>
                  <td>{a.state}</td>
                  <td>
                    {a.title}
                    <div className="muted" style={{ fontSize: 12.5 }}>
                      {a.message}
                    </div>
                  </td>
                  <td className="nowrap muted">{fmtDateTime(a.firstSeenUtc)}</td>
                  <td className="nowrap muted">{a.resolvedUtc ? fmtDateTime(a.resolvedUtc) : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {alerts.data?.items.length === 0 && <div className="empty">No alerts for this device.</div>}
        </div>
      )}

      {editing && <DeviceForm device={d} onClose={() => setEditing(false)} />}
      {alertId !== null && <AlertDetailDialog alertId={alertId} onClose={() => setAlertId(null)} />}
      {confirmDelete && (
        <Confirm
          title={`Delete ${d.name}?`}
          danger
          confirmLabel="Delete device"
          message="The device, its interfaces and sensors are removed. Historical graphs for it can no longer be opened. This cannot be undone."
          onClose={() => setConfirmDelete(false)}
          onConfirm={() =>
            void run(async () => {
              await api.del(`/api/devices/${deviceId}`);
              await qc.invalidateQueries({ queryKey: ['devices'] });
              navigate('/devices');
            })
          }
        />
      )}
    </>
  );
}
