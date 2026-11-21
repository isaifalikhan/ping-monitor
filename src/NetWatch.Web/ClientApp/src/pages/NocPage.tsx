import { useQuery } from '@tanstack/react-query';
import { Maximize, Minimize, Volume2, VolumeX, X } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, qs } from '../api/client';
import type { Alert, DcOverview, DcSecurityEvent, Overview, PagedEvents, Severity } from '../api/types';
import type { Paged } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { groupState, sortOpenAlerts } from '../util/logic';
import { SeverityBadge } from '../components/Badges';
import { useLive } from '../live/LiveContext';
import { fmtAgo, fmtBps, fmtNum, TIME_ZONE } from '../util/format';

const ROTATE_MS = 20_000;

function Clock() {
  const [now, setNow] = useState(new Date());
  useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(t);
  }, []);
  return (
    <span className="noc-clock">
      {now.toLocaleString('en-GB', { timeZone: TIME_ZONE, weekday: 'short', day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit' })} PKT
    </span>
  );
}

function Tile({ label, value, color }: { label: string; value: number | string; color?: string }) {
  return (
    <div className="noc-tile" style={{ borderTopColor: color }}>
      <div className="noc-tile-value" style={{ color }}>
        {value}
      </div>
      <div className="noc-tile-label">{label}</div>
    </div>
  );
}

/**
 * NOC wall display: full-screen, large type, no navigation. Device and alert counts update live; the alert
 * list refreshes every 15 s and the right panel rotates through problem devices, sites, busy links and DCs.
 * Regular polling keeps the session active (sessions still end after the maximum session length).
 */
export function NocPage() {
  const { summary, connected, soundOn, toggleSound } = useLive();
  const { user } = useAuth();
  const kiosk = user?.authSource === 'Kiosk';
  const [panel, setPanel] = useState(0);
  const [paused, setPaused] = useState(false);
  const [fullscreen, setFullscreen] = useState(!!document.fullscreenElement);

  const overview = useQuery({ queryKey: ['overview'], queryFn: () => api.get<Overview>('/api/overview'), refetchInterval: 20000 });
  const alerts = useQuery({
    queryKey: ['noc', 'alerts'],
    queryFn: () => api.get<Paged<Alert>>(`/api/alerts${qs({ state: 'Active,Acknowledged', pageSize: 300 })}`),
    refetchInterval: 15000,
  });
  const dc = useQuery({ queryKey: ['dc', 'summary'], queryFn: () => api.get<DcOverview>('/api/dc/summary'), refetchInterval: 60000 });
  const ticker = useQuery({
    queryKey: ['noc', 'dc-ticker'],
    queryFn: () => api.get<PagedEvents<DcSecurityEvent>>(`/api/dc/security-events${qs({ minSeverity: 'Warning', pageSize: 25, from: new Date(Date.now() - 24 * 3600_000).toISOString() })}`),
    refetchInterval: 30000,
  });

  useEffect(() => {
    const onChange = () => setFullscreen(!!document.fullscreenElement);
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, []);

  const o = overview.data;
  const panels = [
    { key: 'problems', title: 'Devices with problems' },
    { key: 'sites', title: 'Sites and groups' },
    { key: 'links', title: 'Busiest links' },
    ...(dc.data && dc.data.controllers.length > 0 ? [{ key: 'dc', title: 'Domain controllers' }] : []),
  ];
  useEffect(() => {
    if (paused) return;
    const t = setInterval(() => setPanel((p) => (p + 1) % panels.length), ROTATE_MS);
    return () => clearInterval(t);
  }, [paused, panels.length]);
  const current = panels[panel % panels.length];

  const devices = summary?.devices ?? o?.totals;
  const counts = summary?.alerts ?? o?.alerts;
  const open = sortOpenAlerts(alerts.data?.items ?? []);

  const toggleFullscreen = () => {
    if (document.fullscreenElement) void document.exitFullscreen();
    else void document.documentElement.requestFullscreen().catch(() => undefined);
  };

  const sevColor = (s: Severity) => (s === 'Critical' ? 'var(--crit)' : s === 'Major' ? 'var(--major)' : s === 'Warning' ? 'var(--warn)' : 'var(--text-muted)');

  return (
    <div className="noc">
      <header className="noc-head">
        <strong>NetWatch NOC</strong>
        <span className={connected ? 'noc-live' : 'noc-live off'}>{connected ? '● live' : '● reconnecting'}</span>
        <span style={{ flex: 1 }} />
        <Clock />
        <button className="noc-btn" title={soundOn ? 'Sound on for new critical/major alerts — click to mute' : 'Enable sound for new alerts (browsers need one click)'} onClick={() => void toggleSound()}>
          {soundOn ? <Volume2 size={18} /> : <VolumeX size={18} />}
        </button>
        <button className="noc-btn" title={fullscreen ? 'Leave full screen' : 'Full screen'} onClick={toggleFullscreen}>
          {fullscreen ? <Minimize size={18} /> : <Maximize size={18} />}
        </button>
        {!kiosk && (
          <Link className="noc-btn" to="/" title="Leave NOC mode">
            <X size={18} />
          </Link>
        )}
      </header>

      <section className="noc-tiles">
        <Tile label="Devices up" value={devices?.up ?? '–'} color="var(--ok)" />
        <Tile label="Warning" value={devices?.warning ?? '–'} color="var(--warn)" />
        <Tile label="Down" value={devices?.down ?? '–'} color="var(--crit)" />
        <Tile label="Maintenance" value={devices?.maintenance ?? '–'} color="var(--accent)" />
        <Tile label="Critical alerts" value={counts?.Critical ?? '–'} color="var(--crit)" />
        <Tile label="Major alerts" value={counts?.Major ?? '–'} color="var(--major)" />
        <Tile label="Warnings" value={counts?.Warning ?? '–'} color="var(--warn)" />
      </section>

      {(o?.groups.length ?? 0) > 0 && (
        <section className="noc-groups" aria-label="Sites and groups">
          {o!.groups.map((g) => {
            const state = groupState(g.counts);
            return (
              <div key={g.groupId} className={`noc-group s-${state}`} title={g.path}>
                <span className="noc-group-name">{g.name}</span>
                <span className="noc-group-count">
                  {g.counts.down > 0 ? `${g.counts.down} down` : g.counts.warning > 0 ? `${g.counts.warning} warning` : `${g.counts.up}/${g.counts.total} up`}
                </span>
              </div>
            );
          })}
        </section>
      )}

      <section className="noc-body">
        <div className="noc-panel">
          <h2>
            Open alerts <span className="muted">({open.length})</span>
          </h2>
          <div className="noc-list">
            {open.slice(0, 40).map((a) => (
              <div key={a.id} className="noc-alert" style={{ borderLeftColor: sevColor(a.severity), opacity: a.state === 'Acknowledged' ? 0.6 : 1 }}>
                <SeverityBadge severity={a.severity} />
                <span className="noc-alert-title">{a.title}</span>
                <span className="muted nowrap">
                  {a.state === 'Acknowledged' ? `ack ${a.ackByUsername ?? ''} · ` : a.escalatedUtc ? 'escalated · ' : ''}
                  {fmtAgo(a.firstSeenUtc)}
                </span>
              </div>
            ))}
            {alerts.data && open.length === 0 && <div className="noc-allclear">All clear: no open alerts.</div>}
          </div>
        </div>

        <div className="noc-panel" onMouseEnter={() => setPaused(true)} onMouseLeave={() => setPaused(false)}>
          <h2>
            {current.title}
            <span className="noc-dots">
              {panels.map((p, i) => (
                <button key={p.key} className={i === panel % panels.length ? 'on' : ''} onClick={() => setPanel(i)} aria-label={p.title} />
              ))}
            </span>
          </h2>
          <div className="noc-list">
            {current.key === 'problems' &&
              (o?.problemDevices.length ? (
                o.problemDevices.slice(0, 25).map((d) => (
                  <div key={d.id} className="noc-row">
                    <span className={`dot s-${d.status.toLowerCase()}`} />
                    <span className="noc-row-name">{d.name}</span>
                    <span className="muted">{d.unreachableVia ? `via ${d.unreachableVia}` : d.status}</span>
                  </div>
                ))
              ) : (
                <div className="noc-allclear">Every device is up.</div>
              ))}
            {current.key === 'sites' &&
              (o?.groups ?? []).map((g) => (
                <div key={g.groupId} className="noc-row">
                  <span className={`dot s-${g.counts.down > 0 ? 'down' : g.counts.warning > 0 ? 'warning' : 'up'}`} />
                  <span className="noc-row-name">{g.path}</span>
                  <span className="muted">
                    {g.counts.up}/{g.counts.total} up
                    {g.counts.down > 0 && <strong style={{ color: 'var(--crit)' }}> · {g.counts.down} down</strong>}
                  </span>
                </div>
              ))}
            {current.key === 'links' &&
              (o?.topInterfaces ?? []).map((i) => (
                <div key={i.interfaceId} className="noc-row">
                  <span className="noc-row-name">
                    {i.deviceName} <span className="muted">{i.interfaceName}</span>
                  </span>
                  <span style={{ color: Math.max(i.inUtilPct ?? 0, i.outUtilPct ?? 0) > 80 ? 'var(--crit)' : undefined }}>
                    {fmtNum(Math.max(i.inUtilPct ?? 0, i.outUtilPct ?? 0), 0)} % · {fmtBps(Math.max(i.inBps ?? 0, i.outBps ?? 0))}
                  </span>
                </div>
              ))}
            {current.key === 'dc' &&
              (dc.data?.controllers ?? []).map((c) => (
                <div key={c.deviceId} className="noc-row">
                  <span className={`dot s-${c.status.toLowerCase()}`} />
                  <span className="noc-row-name">{c.name}</span>
                  <span className="muted">
                    {c.replicationFailing > 0 ? <strong style={{ color: 'var(--crit)' }}>replication failing · </strong> : 'replication OK · '}
                    {c.failedLogons24h} failed logons · {c.lockouts24h} lockouts (24 h)
                  </span>
                </div>
              ))}
          </div>
        </div>
      </section>

      {(ticker.data?.items.length ?? 0) > 0 && (
        <footer className="noc-ticker" aria-label="Domain controller security events">
          <span className="noc-ticker-label">DC security</span>
          <div className="noc-ticker-track">
            <div className="noc-ticker-items">
              {[...ticker.data!.items, ...ticker.data!.items].map((e, i) => (
                <span key={i} className="noc-ticker-item" style={{ color: sevColor(e.severity) }}>
                  {fmtAgo(e.timestampUtc)} · {e.deviceName} · {e.message}
                </span>
              ))}
            </div>
          </div>
        </footer>
      )}
    </div>
  );
}
