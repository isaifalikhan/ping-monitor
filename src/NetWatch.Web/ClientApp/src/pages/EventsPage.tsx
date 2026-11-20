import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, qs } from '../api/client';
import type { DeviceEvent, DeviceListItem, PagedEvents, Severity, SyslogEntry, TrapEntry, WindowsEvent } from '../api/types';
import { SEVERITIES } from '../api/types';
import { SeverityBadge } from '../components/Badges';
import { ErrorBox, Modal } from '../components/Modal';
import { rangeFor, TimeRange, type TimeRangeValue } from '../components/TimeRange';
import { fmtDateTime } from '../util/format';

type Tab = 'device' | 'syslog' | 'traps' | 'windows';

const TABS: { key: Tab; label: string }[] = [
  { key: 'device', label: 'Device events' },
  { key: 'syslog', label: 'Syslog' },
  { key: 'traps', label: 'SNMP traps' },
  { key: 'windows', label: 'Windows event logs' },
];

const PAGE_SIZE = 200;

/** Syslog severity badge: 0–2 critical, 3 error, 4 warning, 5–7 info. */
export function SyslogSeverity({ severity, name }: { severity: number; name: string }) {
  const cls = severity <= 2 ? 'sev-critical' : severity === 3 ? 'sev-major' : severity === 4 ? 'sev-warning' : 'sev-info';
  return <span className={`badge ${cls}`}>{name}</span>;
}

export function WindowsLevel({ level }: { level: number }) {
  const [cls, name] = level === 1 ? ['sev-critical', 'Critical'] : level === 2 ? ['sev-major', 'Error'] : level === 3 ? ['sev-warning', 'Warning'] : ['sev-info', 'Information'];
  return <span className={`badge ${cls}`}>{name}</span>;
}

function useDebounced<T>(value: T, ms = 400): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

