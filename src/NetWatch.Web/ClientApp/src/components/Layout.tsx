import { useQuery } from '@tanstack/react-query';
import { Bell, BellOff, Boxes, Cable, FileText, GitFork, HardDrive, LayoutDashboard, LogOut, MonitorPlay, Moon, ScrollText, Server, Settings, ShieldCheck, Siren, Sun, Wifi, WifiOff } from 'lucide-react';
import { BrandMark } from './BrandMark';
import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import type { Alert, Paged } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { useLive } from '../live/LiveContext';
import { useTheme } from '../theme/useTheme';
import { fmtTime, TIME_ZONE } from '../util/format';
import { playSeverity, soundReady } from '../live/sound';
import { SeverityBadge } from './Badges';

const dateFmt = new Intl.DateTimeFormat('en-GB', { timeZone: TIME_ZONE, weekday: 'short', day: '2-digit', month: 'short' });

function Clock() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const t = window.setInterval(() => setNow(new Date()), 1000);
    return () => window.clearInterval(t);
  }, []);
  return (
    <span className="clock" title="Pakistan Standard Time (Asia/Karachi)">
      {dateFmt.format(now)} {fmtTime(now)} PKT
    </span>
  );
}

function Toasts() {
  const { toasts, dismissToast } = useLive();
  const navigate = useNavigate();
  return (
    <div className="toasts" aria-live="polite">
      {toasts.map((t) => (
        <div key={t.key} className={`toast sev-${t.alert.severity}${t.kind === 'resolved' ? ' resolved' : ''}`}>
          <div className="t-head">
            {t.kind === 'resolved' ? <span className="badge s-up">Resolved</span> : <SeverityBadge severity={t.alert.severity} />}
            {t.kind === 'escalated' && <span className="badge sev-critical">Escalated — not acknowledged</span>}
            <span className="faint" style={{ marginLeft: 'auto', fontSize: 12 }}>
              {fmtTime(t.at)}
            </span>
          </div>
          <div className="t-title">{t.alert.title}</div>
          <div className="t-msg">{t.alert.message}</div>
          <div className="t-actions">
            <button
              className="small"
              onClick={() => {
                dismissToast(t.key);
                navigate(t.alert.deviceId ? `/devices/${t.alert.deviceId}` : '/alerts');
              }}
            >
              Open
            </button>
            <button className="small ghost" onClick={() => dismissToast(t.key)}>
              Dismiss
            </button>
          </div>
        </div>
      ))}
    </div>
  );
}

/**
 * Banner shown while any unacknowledged Critical/Major alert exists (refreshed by live events and every 30 s).
 * While escalated alerts stay unacknowledged, the alarm sound repeats at the configured interval.
 */
