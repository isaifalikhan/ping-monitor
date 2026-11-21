import { useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { KeyRound, Pencil } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, qs } from '../../api/client';
import type { DeviceDetail, PagedEvents, Series, SeriesInfo, ServerDetail, ServiceState, WindowsEvent } from '../../api/types';
import { GpuPanel, HardwarePanel } from './HardwarePanels';
import { useAuth } from '../../auth/AuthContext';
import { UtilBar } from '../../components/Badges';
import { MetricChart, type SeriesSpec } from '../../components/MetricChart';
import { Confirm, ErrorBox, Modal } from '../../components/Modal';
import type { TimeRangeValue } from '../../components/TimeRange';
import { fmtAgo, fmtBps, fmtBytes, fmtDateTime, fmtDuration, fmtNum, toMs } from '../../util/format';
import { WindowsLevel } from '../EventsPage';
import { methodLabel } from '../ServersPage';

const stateText: Record<ServiceState, string> = {
  Unknown: 'unknown',
  Running: 'running',
  Stopped: 'stopped',
  StartPending: 'starting',
  StopPending: 'stopping',
  Paused: 'paused',
  Failed: 'failed',
  NotFound: 'not installed',
};

/** Merges several single-value series (same poll timestamps) into one chart-friendly series. */
function merge(parts: { label: string; data?: Series }[]): Series | undefined {
  const present = parts.filter((p) => p.data);
  if (present.length === 0) return undefined;
  const ts = [...new Set(present.flatMap((p) => p.data!.ts))].sort((a, b) => a - b);
  const index = new Map(ts.map((t, i) => [t, i]));
  const avg: Record<string, (number | null)[]> = {};
  const max: Record<string, (number | null)[]> = {};
  let hasMax = false;
  for (const p of present) {
    const a: (number | null)[] = new Array(ts.length).fill(null);
    const m: (number | null)[] = new Array(ts.length).fill(null);
    p.data!.ts.forEach((t, i) => {
      a[index.get(t)!] = p.data!.avg.value?.[i] ?? null;
      m[index.get(t)!] = p.data!.max?.value?.[i] ?? null;
    });
    avg[p.label] = a;
    max[p.label] = m;
    hasMax ||= !!p.data!.max;
  }
  return { resolution: present[0].data!.resolution, ts, avg, max: hasMax ? max : null };
}

function SeriesChart({ title, series, range, format, yMax, threshold }: {
  title: string;
  series: SeriesInfo[];
  range: TimeRangeValue;
  format: (v: number) => string;
  yMax?: number;
  threshold?: number;
}) {
  const results = useQueries({
    queries: series.map((s) => ({
      queryKey: ['metrics', 'series', s.id, range.key, range.from.getTime(), range.to.getTime()],
      queryFn: () => api.get<Series>(`/api/series/${s.id}/metrics${qs({ from: range.from.toISOString(), to: range.to.toISOString() })}`),
    })),
  });
  const labels = series.map((s) => s.instance || s.displayName);
  const data = merge(series.map((_, i) => ({ label: labels[i], data: results[i].data })));
  const specs: SeriesSpec[] = labels.map((l) => ({ column: l, label: l }));
  return (
    <div className="card">
      <div className="card-head">
        <h3>{title}</h3>
      </div>
      <div className="card-body">
        <MetricChart data={data} series={specs} formatValue={format} yMax={yMax} threshold={threshold} />
      </div>
    </div>
  );
}

