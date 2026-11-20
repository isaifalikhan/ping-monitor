import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import type { Overview, Severity, StatusCounts } from '../api/types';
import { SEVERITIES } from '../api/types';
import { SeverityBadge, StatusBadge, UtilBar } from '../components/Badges';
import { ErrorBox } from '../components/Modal';
import { fmtAgo, fmtBps, fmtDateTime, typeLabel } from '../util/format';

function Breakdown({ c }: { c: StatusCounts }) {
  return (
    <div className="breakdown">
      <span style={{ color: 'var(--ok)' }}>● {c.up} up</span>
      {c.warning > 0 && <span style={{ color: 'var(--warn)' }}>● {c.warning} warning</span>}
      {c.down > 0 && <span style={{ color: 'var(--crit)' }}>● {c.down} down</span>}
      {c.unknown > 0 && <span className="faint">● {c.unknown} unknown</span>}
      {c.maintenance > 0 && <span style={{ color: 'var(--maint)' }}>● {c.maintenance} maint.</span>}
    </div>
  );
}

function HealthTile({ name, path, c }: { name: string; path: string; c: StatusCounts }) {
  const tone = c.down > 0 ? 'var(--crit)' : c.warning > 0 ? 'var(--warn)' : 'var(--ok)';
  return (
    <div className="card stat" style={{ borderTop: `3px solid ${tone}` }} title={path}>
      <div className="label">{name}</div>
      <div className="value" style={{ fontSize: 22 }}>
        {c.total} <span className="muted" style={{ fontSize: 13, fontWeight: 400 }}>devices</span>
      </div>
      <Breakdown c={c} />
    </div>
  );
}

