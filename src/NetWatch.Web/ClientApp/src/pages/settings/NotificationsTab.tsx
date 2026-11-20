import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { ChannelStatus, NotificationSettings, Severity, SmtpSecurity } from '../../api/types';
import { SEVERITIES } from '../../api/types';
import { ErrorBox } from '../../components/Modal';
import { fmtAgo } from '../../util/format';

type Form = Omit<NotificationSettings, 'hasSmtpPassword' | 'scriptsFolder'> & { smtpPassword: string | null };

function SeveritySelect({ value, onChange }: { value: Severity; onChange: (s: Severity) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value as Severity)}>
      {SEVERITIES.map((s) => (
        <option key={s} value={s}>
          {s} and above
        </option>
      ))}
    </select>
  );
}

function TestButton({ channel }: { channel: string }) {
  const [state, setState] = useState<{ busy: boolean; result?: { success: boolean; error?: string } }>({ busy: false });
  return (
    <span className="toolbar">
      <button
        className="small"
        disabled={state.busy}
        onClick={() =>
          void (async () => {
            setState({ busy: true });
            try {
              setState({ busy: false, result: await api.post<{ success: boolean; error?: string }>(`/api/notifications/test/${channel}`) });
            } catch (e) {
              setState({ busy: false, result: { success: false, error: e instanceof Error ? e.message : String(e) } });
            }
          })()
        }
      >
        {state.busy ? 'Sending…' : 'Send test'}
      </button>
      {state.result && (state.result.success ? <span style={{ color: 'var(--ok)' }}>Sent</span> : <span style={{ color: 'var(--crit)' }}>{state.result.error}</span>)}
    </span>
  );
}

function Status({ status }: { status?: ChannelStatus }) {
  if (!status || (status.sent === 0 && status.failed === 0)) return <span className="faint">nothing sent since start</span>;
  return (
    <span className="muted">
      {status.sent} sent{status.lastSuccessUtc ? ` (last ${fmtAgo(status.lastSuccessUtc)})` : ''}
      {status.failed > 0 && (
        <span style={{ color: 'var(--crit)' }}>
          {' '}
          · {status.failed} failed — {status.lastError}
        </span>
      )}
    </span>
  );
}