function AlertBanner() {
  const active = useQuery({
    queryKey: ['alerts', 'banner'],
    queryFn: () => api.get<Paged<Alert>>('/api/alerts?state=Active&minSeverity=Major&pageSize=50'),
    refetchInterval: 30000,
  });
  const info = useQuery({ queryKey: ['system', 'info'], queryFn: () => api.get<{ repeatSoundEveryMin: number }>('/api/system/info'), staleTime: 300000 });
  const navigate = useNavigate();
  const items = active.data?.items ?? [];
  const critical = items.filter((a) => a.severity === 'Critical').length;
  const major = items.length - critical;
  const escalated = items.filter((a) => a.escalatedUtc).length;
  const repeatMin = info.data?.repeatSoundEveryMin ?? 0;

  useEffect(() => {
    if (escalated === 0 || repeatMin <= 0) return;
    const t = window.setInterval(() => {
      if (soundReady()) playSeverity('Critical');
    }, repeatMin * 60000);
    return () => window.clearInterval(t);
  }, [escalated, repeatMin]);

  if (items.length === 0) return null;
  return (
    <div className={`banner ${critical > 0 ? 'crit' : 'major'}`} role="alert">
      <span className="pulse" />
      <span>
        {critical > 0 && `${critical} critical`}
        {critical > 0 && major > 0 && ', '}
        {major > 0 && `${major} major`} alert{items.length === 1 ? '' : 's'} not acknowledged
        {escalated > 0 && ` — ${escalated} escalated`}
      </span>
      <span className="muted" style={{ fontWeight: 400, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {items[0]?.title}
      </span>
      <button className="small" style={{ marginLeft: 'auto' }} onClick={() => navigate('/alerts')}>
        View alerts
      </button>
    </div>
  );
}

export function Layout({ children }: { children: ReactNode }) {
  const { user, logout, can } = useAuth();
  const { connected, summary, soundOn, toggleSound } = useLive();
  const [theme, toggleTheme] = useTheme();
  const navigate = useNavigate();
  const openAlerts = summary ? summary.alerts.Critical + summary.alerts.Major + summary.alerts.Warning + summary.alerts.Info : 0;

  return (
    <div className="shell">
      <aside className="sidebar">
        <BrandMark size={26} />
        <nav className="nav">
          <NavLink to="/" end>
            <LayoutDashboard size={18} />
            <span>Overview</span>
          </NavLink>
          <NavLink to="/devices">
            <Server size={18} />
            <span>Devices</span>
          </NavLink>
          <NavLink to="/switches">
            <Cable size={18} />
            <span>Switches</span>
          </NavLink>
          <NavLink to="/servers">
            <HardDrive size={18} />
            <span>Servers</span>
          </NavLink>
          <NavLink to="/vmware">
            <Boxes size={18} />
            <span>VMware</span>
          </NavLink>
          <NavLink to="/dc">
            <ShieldCheck size={18} />
            <span>Domain controllers</span>
          </NavLink>
          <NavLink to="/topology">
            <GitFork size={18} />
            <span>Topology</span>
          </NavLink>
          <NavLink to="/events">
            <ScrollText size={18} />
            <span>Events &amp; logs</span>
          </NavLink>
          <NavLink to="/alerts">
            <Siren size={18} />
            <span>Alerts</span>
            {openAlerts > 0 && <span className="count">{openAlerts}</span>}
          </NavLink>
          <NavLink to="/reports">
            <FileText size={18} />
            <span>Reports</span>
          </NavLink>
          <NavLink to="/noc">
            <MonitorPlay size={18} />
            <span>NOC mode</span>
          </NavLink>
          {can('Admin') && (
            <NavLink to="/settings">
              <Settings size={18} />
              <span>Settings</span>
            </NavLink>
          )}
        </nav>
        <div className="sidebar-foot">
          {user?.displayName} · {user?.role}
        </div>
      </aside>

      <header className="topbar">
        <span className="muted" title={connected ? 'Live updates connected' : 'Live updates disconnected — reconnecting'} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          {connected ? <Wifi size={16} color="var(--ok)" /> : <WifiOff size={16} color="var(--crit)" />}
          {connected ? 'Live' : 'Reconnecting…'}
        </span>
        {summary && (
          <span className="muted" style={{ fontSize: 13 }}>
            {summary.devices.up} up · <span style={{ color: 'var(--warn)' }}>{summary.devices.warning} warning</span> ·{' '}
            <span style={{ color: 'var(--crit)' }}>{summary.devices.down} down</span>
          </span>
        )}
        <span className="spacer" />
        <Clock />
        <button className="ghost icon" onClick={() => void toggleSound()} title={soundOn ? 'Alert sound on — click to mute' : 'Enable alert sound'}>
          {soundOn ? <Bell size={18} /> : <BellOff size={18} color="var(--warn)" />}
        </button>
        <button className="ghost icon" onClick={toggleTheme} title="Toggle dark/light theme">
          {theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}
        </button>
        <button className="ghost" onClick={() => navigate('/account')} title="Account">
          {user?.username}
        </button>
        <button className="ghost icon" onClick={() => void logout()} title="Sign out">
          <LogOut size={18} />
        </button>
      </header>

      <AlertBanner />
      <main className="main">{children}</main>
      <Toasts />
    </div>
  );
}