export function OverviewPage() {
  const navigate = useNavigate();
  const q = useQuery({ queryKey: ['overview'], queryFn: () => api.get<Overview>('/api/overview'), refetchInterval: 30000 });
  const o = q.data;

  return (
    <>
      <div className="page-head">
        <h1>Overview</h1>
      </div>
      <ErrorBox error={q.error} />
      {o && (
        <div className="grid" style={{ gap: 18 }}>
          <div className="grid cols-4">
            <div className="card stat">
              <div className="label">All devices</div>
              <div className="value">{o.totals.total}</div>
              <Breakdown c={o.totals} />
            </div>
            {o.byType.map((t) => (
              <div key={t.type} className="card stat" style={{ cursor: 'pointer' }} onClick={() => navigate(`/devices?type=${t.type}`)}>
                <div className="label">{typeLabel(t.type)}s</div>
                <div className="value">{t.counts.total}</div>
                <Breakdown c={t.counts} />
              </div>
            ))}
            <div className="card stat" style={{ cursor: 'pointer' }} onClick={() => navigate('/alerts')}>
              <div className="label">Open alerts</div>
              <div className="value">{SEVERITIES.reduce((n, s) => n + (o.alerts[s] ?? 0), 0)}</div>
              <div className="breakdown">
                {(['Critical', 'Major', 'Warning', 'Info'] as Severity[]).map((s) => (
                  <span key={s} className={`badge sev-${s.toLowerCase()}`}>
                    {o.alerts[s] ?? 0} {s}
                  </span>
                ))}
              </div>
            </div>
          </div>

          {o.groups.length > 0 && (
            <div>
              <h2 style={{ marginBottom: 10 }}>Sites &amp; groups</h2>
              <div className="grid cols-4">
                {o.groups.map((g) => (
                  <HealthTile key={g.groupId} name={g.name} path={g.path} c={g.counts} />
                ))}
              </div>
            </div>
          )}

          <div className="grid cols-2">
            <div className="card">
              <div className="card-head">
                <h2>Problem devices</h2>
              </div>
              <div className="card-body flush table-wrap" style={{ maxHeight: 380 }}>
                {o.problemDevices.length === 0 ? (
                  <div className="empty">All devices are healthy.</div>
                ) : (
                  <table className="data">
                    <tbody>
                      {o.problemDevices.map((d) => (
                        <tr key={d.id} className="clickable" onClick={() => navigate(`/devices/${d.id}`)}>
                          <td>
                            <StatusBadge status={d.status} />
                          </td>
                          <td>{d.name}</td>
                          <td className="mono muted">{d.ipAddress}</td>
                          <td className="muted nowrap">{d.status === 'Down' ? `down ${fmtAgo(d.statusSinceUtc)}` : d.worstAlert ?? ''}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            </div>

            <div className="card">
              <div className="card-head">
                <h2>Busiest interfaces</h2>
              </div>
              <div className="card-body flush table-wrap" style={{ maxHeight: 380 }}>
                <table className="data">
                  <thead>
                    <tr>
                      <th>Device / port</th>
                      <th className="num">In</th>
                      <th className="num">Out</th>
                      <th style={{ width: 150 }}>Utilization</th>
                    </tr>
                  </thead>
                  <tbody>
                    {o.topInterfaces.map((i) => (
                      <tr key={i.interfaceId} className="clickable" onClick={() => navigate(`/devices/${i.deviceId}?tab=interfaces&if=${i.interfaceId}`)}>
                        <td>
                          {i.deviceName} <span className="muted">{i.interfaceName}</span>
                        </td>
                        <td className="num">{fmtBps(i.inBps)}</td>
                        <td className="num">{fmtBps(i.outBps)}</td>
                        <td>
                          <UtilBar value={Math.max(i.inUtilPct ?? 0, i.outUtilPct ?? 0)} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {o.topInterfaces.length === 0 && <div className="empty">No interface data yet.</div>}
              </div>
            </div>

            <div className="card">
              <div className="card-head">
                <h2>Top CPU</h2>
              </div>
              <div className="card-body flush">
                <table className="data">
                  <tbody>
                    {o.topCpu.map((d) => (
                      <tr key={d.deviceId} className="clickable" onClick={() => navigate(`/devices/${d.deviceId}`)}>
                        <td>{d.name}</td>
                        <td className="muted">{typeLabel(d.type)}</td>
                        <td style={{ width: 170 }}>
                          <UtilBar value={d.value} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {o.topCpu.length === 0 && <div className="empty">No CPU data yet.</div>}
              </div>
            </div>

            <div className="card">
              <div className="card-head">
                <h2>Top memory</h2>
              </div>
              <div className="card-body flush">
                <table className="data">
                  <tbody>
                    {o.topMemory.map((d) => (
                      <tr key={d.deviceId} className="clickable" onClick={() => navigate(`/devices/${d.deviceId}`)}>
                        <td>{d.name}</td>
                        <td className="muted">{typeLabel(d.type)}</td>
                        <td style={{ width: 170 }}>
                          <UtilBar value={d.value} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {o.topMemory.length === 0 && <div className="empty">No memory data yet.</div>}
              </div>
            </div>
          </div>

          <div className="card">
            <div className="card-head">
              <h2>Recent events</h2>
            </div>
            <div className="card-body flush table-wrap">
              <table className="data">
                <tbody>
                  {o.recentEvents.map((e) => (
                    <tr key={e.id}>
                      <td className="nowrap muted" style={{ width: 170 }}>
                        {fmtDateTime(e.timestampUtc)}
                      </td>
                      <td style={{ width: 90 }}>
                        <SeverityBadge severity={e.severity} />
                      </td>
                      <td className="nowrap">{e.deviceId ? <Link to={`/devices/${e.deviceId}`}>{e.deviceName}</Link> : '—'}</td>
                      <td>{e.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {o.recentEvents.length === 0 && <div className="empty">No events yet.</div>}
            </div>
          </div>
          <div className="faint" style={{ fontSize: 12 }}>
            Utilization bars show the higher of in/out. Values refresh live; a dash (—) means the device does not report that value.
          </div>
        </div>
      )}
    </>
  );
}
