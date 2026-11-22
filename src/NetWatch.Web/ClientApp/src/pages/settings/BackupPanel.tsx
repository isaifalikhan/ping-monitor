import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCircle2, Download, Trash2, XCircle } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../../api/client';
import type { BackupOverview, BackupSettings, SelfCheck, SelfMonitorSettings } from '../../api/types';
import { ErrorBox } from '../../components/Modal';
import { fmtAgo, fmtBytes, fmtDateTime } from '../../util/format';

/** Results of NetWatch's self-checks, with their thresholds. */
export function SelfCheckPanel({ checks, checkedUtc, selfDeviceId }: { checks: SelfCheck[]; checkedUtc?: string | null; selfDeviceId?: number | null }) {
  const qc = useQueryClient();
  const settings = useQuery({ queryKey: ['settings'], queryFn: () => api.get<{ selfMonitor: SelfMonitorSettings }>('/api/settings') });
  const [value, setValue] = useState<SelfMonitorSettings | null>(null);
  useEffect(() => setValue(settings.data?.selfMonitor ?? null), [settings.data]);
  const save = useMutation({
    mutationFn: (v: SelfMonitorSettings) => api.put<SelfMonitorSettings>('/api/settings/selfmonitor', v),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['settings'] }),
  });
  const failing = checks.filter((c) => !c.ok).length;
  const field = (key: keyof SelfMonitorSettings, label: string, min: number, max: number) =>
    value && (
      <label className="field">
        <span>{label}</span>
        <input type="number" min={min} max={max} value={value[key]} onChange={(e) => setValue({ ...value, [key]: Number(e.target.value) })} />
      </label>
    );

  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <div className="card-head">
        <h2>Self-monitoring</h2>
        <span className="spacer" />
        <span className={failing > 0 ? 'badge s-down' : 'badge s-up'}>{failing > 0 ? `${failing} check(s) failing` : 'all checks OK'}</span>
      </div>
      <div className="card-body">
        <table className="data" style={{ marginBottom: 12 }}>
          <tbody>
            {checks.map((c) => (
              <tr key={c.name}>
                <td style={{ width: 28 }}>{c.ok ? <CheckCircle2 size={16} color="var(--ok)" /> : <XCircle size={16} color="var(--crit)" />}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <strong>{c.name}</strong>
                </td>
                <td style={{ color: c.ok ? undefined : 'var(--crit)' }}>{c.detail}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {checks.length === 0 && <div className="muted">The first check runs about a minute after start.</div>}
        <p className="muted">
          Checked every minute{checkedUtc ? ` (last ${fmtAgo(checkedUtc)})` : ''}. A failing check raises the alert “HIT SIEM self-check failing” on the device{' '}
          {selfDeviceId ? <Link to={`/devices/${selfDeviceId}`}>for this server</Link> : 'for this server'}, so it reaches the usual notification channels.
        </p>
        <ErrorBox error={save.error} />
        <div className="form-grid">
          {field('minFreeDiskPct', 'Minimum free disk (%)', 1, 50)}
          {field('minFreeDiskGb', 'Minimum free disk (GB)', 1, 1000)}
          {field('certificateWarnDays', 'Warn before certificate expiry (days)', 1, 365)}
          {field('maxBackupAgeHours', 'Maximum backup age (hours)', 2, 744)}
        </div>
        <button className="primary small" disabled={!value || save.isPending} onClick={() => value && save.mutate(value)}>
          Save thresholds
        </button>
        {save.isSuccess && <span style={{ color: 'var(--ok)', marginLeft: 8 }}>Saved</span>}
      </div>
    </div>
  );
}

/** Backups: schedule settings, run now, list, download and delete. Restore is offline (command line). */
export function BackupPanel() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['backups'], queryFn: () => api.get<BackupOverview>('/api/backups'), refetchInterval: 10000 });
  const [settings, setSettings] = useState<BackupSettings | null>(null);
  useEffect(() => {
    if (q.data && settings === null) setSettings(q.data.settings);
  }, [q.data, settings]);
  const refresh = () => void qc.invalidateQueries({ queryKey: ['backups'] });
  const create = useMutation({ mutationFn: () => api.post('/api/backups'), onSettled: refresh });
  const portable = useMutation({ mutationFn: (password: string) => api.post('/api/backups/portable', { password }), onSettled: refresh });
  const restore = useMutation({
    mutationFn: (v: { name: string; password: string }) => api.post<{ message: string }>(`/api/backups/${v.name}/restore`, { confirm: 'RESTORE', password: v.password || null }),
  });
  const askPortable = () => {
    const password = window.prompt('Password for the credential export (at least 12 characters). Keep it safe: it is needed to restore this backup on another server.');
    if (password) portable.mutate(password);
  };
  const askRestore = (name: string) => {
    if (window.prompt(`Restore ${name}? ALL current data (devices, alerts, history, settings) is replaced. HIT SIEM stops and restores at the next start. Type RESTORE to continue.`) !== 'RESTORE') return;
    const password = name.includes('-portable') ? (window.prompt('Portable credential password (leave empty to keep the credentials encrypted for the original server):') ?? '') : '';
    restore.mutate({ name, password });
  };
  const remove = useMutation({ mutationFn: (name: string) => api.del(`/api/backups/${name}`), onSuccess: refresh });
  const save = useMutation({
    mutationFn: (v: BackupSettings) => api.put<BackupSettings>('/api/settings/backup', { ...v, folder: v.folder?.trim() || null }),
    onSuccess: (v) => {
      setSettings(v);
      refresh();
    },
  });
  const d = q.data;

  return (
    <div className="card">
      <div className="card-head">
        <h2>Backups</h2>
        <span className="spacer" />
        <button className="small" disabled={portable.isPending || d?.running} onClick={askPortable} title="Also export the credentials, re-encrypted with a password, so the backup can be restored on a new server">
          Back up for another server…
        </button>
        <button className="primary small" disabled={create.isPending || d?.running} onClick={() => create.mutate()}>
          {create.isPending || d?.running ? 'Backing up…' : 'Back up now'}
        </button>
      </div>
      <div className="card-body">
        <ErrorBox error={q.error ?? create.error ?? remove.error ?? save.error ?? portable.error ?? restore.error} />
        {restore.data && <div style={{ color: 'var(--warn)', marginBottom: 8 }}>{restore.data.message}</div>}
        {d?.lastError && <div style={{ color: 'var(--crit)', marginBottom: 8 }}>Last backup failed: {d.lastError}</div>}
        <p className="muted" style={{ marginTop: 0 }}>
          A backup is a zip with consistent copies of the three databases (taken while HIT SIEM keeps running), appsettings.json and the certificate, plus a checksum manifest.
          Stored in <span className="mono">{d?.settings.folder || d?.defaultFolder}</span>
          {d?.nextRunUtc ? `; next automatic backup ${fmtDateTime(d.nextRunUtc)} PKT` : ''}. Restore here (HIT SIEM restarts) or with the service stopped:{' '}
          <span className="mono">NetWatch.Service.exe --restore &lt;file&gt; [--restore-password]</span>.
        </p>
        {settings && (
          <>
            <div className="form-grid">
              <label className="check">
                <input type="checkbox" checked={settings.enabled} onChange={(e) => setSettings({ ...settings, enabled: e.target.checked })} /> Daily automatic backup
              </label>
              <label className="field">
                <span>Time (PKT)</span>
                <input type="time" value={settings.dailyAt} onChange={(e) => setSettings({ ...settings, dailyAt: e.target.value })} />
              </label>
              <label className="field">
                <span>Keep newest</span>
                <input type="number" min={1} max={365} value={settings.keepCount} onChange={(e) => setSettings({ ...settings, keepCount: Number(e.target.value) })} />
              </label>
              <label className="check">
                <input type="checkbox" checked={settings.includeMetrics} onChange={(e) => setSettings({ ...settings, includeMetrics: e.target.checked })} /> Include performance
                history (metrics.db)
              </label>
              <label className="field wide">
                <span>Folder (empty = data folder\backups)</span>
                <input className="mono" value={settings.folder ?? ''} onChange={(e) => setSettings({ ...settings, folder: e.target.value })} placeholder="E:\Backups\HIT SIEM or \\backupserver\netwatch" />
                <span className="hint">Use another disk or a share so a disk failure does not take the backups with it. The HIT SIEM service account needs write access.</span>
              </label>
            </div>
            <button className="small" disabled={save.isPending} onClick={() => save.mutate(settings)}>
              Save backup settings
            </button>
            {save.isSuccess && <span style={{ color: 'var(--ok)', marginLeft: 8 }}>Saved</span>}
          </>
        )}
        <div className="table-wrap" style={{ marginTop: 14 }}>
          <table className="data">
            <thead>
              <tr>
                <th>Backup</th>
                <th>Created (PKT)</th>
                <th>Contents</th>
                <th className="num">Size</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {(d?.items ?? []).map((b) => (
                <tr key={b.name}>
                  <td className="mono">{b.name}</td>
                  <td className="muted nowrap">{fmtDateTime(b.createdUtc)}</td>
                  <td className="muted">
                    {b.includesMetrics ? 'all data' : 'without history'} · {b.reason} · v{b.version}
                  </td>
                  <td className="num">{fmtBytes(b.sizeBytes)}</td>
                  <td className="nowrap">
                    <a className="btn small" href={`/api/backups/${b.name}`} download={b.name} title="Download">
                      <Download size={13} />
                    </a>{' '}
                    <button className="small" title="Restore this backup" disabled={restore.isPending} onClick={() => askRestore(b.name)}>
                      Restore
                    </button>{' '}
                    <button className="small" title="Delete" onClick={() => window.confirm(`Delete backup ${b.name}? This cannot be undone.`) && remove.mutate(b.name)}>
                      <Trash2 size={13} />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {d?.items.length === 0 && <div className="empty">No backups yet.</div>}
        </div>
      </div>
    </div>
  );
}
