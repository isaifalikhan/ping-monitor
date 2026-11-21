import { useQuery } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { DeviceListItem, NetInterface } from '../api/types';
import { StatusBadge, UtilBar } from '../components/Badges';
import { useLive, useLiveEvent } from '../live/LiveContext';
import { fmtBps, fmtSpeed, ifStatusText } from '../util/format';

interface LiveMetrics {
  deviceId: number;
  interfaces: { id: number; operStatus: number; inBps?: number | null; outBps?: number | null; inUtilPct?: number | null; outUtilPct?: number | null }[];
}

function portClass(i: NetInterface): string {
  const util = Math.max(i.inUtilPct ?? 0, i.outUtilPct ?? 0);
  const cls = ['port'];
  if (i.adminStatus !== 1) cls.push('admin-down');
  else if (i.operStatus === 1) cls.push(util >= 80 ? 'hot' : 'up');
  else cls.push(i.isImportant ? 'problem' : 'down');
  if (i.isImportant) cls.push('important');
  if (i.operStatus === 1 && i.duplex === 2) cls.push('half-duplex');
  if ((i.flapCount24h ?? 0) >= 4) cls.push('flappy');
  return cls.join(' ');
}

export function duplexText(d?: number | null): string {
  return d === 2 ? 'half' : d === 3 ? 'full' : 'unknown';
}

function shortName(name: string): string {
  const m = name.match(/(\d+)$/);
  return m ? m[1] : name.slice(-3);
}

/** Port grid of one switch with live colours. */
function SwitchPorts({ device }: { device: DeviceListItem }) {
  const { joinDevice } = useLive();
  const q = useQuery({ queryKey: ['device', device.id, 'interfaces'], queryFn: () => api.get<NetInterface[]>(`/api/devices/${device.id}/interfaces`) });
  const [live, setLive] = useState<LiveMetrics | null>(null);
  useEffect(() => joinDevice(device.id), [device.id, joinDevice]);
  useLiveEvent<LiveMetrics>('deviceMetrics', (m) => m.deviceId === device.id && setLive(m));

  const ports = useMemo(() => {
    const byId = new Map(live?.interfaces.map((i) => [i.id, i]) ?? []);
    return (q.data ?? [])
      .filter((i) => i.ifType === 6 || i.ifType === 117 || i.ifType === 62)
      .map((i) => ({ ...i, ...(byId.get(i.id) ?? {}) }) as NetInterface)
      .sort((a, b) => a.ifIndex - b.ifIndex);
  }, [q.data, live]);

  const uplinks = ports.filter((p) => p.isImportant);
  const up = ports.filter((p) => p.operStatus === 1).length;

  return (
    <div className="card">
      <div className="card-head">
        <StatusBadge status={device.status} />
        <Link to={`/devices/${device.id}?tab=interfaces`}>
          <strong>{device.name}</strong>
        </Link>
        <span className="mono muted">{device.ipAddress}</span>
        <span className="muted">{[device.vendor, device.model].filter(Boolean).join(' ')}</span>
        <span className="spacer" />
        <span className="muted">
          {up}/{ports.length} ports up
        </span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {ports.length === 0 ? (
          <div className="faint">{q.isLoading ? 'Loading…' : 'No Ethernet ports discovered yet.'}</div>
        ) : (
          <div className="port-grid">
            {ports.map((p) => (
              <div
                key={p.id}
                className={portClass(p)}
                title={`${p.name}${p.alias ? ` — ${p.alias}` : ''}\n${p.adminStatus !== 1 ? 'admin down' : ifStatusText(p.operStatus)}, ${fmtSpeed(p.speedBps)}bps, ${duplexText(p.duplex)} duplex\nIn ${fmtBps(p.inBps)} / Out ${fmtBps(p.outBps)}\n${p.flapCount24h ?? 0} up/down change(s) in 24 h`}
              >
                <div className="pn">{shortName(p.name)}</div>
                <div className="pu">{p.operStatus === 1 ? `${Math.round(Math.max(p.inUtilPct ?? 0, p.outUtilPct ?? 0))}%` : '·'}</div>
              </div>
            ))}
          </div>
        )}
        {uplinks.length > 0 && (
          <table className="data">
            <thead>
              <tr>
                <th>Uplink / important port</th>
                <th>Status</th>
                <th className="num">In</th>
                <th className="num">Out</th>
                <th style={{ width: 180 }}>Utilization</th>
              </tr>
            </thead>
            <tbody>
              {uplinks.map((p) => (
                <tr key={p.id}>
                  <td>
                    {p.name} <span className="muted">{p.alias}</span>
                  </td>
                  <td>
                    <span className={`badge ${p.operStatus === 1 ? 's-up' : 's-down'}`}>{ifStatusText(p.operStatus)}</span>
                  </td>
                  <td className="num">{fmtBps(p.inBps)}</td>
                  <td className="num">{fmtBps(p.outBps)}</td>
                  <td>
                    <UtilBar value={p.operStatus === 1 ? Math.max(p.inUtilPct ?? 0, p.outUtilPct ?? 0) : null} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

const PAGE = 6;

export function SwitchesPage() {
  const devices = useQuery({ queryKey: ['devices', 'all'], queryFn: () => api.get<DeviceListItem[]>('/api/devices'), refetchInterval: 30000 });
  const [search, setSearch] = useState('');
  const [problemsOnly, setProblemsOnly] = useState(false);
  const [page, setPage] = useState(0);

  const switches = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (devices.data ?? [])
      .filter((d) => d.type === 'Switch')
      .filter((d) => !problemsOnly || d.status === 'Down' || d.status === 'Warning')
      .filter((d) => !term || d.name.toLowerCase().includes(term) || d.ipAddress.includes(term) || (d.groupPath ?? '').toLowerCase().includes(term))
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
  }, [devices.data, search, problemsOnly]);

  const pages = Math.max(1, Math.ceil(switches.length / PAGE));
  const current = Math.min(page, pages - 1);
  const visible = switches.slice(current * PAGE, current * PAGE + PAGE);

  return (
    <>
      <div className="page-head">
        <h1>Switches</h1>
        <span className="muted">{switches.length} switch(es)</span>
        <span className="spacer" />
        <div className="toolbar">
          <input
            type="search"
            placeholder="Filter by name, IP, group…"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setPage(0);
            }}
          />
          <label className="check">
            <input type="checkbox" checked={problemsOnly} onChange={(e) => setProblemsOnly(e.target.checked)} /> Problems only
          </label>
          <button disabled={current === 0} onClick={() => setPage(current - 1)}>
            ‹ Prev
          </button>
          <span className="muted">
            {current + 1} / {pages}
          </span>
          <button disabled={current >= pages - 1} onClick={() => setPage(current + 1)}>
            Next ›
          </button>
        </div>
      </div>
      <div className="toolbar faint" style={{ marginBottom: 12, fontSize: 12.5 }}>
        <span className="port up" style={{ padding: '1px 8px' }}>up</span>
        <span className="port hot" style={{ padding: '1px 8px' }}>≥80%</span>
        <span className="port down" style={{ padding: '1px 8px' }}>down</span>
        <span className="port problem" style={{ padding: '1px 8px' }}>important down</span>
        <span className="port admin-down" style={{ padding: '1px 8px' }}>admin down</span>
        <span>blue dot = important port</span>
      </div>
      <div className="grid" style={{ gap: 16 }}>
        {visible.map((d) => (
          <SwitchPorts key={d.id} device={d} />
        ))}
        {switches.length === 0 && <div className="empty">{devices.isLoading ? 'Loading…' : 'No switches.'}</div>}
      </div>
    </>
  );
}