/** Offline notification channels: Windows Event Log, syslog forwarding, local script, SMTP relay. */
export function NotificationsTab() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['settings', 'notifications'], queryFn: () => api.get<NotificationSettings>('/api/settings/notifications') });
  const status = useQuery({ queryKey: ['notifications', 'status'], queryFn: () => api.get<ChannelStatus[]>('/api/notifications/status'), refetchInterval: 15000 });
  const [f, setF] = useState<Form | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (q.data) {
      const { hasSmtpPassword: _h, scriptsFolder: _s, ...rest } = q.data;
      setF({ ...rest, smtpPassword: null });
    }
  }, [q.data]);

  if (q.error) return <ErrorBox error={q.error} />;
  if (!f || !q.data) return <div className="empty">Loading…</div>;

  const set = <K extends keyof Form>(k: K, v: Form[K]) => {
    setSaved(false);
    setF((s) => (s ? { ...s, [k]: v } : s));
  };
  const st = (c: string) => status.data?.find((s) => s.channel === c);

  const save = async () => {
    setError(null);
    try {
      await api.put('/api/settings/notifications', f);
      setSaved(true);
      await qc.invalidateQueries({ queryKey: ['settings', 'notifications'] });
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="grid" style={{ gap: 16 }}>
      <div className="toolbar">
        <span className="muted">The dashboard (pop-ups, banner, sound) always notifies. These channels are optional and work without internet access. Save before sending a test.</span>
        <span style={{ flex: 1 }} />
        {saved && <span style={{ color: 'var(--ok)' }}>Saved</span>}
        <button className="primary" onClick={() => void save()}>
          Save
        </button>
      </div>
      <ErrorBox error={error} />

      <div className="card card-body">
        <h2 style={{ marginBottom: 10 }}>General</h2>
        <div className="toolbar">
          <label className="check">
            <input type="checkbox" checked={f.notifyOnResolve} onChange={(e) => set('notifyOnResolve', e.target.checked)} /> Also notify when alerts resolve
          </label>
          <label className="check">
            <input type="checkbox" checked={f.notifyOnEscalation} onChange={(e) => set('notifyOnEscalation', e.target.checked)} /> Notify on escalation (not acknowledged in time)
          </label>
        </div>
      </div>

      <div className="card card-body">
        <div className="toolbar" style={{ marginBottom: 10 }}>
          <h2>Windows Event Log</h2>
          <span style={{ flex: 1 }} />
          <Status status={st('eventlog')} />
          <TestButton channel="eventlog" />
        </div>
        <div className="form-grid">
          <label className="check">
            <input type="checkbox" checked={f.eventLogEnabled} onChange={(e) => set('eventLogEnabled', e.target.checked)} /> Write alerts to the Application log (source “NetWatch”)
          </label>
          <label className="field">
            <span>Minimum severity</span>
            <SeveritySelect value={f.eventLogMinSeverity} onChange={(v) => set('eventLogMinSeverity', v)} />
          </label>
        </div>
      </div>

      <div className="card card-body">
        <div className="toolbar" style={{ marginBottom: 10 }}>
          <h2>Syslog forwarding (SIEM)</h2>
          <span style={{ flex: 1 }} />
          <Status status={st('syslog')} />
          <TestButton channel="syslog" />
        </div>
        <div className="form-grid">
          <label className="check">
            <input type="checkbox" checked={f.syslogForwardEnabled} onChange={(e) => set('syslogForwardEnabled', e.target.checked)} /> Forward alerts as RFC 5424 syslog
          </label>
          <label className="field">
            <span>Target host</span>
            <input value={f.syslogForwardHost ?? ''} onChange={(e) => set('syslogForwardHost', e.target.value)} placeholder="siem.corp.local" />
          </label>
          <label className="field">
            <span>Port / protocol</span>
            <div style={{ display: 'flex', gap: 6 }}>
              <input type="number" min={1} max={65535} value={f.syslogForwardPort} onChange={(e) => set('syslogForwardPort', Number(e.target.value))} style={{ width: 100 }} />
              <select value={f.syslogForwardProtocol} onChange={(e) => set('syslogForwardProtocol', e.target.value as 'Udp' | 'Tcp')}>
                <option value="Udp">UDP</option>
                <option value="Tcp">TCP</option>
              </select>
            </div>
          </label>
          <label className="field">
            <span>Minimum severity</span>
            <SeveritySelect value={f.syslogForwardMinSeverity} onChange={(v) => set('syslogForwardMinSeverity', v)} />
          </label>
        </div>
      </div>

      <div className="card card-body">
        <div className="toolbar" style={{ marginBottom: 10 }}>
          <h2>Run a script</h2>
          <span style={{ flex: 1 }} />
          <Status status={st('script')} />
          <TestButton channel="script" />
        </div>
        <div className="form-grid">
          <label className="check">
            <input type="checkbox" checked={f.scriptEnabled} onChange={(e) => set('scriptEnabled', e.target.checked)} /> Run a local script on alerts
          </label>
          <label className="field">
            <span>Script file name</span>
            <input value={f.scriptFile ?? ''} onChange={(e) => set('scriptFile', e.target.value)} placeholder="on-alert.ps1" />
            <span className="hint">
              Must be in <span className="mono">{q.data.scriptsFolder}</span> (.ps1, .cmd, .bat or .exe). Alert data arrives in NETWATCH_* environment variables and as JSON on standard
              input.
            </span>
          </label>
          <label className="field">
            <span>Minimum severity</span>
            <SeveritySelect value={f.scriptMinSeverity} onChange={(v) => set('scriptMinSeverity', v)} />
          </label>
          <label className="field">
            <span>Timeout (seconds)</span>
            <input type="number" min={5} max={300} value={f.scriptTimeoutSec} onChange={(e) => set('scriptTimeoutSec', Number(e.target.value))} />
          </label>
        </div>
      </div>

      <div className="card card-body">
        <div className="toolbar" style={{ marginBottom: 10 }}>
          <h2>E-mail (internal SMTP relay)</h2>
          <span style={{ flex: 1 }} />
          <Status status={st('smtp')} />
          <TestButton channel="smtp" />
        </div>
        <div className="form-grid">
          <label className="check">
            <input type="checkbox" checked={f.smtpEnabled} onChange={(e) => set('smtpEnabled', e.target.checked)} /> Send e-mail
          </label>
          <label className="field">
            <span>SMTP server</span>
            <input value={f.smtpHost ?? ''} onChange={(e) => set('smtpHost', e.target.value)} placeholder="10.0.0.25" />
          </label>
          <label className="field">
            <span>Port / security</span>
            <div style={{ display: 'flex', gap: 6 }}>
              <input type="number" min={1} max={65535} value={f.smtpPort} onChange={(e) => set('smtpPort', Number(e.target.value))} style={{ width: 100 }} />
              <select value={f.smtpSecurity} onChange={(e) => set('smtpSecurity', e.target.value as SmtpSecurity)}>
                <option value="StartTls">STARTTLS</option>
                <option value="SslOnConnect">TLS (port 465)</option>
                <option value="None">None (trusted relay only)</option>
              </select>
            </div>
          </label>
          <label className="field">
            <span>User name (optional)</span>
            <input autoComplete="off" value={f.smtpUsername ?? ''} onChange={(e) => set('smtpUsername', e.target.value)} />
          </label>
          <label className="field">
            <span>Password {q.data.hasSmtpPassword && <span className="muted">(stored — leave empty to keep)</span>}</span>
            <input type="password" autoComplete="new-password" value={f.smtpPassword ?? ''} onChange={(e) => set('smtpPassword', e.target.value === '' ? null : e.target.value)} />
            {q.data.hasSmtpPassword && (
              <span className="check">
                <input type="checkbox" aria-label="Remove the stored password" checked={f.smtpPassword === ''} onChange={(e) => set('smtpPassword', e.target.checked ? '' : null)} /> Remove the stored
                password
              </span>
            )}
          </label>
          <label className="field">
            <span>From</span>
            <input value={f.smtpFrom ?? ''} onChange={(e) => set('smtpFrom', e.target.value)} placeholder="netwatch@corp.local" />
          </label>
          <label className="field">
            <span>To (comma separated)</span>
            <input value={f.smtpTo ?? ''} onChange={(e) => set('smtpTo', e.target.value)} placeholder="noc@corp.local, oncall@corp.local" />
          </label>
          <label className="field">
            <span>Minimum severity</span>
            <SeveritySelect value={f.smtpMinSeverity} onChange={(v) => set('smtpMinSeverity', v)} />
          </label>
        </div>
        <div className="hint" style={{ marginTop: 6 }}>
          Five or more notifications arriving together are sent as one digest e-mail. The SMTP password is encrypted with DPAPI and never shown again.
        </div>
      </div>
    </div>
  );
}