function ServicesDialog({ device, onClose }: { device: DeviceDetail; onClose: () => void }) {
  const qc = useQueryClient();
  const [text, setText] = useState(device.watchedServices.join('\n'));
  const [error, setError] = useState<unknown>(null);
  const save = async () => {
    try {
      const services = text.split(/[\n,;]/).map((s) => s.trim()).filter(Boolean);
      await api.put(`/api/devices/${device.id}/services`, { services });
      await qc.invalidateQueries({ queryKey: ['device', device.id] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };
  return (
    <Modal
      title={`Watched services — ${device.name}`}
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Cancel</button>
          <button className="primary" onClick={() => void save()}>
            Save
          </button>
        </>
      }
    >
      <ErrorBox error={error} />
      <label className="field">
        <span>Service names, one per line</span>
        <textarea rows={8} className="mono" value={text} onChange={(e) => setText(e.target.value)} />
        <span className="hint">
          Windows: the service <em>name</em> (e.g. W3SVC, MSSQLSERVER, Spooler), not the display name. Linux: the systemd unit (e.g. sshd, nginx). An alert is raised
          when a watched service is not running.
        </span>
      </label>
    </Modal>
  );
}

/** "Server" tab of the device page. */
export function ServerPanel({ device, range }: { device: DeviceDetail; range: TimeRangeValue }) {
  const { can } = useAuth();
  const qc = useQueryClient();
  const [editServices, setEditServices] = useState(false);
  const [resetKey, setResetKey] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const detail = useQuery({ queryKey: ['device', device.id, 'server'], queryFn: () => api.get<ServerDetail>(`/api/devices/${device.id}/server`), refetchInterval: 60000 });
  const series = useQuery({ queryKey: ['device', device.id, 'series'], queryFn: () => api.get<SeriesInfo[]>(`/api/devices/${device.id}/series`), refetchInterval: 300000 });
  const events = useQuery({
    queryKey: ['device', device.id, 'windows-events'],
    queryFn: () => api.get<PagedEvents<WindowsEvent>>(`/api/windows-events${qs({ deviceId: device.id, pageSize: 15 })}`),
    enabled: device.os === 'Windows',
    refetchInterval: 60000,
  });

  const groups = useMemo(() => {
    const all = series.data ?? [];
    return {
      disk: all.filter((s) => s.key === 'disk.used.pct'),
      net: all.filter((s) => s.key === 'net.in.bps' || s.key === 'net.out.bps').map((s) => ({ ...s, instance: s.key === 'net.in.bps' ? 'In' : 'Out' })),
      load: all.filter((s) => s.key === 'load.per.cpu').map((s) => ({ ...s, instance: 'Load per CPU' })),
    };
  }, [series.data]);

  const info = detail.data?.info;
  const management = device.credentials.find((c) => c.purpose === 'Management');

  return (
    <div className="grid" style={{ gap: 16 }}>
      <ErrorBox error={error ?? detail.error} />
      {!management && (
        <div className="card card-body muted">
          No Windows or SSH credential is assigned, so only ping/SNMP data is collected
          {detail.data?.volumes.length ? ' (disks come from SNMP HOST-RESOURCES-MIB)' : ''}. Assign a credential in <em>Edit</em> to collect disks, services, patches and event logs.
        </div>
      )}
      {info?.lastError && (
        <div className="error-box">
          {methodLabel[info.method]} collection failed {fmtAgo(info.lastAttemptUtc)}: {info.lastError}
        </div>
      )}

      <div className="grid cols-2">
        <div className="card">
          <div className="card-head">
            <h3>Operating system</h3>
          </div>
          <div className="card-body">
            {info ? (
              <dl className="kv">
                <dt>OS</dt>
                <dd>
                  {info.osName ?? '—'} <span className="muted">{info.osVersion}</span>
                </dd>
                <dt>Collected via</dt>
                <dd>
                  {methodLabel[info.method]} {info.lastSuccessUtc && <span className="muted">· last success {fmtAgo(info.lastSuccessUtc)}</span>}
                </dd>
                <dt>Last boot</dt>
                <dd>{info.lastBootUtc ? `${fmtDateTime(info.lastBootUtc)} (up ${fmtDuration((Date.now() - toMs(info.lastBootUtc)) / 1000)})` : '—'}</dd>
                <dt>Pending reboot</dt>
                <dd>{info.pendingReboot == null ? '—' : info.pendingReboot ? <span className="badge s-warning">yes</span> : 'no'}</dd>
                <dt>Last patch</dt>
                <dd>{info.lastPatchUtc ? `${fmtDateTime(info.lastPatchUtc).slice(0, 10)} ${info.lastPatchId ?? ''}` : '—'}</dd>
                <dt>CPUs / memory</dt>
                <dd>
                  {info.cpuCount ?? '—'} logical CPUs · {fmtBytes(info.totalMemoryBytes)}
                </dd>
                {info.load1 != null && (
                  <>
                    <dt>Load average</dt>
                    <dd>
                      {fmtNum(info.load1, 2)} / {fmtNum(info.load5, 2)} / {fmtNum(info.load15, 2)} <span className="muted">(1/5/15 min)</span>
                    </dd>
                  </>
                )}
                {info.eventLogError && (
                  <>
                    <dt>Event logs</dt>
                    <dd style={{ color: 'var(--warn)' }}>{info.eventLogError}</dd>
                  </>
                )}
                {device.sshHostKeyFingerprint && (
                  <>
                    <dt>SSH host key</dt>
                    <dd>
                      <span className="mono" style={{ fontSize: 12 }}>
                        SHA256:{device.sshHostKeyFingerprint}
                      </span>{' '}
                      {can('Admin') && (
                        <button className="small" onClick={() => setResetKey(true)} title="Accept a new host key at the next connection (after a reinstall)">
                          <KeyRound size={13} /> Reset
                        </button>
                      )}
                    </dd>
                  </>
                )}
              </dl>
            ) : (
              <div className="empty">{management ? 'Waiting for the first collection…' : 'No server data.'}</div>
            )}
          </div>
        </div>

        <div className="card">
          <div className="card-head">
            <h3>Disks</h3>
          </div>
          <div className="card-body flush">
            <table className="data">
              <thead>
                <tr>
                  <th>Volume</th>
                  <th style={{ width: 170 }}>Used</th>
                  <th className="num">Free</th>
                  <th className="num">Size</th>
                </tr>
              </thead>
              <tbody>
                {(detail.data?.volumes ?? []).map((v) => (
                  <tr key={v.id}>
                    <td>
                      <strong>{v.name}</strong> <span className="muted">{[v.label, v.fileSystem].filter(Boolean).join(' · ')}</span>
                    </td>
                    <td>
                      <UtilBar value={v.usedPct} />
                    </td>
                    <td className="num">{fmtBytes(v.freeBytes)}</td>
                    <td className="num">{fmtBytes(v.totalBytes)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {detail.data?.volumes.length === 0 && <div className="empty">No volumes reported.</div>}
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <h3>Watched services</h3>
          <span className="spacer" />
          {can('Admin') && (
            <button className="small" onClick={() => setEditServices(true)}>
              <Pencil size={13} /> Edit list
            </button>
          )}
        </div>
        <div className="card-body flush">
          <table className="data">
            <tbody>
              {(detail.data?.services ?? []).map((s) => (
                <tr key={s.id}>
                  <td style={{ width: 130 }}>
                    <span className={`badge ${s.state === 'Running' ? 's-up' : s.state === 'Unknown' ? 's-unknown' : 's-down'}`}>{stateText[s.state]}</span>
                  </td>
                  <td>
                    <strong>{s.serviceName}</strong> <span className="muted">{s.displayName && s.displayName !== s.serviceName ? s.displayName : ''}</span>
                  </td>
                  <td className="muted">{s.startMode ?? ''}</td>
                  <td className="muted nowrap">{s.lastChangeUtc ? `changed ${fmtAgo(s.lastChangeUtc)}` : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {detail.data?.services.length === 0 && <div className="empty">No services watched. Use “Edit list” to add Windows services or systemd units.</div>}
        </div>
      </div>

      <div className="grid cols-2">
        {groups.disk.length > 0 && <SeriesChart title="Disk used" series={groups.disk} range={range} format={(v) => `${v.toFixed(0)}%`} yMax={100} threshold={85} />}
        {groups.net.length > 0 && <SeriesChart title="Network throughput (all adapters)" series={groups.net} range={range} format={fmtBps} />}
        {groups.load.length > 0 && <SeriesChart title="Load average per CPU (5 min)" series={groups.load} range={range} format={(v) => v.toFixed(2)} />}
      </div>

      {device.os === 'Windows' && (
        <div className="card">
          <div className="card-head">
            <h3>Recent Critical/Error events</h3>
            <span className="spacer" />
            <Link to={`/events?tab=windows&deviceId=${device.id}`}>All events →</Link>
          </div>
          <div className="card-body flush table-wrap" style={{ maxHeight: 360 }}>
            <table className="data">
              <tbody>
                {(events.data?.items ?? []).map((e) => (
                  <tr key={e.id}>
                    <td className="nowrap muted" style={{ width: 160 }}>
                      {fmtDateTime(e.timestampUtc)}
                    </td>
                    <td style={{ width: 100 }}>
                      <WindowsLevel level={e.level} />
                    </td>
                    <td className="muted nowrap">
                      {e.logName} · {e.provider} · {e.eventId}
                    </td>
                    <td>{e.message}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {events.data?.items.length === 0 && <div className="empty">No Critical/Error entries recorded.</div>}
          </div>
        </div>
      )}

      <GpuPanel gpus={detail.data?.gpus ?? []} />
      <HardwarePanel components={detail.data?.hardware ?? []} />

      {editServices && <ServicesDialog device={device} onClose={() => { setEditServices(false); void qc.invalidateQueries({ queryKey: ['device', device.id, 'server'] }); }} />}
      {resetKey && (
        <Confirm
          title="Reset the SSH host key?"
          message="The next SSH connection will accept and store the server's current host key. Only do this if the server was reinstalled or its key was changed on purpose — otherwise a changed key can indicate an impersonated server."
          confirmLabel="Reset host key"
          onClose={() => setResetKey(false)}
          onConfirm={() =>
            void (async () => {
              try {
                await api.post(`/api/devices/${device.id}/ssh-host-key/reset`);
                await qc.invalidateQueries({ queryKey: ['device', device.id] });
              } catch (e) {
                setError(e);
              }
              setResetKey(false);
            })()
          }
        />
      )}
    </div>
  );
}