function Pager({ page, total, capped, onPage }: { page: number; total: number; capped: boolean; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  return (
    <div className="toolbar" style={{ justifyContent: 'flex-end', marginTop: 8 }}>
      <span className="muted">
        {capped ? 'more than 10,000' : total.toLocaleString()} entries · page {page}
        {!capped && ` of ${pages}`}
      </span>
      <button className="small" disabled={page <= 1} onClick={() => onPage(page - 1)}>
        Newer
      </button>
      <button className="small" disabled={!capped && page >= pages} onClick={() => onPage(page + 1)}>
        Older
      </button>
    </div>
  );
}

function TrapDialog({ trap, onClose }: { trap: TrapEntry; onClose: () => void }) {
  let varbinds: { oid: string; name?: string | null; value: string }[] = [];
  try {
    varbinds = JSON.parse(trap.varbindsJson);
  } catch {
    /* malformed: show nothing */
  }
  return (
    <Modal title={trap.trapName ?? trap.trapOid} onClose={onClose} wide>
      <dl className="kv">
        <dt>Received</dt>
        <dd>{fmtDateTime(trap.receivedUtc)}</dd>
        <dt>Source</dt>
        <dd>
          <span className="mono">{trap.sourceIp}</span> {trap.deviceName && <>({trap.deviceName})</>}
        </dd>
        <dt>Version</dt>
        <dd>{trap.version}</dd>
        <dt>Trap OID</dt>
        <dd className="mono">{trap.trapOid}</dd>
        <dt>Summary</dt>
        <dd>{trap.summary}</dd>
      </dl>
      <h3>Variable bindings</h3>
      <table className="data">
        <tbody>
          {varbinds.map((v, i) => (
            <tr key={i}>
              <td className="mono" style={{ width: '45%' }}>
                {v.name ? (
                  <>
                    {v.name} <span className="faint">{v.oid}</span>
                  </>
                ) : (
                  v.oid
                )}
              </td>
              <td className="mono">{v.value}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {varbinds.length === 0 && <div className="empty">No variable bindings.</div>}
    </Modal>
  );
}

/** Device events, syslog, SNMP traps and Windows event-log entries with filters (server-side paging). */
export function EventsPage() {
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) ?? 'device';
  const deviceId = params.get('deviceId') ?? '';
  const [range, setRange] = useState<TimeRangeValue>(() => rangeFor('24h'));
  const [search, setSearch] = useState('');
  const [severity, setSeverity] = useState('');
  const [page, setPage] = useState(1);
  const [trap, setTrap] = useState<TrapEntry | null>(null);
  const term = useDebounced(search);

  useEffect(() => setPage(1), [tab, deviceId, term, severity, range]);
  useEffect(() => setSeverity(''), [tab]);

  const devices = useQuery({ queryKey: ['devices', 'all'], queryFn: () => api.get<DeviceListItem[]>('/api/devices'), staleTime: 60000 });
  const filters = { deviceId: deviceId || undefined, search: term || undefined, from: range.from.toISOString(), to: range.key === 'custom' ? range.to.toISOString() : undefined };

  const device = useQuery({
    queryKey: ['events', 'device', filters, severity],
    queryFn: () => api.get<DeviceEvent[]>(`/api/events${qs({ ...filters, minSeverity: severity || undefined, limit: 2000 })}`),
    enabled: tab === 'device',
    refetchInterval: 30000,
  });
  const syslog = useQuery({
    queryKey: ['events', 'syslog', filters, severity, page],
    queryFn: () => api.get<PagedEvents<SyslogEntry>>(`/api/syslog${qs({ ...filters, maxSeverity: severity || undefined, page, pageSize: PAGE_SIZE })}`),
    enabled: tab === 'syslog',
    refetchInterval: 15000,
  });
  const traps = useQuery({
    queryKey: ['events', 'traps', filters, severity, page],
    queryFn: () => api.get<PagedEvents<TrapEntry>>(`/api/traps${qs({ ...filters, minSeverity: severity || undefined, page, pageSize: PAGE_SIZE })}`),
    enabled: tab === 'traps',
    refetchInterval: 15000,
  });
  const windows = useQuery({
    queryKey: ['events', 'windows', filters, severity, page],
    queryFn: () => api.get<PagedEvents<WindowsEvent>>(`/api/windows-events${qs({ ...filters, maxLevel: severity || undefined, page, pageSize: PAGE_SIZE })}`),
    enabled: tab === 'windows',
    refetchInterval: 30000,
  });

  const setParam = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  const deviceLink = (id?: number | null, name?: string | null, fallback?: string) =>
    id ? <Link to={`/devices/${id}`}>{name ?? `#${id}`}</Link> : <span className="mono muted">{fallback ?? '—'}</span>;

  return (
    <>
      <div className="page-head">
        <h1>Events &amp; logs</h1>
      </div>
      <div className="tabs">
        {TABS.map((t) => (
          <button key={t.key} className={tab === t.key ? 'active' : ''} onClick={() => setParam('tab', t.key)}>
            {t.label}
          </button>
        ))}
      </div>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <select value={deviceId} onChange={(e) => setParam('deviceId', e.target.value)} aria-label="Device" style={{ maxWidth: 260 }}>
          <option value="">All devices</option>
          {(devices.data ?? [])
            .slice()
            .sort((a, b) => a.name.localeCompare(b.name))
            .map((d) => (
              <option key={d.id} value={d.id}>
                {d.name}
              </option>
            ))}
        </select>
        {tab === 'syslog' && (
          <select value={severity} onChange={(e) => setSeverity(e.target.value)} aria-label="Severity">
            <option value="">All severities</option>
            <option value="2">Critical and worse (0–2)</option>
            <option value="3">Error and worse (0–3)</option>
            <option value="4">Warning and worse (0–4)</option>
            <option value="5">Notice and worse (0–5)</option>
          </select>
        )}
        {tab === 'windows' && (
          <select value={severity} onChange={(e) => setSeverity(e.target.value)} aria-label="Level">
            <option value="">All stored levels</option>
            <option value="1">Critical only</option>
          </select>
        )}
        {(tab === 'device' || tab === 'traps') && (
          <select value={severity} onChange={(e) => setSeverity(e.target.value)} aria-label="Severity">
            <option value="">All severities</option>
            {SEVERITIES.map((s: Severity) => (
              <option key={s} value={s}>
                {s} and above
              </option>
            ))}
          </select>
        )}
        <input type="search" placeholder="Search text…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <TimeRange value={range} onChange={setRange} />
      </div>

      {tab === 'device' && (
        <>
          <ErrorBox error={device.error} />
          <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 280px)' }}>
            <table className="data">
              <thead>
                <tr>
                  <th>Time (PKT)</th>
                  <th>Severity</th>
                  <th>Device</th>
                  <th>Type</th>
                  <th>Message</th>
                </tr>
              </thead>
              <tbody>
                {(device.data ?? []).map((e) => (
                  <tr key={e.id}>
                    <td className="nowrap muted">{fmtDateTime(e.timestampUtc)}</td>
                    <td>
                      <SeverityBadge severity={e.severity} />
                    </td>
                    <td className="nowrap">{deviceLink(e.deviceId, e.deviceName)}</td>
                    <td className="muted">{e.kind}</td>
                    <td>{e.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {device.data?.length === 0 && <div className="empty">No events in this period.</div>}
          </div>
        </>
      )}

      {tab === 'syslog' && (
        <>
          <ErrorBox error={syslog.error} />
          <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 310px)' }}>
            <table className="data">
              <thead>
                <tr>
                  <th>Received (PKT)</th>
                  <th>Severity</th>
                  <th>Source</th>
                  <th>App</th>
                  <th>Message</th>
                </tr>
              </thead>
              <tbody>
                {(syslog.data?.items ?? []).map((m) => (
                  <tr key={m.id}>
                    <td className="nowrap muted">{fmtDateTime(m.receivedUtc)}</td>
                    <td>
                      <SyslogSeverity severity={m.severity} name={m.severityName} />
                    </td>
                    <td className="nowrap">{deviceLink(m.deviceId, m.deviceName, m.sourceIp)}</td>
                    <td className="muted nowrap">{m.appName ?? ''}</td>
                    <td style={{ wordBreak: 'break-word' }}>{m.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {syslog.data?.items.length === 0 && <div className="empty">No syslog messages. Point devices at this server (UDP/TCP 514).</div>}
          </div>
          {syslog.data && <Pager page={page} total={syslog.data.total} capped={syslog.data.totalCapped} onPage={setPage} />}
        </>
      )}

      {tab === 'traps' && (
        <>
          <ErrorBox error={traps.error} />
          <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 310px)' }}>
            <table className="data">
              <thead>
                <tr>
                  <th>Received (PKT)</th>
                  <th>Severity</th>
                  <th>Source</th>
                  <th>Trap</th>
                  <th>Summary</th>
                </tr>
              </thead>
              <tbody>
                {(traps.data?.items ?? []).map((t) => (
                  <tr key={t.id} className="clickable" onClick={() => setTrap(t)}>
                    <td className="nowrap muted">{fmtDateTime(t.receivedUtc)}</td>
                    <td>
                      <SeverityBadge severity={t.severity} />
                    </td>
                    <td className="nowrap">{deviceLink(t.deviceId, t.deviceName, t.sourceIp)}</td>
                    <td className="nowrap">{t.trapName ?? <span className="mono muted">{t.trapOid}</span>}</td>
                    <td>{t.summary}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {traps.data?.items.length === 0 && <div className="empty">No traps. Configure devices to send SNMP traps to this server (UDP 162).</div>}
          </div>
          {traps.data && <Pager page={page} total={traps.data.total} capped={traps.data.totalCapped} onPage={setPage} />}
        </>
      )}

      {tab === 'windows' && (
        <>
          <ErrorBox error={windows.error} />
          <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 310px)' }}>
            <table className="data">
              <thead>
                <tr>
                  <th>Time (PKT)</th>
                  <th>Level</th>
                  <th>Server</th>
                  <th>Log</th>
                  <th>Source</th>
                  <th className="num">Event ID</th>
                  <th>Message</th>
                </tr>
              </thead>
              <tbody>
                {(windows.data?.items ?? []).map((e) => (
                  <tr key={e.id}>
                    <td className="nowrap muted">{fmtDateTime(e.timestampUtc)}</td>
                    <td>
                      <WindowsLevel level={e.level} />
                    </td>
                    <td className="nowrap">{deviceLink(e.deviceId, e.deviceName)}</td>
                    <td className="muted">{e.logName}</td>
                    <td className="muted">{e.provider}</td>
                    <td className="num">{e.eventId}</td>
                    <td style={{ wordBreak: 'break-word' }}>{e.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {windows.data?.items.length === 0 && <div className="empty">No Critical/Error entries in this period.</div>}
          </div>
          {windows.data && <Pager page={page} total={windows.data.total} capped={windows.data.totalCapped} onPage={setPage} />}
        </>
      )}

      {trap && <TrapDialog trap={trap} onClose={() => setTrap(null)} />}
    </>
  );
}
