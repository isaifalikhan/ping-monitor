import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, Eye, Play, Trash2 } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, downloadPost } from '../api/client';
import type { Group, ReportFileInfo, ReportFormatName, ReportFrequency, ReportPreview, ReportSchedule, ReportTypeInfo, ReportTypeName } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBox, Modal } from '../components/Modal';
import { fmtAgo, fmtBytes, fmtDateTime } from '../util/format';

type Tab = 'run' | 'schedules' | 'files';

const PERIODS: { key: string; label: string; days?: number; lastMonth?: boolean }[] = [
  { key: '1', label: 'Last 24 hours', days: 1 },
  { key: '7', label: 'Last 7 days', days: 7 },
  { key: '30', label: 'Last 30 days', days: 30 },
  { key: 'month', label: 'Previous calendar month', lastMonth: true },
  { key: 'custom', label: 'Custom…' },
];

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

/** Midnight of a PKT calendar day as a UTC Date (PKT = UTC+5, no DST). */
function pktMidnightUtc(year: number, month: number, day: number): Date {
  return new Date(Date.UTC(year, month, day) - 5 * 3600_000);
}

function pktToday(): { y: number; m: number; d: number } {
  const now = new Date(Date.now() + 5 * 3600_000);
  return { y: now.getUTCFullYear(), m: now.getUTCMonth(), d: now.getUTCDate() };
}

/** "2026-11-01" (a PKT day) → UTC Date of its midnight. */
function parseDay(value: string): Date | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  return m ? pktMidnightUtc(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : null;
}

function GroupSelect({ value, onChange, groups }: { value: string; onChange: (v: string) => void; groups: Group[] }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} aria-label="Group">
      <option value="">All devices</option>
      {groups.map((g) => (
        <option key={g.id} value={g.id}>
          {g.name}
        </option>
      ))}
    </select>
  );
}

// ------------------------------------------------------------------ run now

function RunTab({ types, groups }: { types: ReportTypeInfo[]; groups: Group[] }) {
  const [type, setType] = useState<ReportTypeName>('Availability');
  const [format, setFormat] = useState<ReportFormatName>('Pdf');
  const [period, setPeriod] = useState('7');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [group, setGroup] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [preview, setPreview] = useState<ReportPreview | null>(null);
  const info = types.find((t) => t.type === type);

  const range = useMemo((): { fromUtc: string; toUtc: string } | null => {
    const p = PERIODS.find((x) => x.key === period);
    if (p?.days) return { fromUtc: new Date(Date.now() - p.days * 86400_000).toISOString(), toUtc: new Date().toISOString() };
    if (p?.lastMonth) {
      const t = pktToday();
      return { fromUtc: pktMidnightUtc(t.y, t.m - 1, 1).toISOString(), toUtc: pktMidnightUtc(t.y, t.m, 1).toISOString() };
    }
    const f = parseDay(from);
    const e = parseDay(to);
    if (!f || !e) return null;
    return { fromUtc: f.toISOString(), toUtc: new Date(e.getTime() + 86400_000).toISOString() };
  }, [period, from, to]);

  const run = async () => {
    setError(null);
    setBusy(true);
    try {
      const r = range ?? { fromUtc: new Date(Date.now() - 7 * 86400_000).toISOString(), toUtc: new Date().toISOString() };
      await downloadPost('/api/reports/run', { type, format, ...r, groupId: group ? Number(group) : null }, `netwatch-report.${format === 'Pdf' ? 'pdf' : 'csv'}`);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const showPreview = async () => {
    setError(null);
    setBusy(true);
    try {
      const r = range ?? { fromUtc: new Date(Date.now() - 7 * 86400_000).toISOString(), toUtc: new Date().toISOString() };
      setPreview(await api.post<ReportPreview>('/api/reports/preview', { type, format, ...r, groupId: group ? Number(group) : null }));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="grid cols-2" style={{ alignItems: 'start' }}>
      {preview && <ReportPreviewDialog preview={preview} onClose={() => setPreview(null)} onDownload={() => void run()} />}
      <div className="card card-body">
        <h2 style={{ marginBottom: 12 }}>Create a report</h2>
        <ErrorBox error={error} />
        <div className="form-grid">
          <label className="field">
            <span>Report</span>
            <select value={type} onChange={(e) => setType(e.target.value as ReportTypeName)}>
              {types.map((t) => (
                <option key={t.type} value={t.type}>
                  {t.name}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Format</span>
            <select value={format} onChange={(e) => setFormat(e.target.value as ReportFormatName)}>
              <option value="Pdf">PDF</option>
              <option value="Csv">CSV (Excel)</option>
            </select>
          </label>
          {info?.hasPeriod !== false && (
            <label className="field">
              <span>Period</span>
              <select value={period} onChange={(e) => setPeriod(e.target.value)}>
                {PERIODS.map((p) => (
                  <option key={p.key} value={p.key}>
                    {p.label}
                  </option>
                ))}
              </select>
            </label>
          )}
          <label className="field">
            <span>Devices</span>
            <GroupSelect value={group} onChange={setGroup} groups={groups} />
          </label>
          {period === 'custom' && info?.hasPeriod !== false && (
            <>
              <label className="field">
                <span>From (PKT day)</span>
                <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
              </label>
              <label className="field">
                <span>To (PKT day, inclusive)</span>
                <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
              </label>
            </>
          )}
        </div>
        <p className="muted" style={{ margin: '10px 0' }}>
          {info?.description}
        </p>
        <div style={{ display: 'flex', gap: 8 }}>
          <button disabled={busy || (period === 'custom' && !range)} onClick={() => void showPreview()}>
            <Eye size={15} /> Preview
          </button>
          <button className="primary" disabled={busy || (period === 'custom' && !range)} onClick={() => void run()}>
            <Download size={15} /> {busy ? 'Generating…' : 'Generate and download'}
          </button>
        </div>
      </div>
      <div className="card card-body">
        <h2 style={{ marginBottom: 10 }}>Available reports</h2>
        <table className="data">
          <tbody>
            {types.map((t) => (
              <tr key={t.type}>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <strong>{t.name}</strong>
                </td>
                <td className="muted">{t.description}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="muted" style={{ marginTop: 10 }}>
          Times are Pakistan time (PKT). Reports are generated on this server; nothing leaves the network unless a schedule e-mails it through your SMTP relay.
        </p>
      </div>
    </div>
  );
}

/** On-screen rendering of a report document (same sections as the PDF). */
function ReportPreviewDialog({ preview, onClose, onDownload }: { preview: ReportPreview; onClose: () => void; onDownload: () => void }) {
  return (
    <Modal
      title={preview.title}
      onClose={onClose}
      wide
      footer={
        <>
          <button onClick={onClose}>Close</button>
          <button className="primary" onClick={onDownload}>
            <Download size={15} /> Download
          </button>
        </>
      }
    >
      <p className="muted">{preview.subtitle}</p>
      {preview.sections.map((s, i) => (
        <section key={i} style={{ marginTop: 14 }}>
          <h3>{s.heading}</h3>
          {s.note && <p className="faint">{s.note}</p>}
          {s.summary.length > 0 && (
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))', gap: 8, margin: '6px 0' }}>
              {s.summary.map((x) => (
                <div key={x.label} className="card card-body" style={{ padding: 8 }}>
                  <div className="label">{x.label}</div>
                  <strong>{x.value}</strong>
                </div>
              ))}
            </div>
          )}
          {s.bars?.items.map((b) => (
            <div key={b.label} style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 12 }}>
              <span className="clip" style={{ width: 240 }}>
                {b.label}
              </span>
              <span style={{ flex: 1, background: 'var(--bg-elev-2)', height: 8, borderRadius: 4 }}>
                <span style={{ display: 'block', width: `${Math.min(100, (b.value / (s.bars!.max || 1)) * 100)}%`, height: 8, borderRadius: 4, background: 'var(--accent)' }} />
              </span>
              <span className="mono" style={{ width: 70, textAlign: 'right' }}>
                {b.text}
              </span>
            </div>
          ))}
          {s.table && (
            <div className="table-wrap" style={{ maxHeight: 360 }}>
              <table className="data">
                <thead>
                  <tr>
                    {s.table.headers.map((h, c) => (
                      <th key={c} className={s.table!.numeric.includes(c) ? 'num' : undefined}>
                        {h}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {s.table.rows.map((r, ri) => (
                    <tr key={ri}>
                      {r.map((cell, c) => (
                        <td key={c} className={s.table!.numeric.includes(c) ? 'num' : undefined}>
                          {cell}
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
              {s.table.rows.length === 0 && <div className="empty">{s.table.empty ?? 'No rows.'}</div>}
              {s.table.totalRows > s.table.rows.length && <div className="faint">{s.table.totalRows - s.table.rows.length} more row(s) in the download.</div>}
            </div>
          )}
        </section>
      ))}
    </Modal>
  );
}

// ------------------------------------------------------------------ schedules

interface ScheduleForm {
  name: string;
  isEnabled: boolean;
  reportType: ReportTypeName;
  format: ReportFormatName;
  frequency: ReportFrequency;
  timeOfDay: string;
  dayOfWeek: number;
  dayOfMonth: number;
  groupId: string;
  emailTo: string;
}

function ScheduleDialog({ schedule, types, groups, onClose }: { schedule?: ReportSchedule; types: ReportTypeInfo[]; groups: Group[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [f, setF] = useState<ScheduleForm>({
    name: schedule?.name ?? '',
    isEnabled: schedule?.isEnabled ?? true,
    reportType: schedule?.reportType ?? 'Availability',
    format: schedule?.format ?? 'Pdf',
    frequency: schedule?.frequency ?? 'Weekly',
    timeOfDay: schedule?.timeOfDay ?? '07:00',
    dayOfWeek: schedule?.dayOfWeek ?? 1,
    dayOfMonth: schedule?.dayOfMonth ?? 1,
    groupId: schedule?.groupId ? String(schedule.groupId) : '',
    emailTo: schedule?.emailTo ?? '',
  });
  const set = <K extends keyof ScheduleForm>(k: K, v: ScheduleForm[K]) => setF((x) => ({ ...x, [k]: v }));
  const save = useMutation({
    mutationFn: () => {
      const body = { ...f, groupId: f.groupId ? Number(f.groupId) : null, emailTo: f.emailTo.trim() || null };
      return schedule ? api.put(`/api/reports/schedules/${schedule.id}`, body) : api.post('/api/reports/schedules', body);
    },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['report-schedules'] });
      onClose();
    },
  });

  return (
    <Modal
      title={schedule ? 'Edit report schedule' : 'New report schedule'}
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Cancel</button>
          <button className="primary" disabled={!f.name.trim() || save.isPending} onClick={() => save.mutate()}>
            Save
          </button>
        </>
      }
    >
      <ErrorBox error={save.error} />
      <div className="form-grid">
        <label className="field wide">
          <span>Name</span>
          <input maxLength={100} value={f.name} onChange={(e) => set('name', e.target.value)} placeholder="Weekly availability for management" />
        </label>
        <label className="field">
          <span>Report</span>
          <select value={f.reportType} onChange={(e) => set('reportType', e.target.value as ReportTypeName)}>
            {types.map((t) => (
              <option key={t.type} value={t.type}>
                {t.name}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Format</span>
          <select value={f.format} onChange={(e) => set('format', e.target.value as ReportFormatName)}>
            <option value="Pdf">PDF</option>
            <option value="Csv">CSV</option>
          </select>
        </label>
        <label className="field">
          <span>Frequency</span>
          <select value={f.frequency} onChange={(e) => set('frequency', e.target.value as ReportFrequency)}>
            <option value="Daily">Daily (covers the previous day)</option>
            <option value="Weekly">Weekly (covers the previous 7 days)</option>
            <option value="Monthly">Monthly (covers the previous month)</option>
          </select>
        </label>
        <label className="field">
          <span>Time (PKT)</span>
          <input type="time" value={f.timeOfDay} onChange={(e) => set('timeOfDay', e.target.value)} />
        </label>
        {f.frequency === 'Weekly' && (
          <label className="field">
            <span>Day</span>
            <select value={f.dayOfWeek} onChange={(e) => set('dayOfWeek', Number(e.target.value))}>
              {DAYS.map((d, i) => (
                <option key={d} value={i}>
                  {d}
                </option>
              ))}
            </select>
          </label>
        )}
        {f.frequency === 'Monthly' && (
          <label className="field">
            <span>Day of month</span>
            <input type="number" min={1} max={28} value={f.dayOfMonth} onChange={(e) => set('dayOfMonth', Number(e.target.value))} />
          </label>
        )}
        <label className="field">
          <span>Devices</span>
          <GroupSelect value={f.groupId} onChange={(v) => set('groupId', v)} groups={groups} />
        </label>
        <label className="field wide">
          <span>E-mail to (optional)</span>
          <input value={f.emailTo} onChange={(e) => set('emailTo', e.target.value)} placeholder="noc@corp.local, it-manager@corp.local" />
          <span className="hint">Sent through the SMTP relay of Settings → Notifications. The report is always saved on the server too.</span>
        </label>
        <label className="check">
          <input type="checkbox" checked={f.isEnabled} onChange={(e) => set('isEnabled', e.target.checked)} /> Enabled
        </label>
      </div>
    </Modal>
  );
}

function SchedulesTab({ types, groups }: { types: ReportTypeInfo[]; groups: Group[] }) {
  const { can } = useAuth();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['report-schedules'], queryFn: () => api.get<ReportSchedule[]>('/api/reports/schedules'), refetchInterval: 30000 });
  const [editing, setEditing] = useState<ReportSchedule | 'new' | null>(null);
  const run = useMutation({
    mutationFn: (id: number) => api.post<{ status: string }>(`/api/reports/schedules/${id}/run`),
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: ['report-schedules'] });
      void qc.invalidateQueries({ queryKey: ['report-files'] });
    },
  });
  const remove = useMutation({ mutationFn: (id: number) => api.del(`/api/reports/schedules/${id}`), onSuccess: () => void qc.invalidateQueries({ queryKey: ['report-schedules'] }) });
  const groupName = (id?: number | null) => (id ? groups.find((g) => g.id === id)?.name ?? `#${id}` : 'All devices');
  const typeName = (t: ReportTypeName) => types.find((x) => x.type === t)?.name ?? t;
  const when = (s: ReportSchedule) =>
    s.frequency === 'Daily' ? `Daily ${s.timeOfDay}` : s.frequency === 'Weekly' ? `${DAYS[s.dayOfWeek]}s ${s.timeOfDay}` : `Day ${s.dayOfMonth} of each month, ${s.timeOfDay}`;

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 10 }}>
        <span className="muted">Scheduled reports are saved on the server (Stored reports) and optionally e-mailed.</span>
        <span style={{ flex: 1 }} />
        {can('Operator') && (
          <button className="primary" onClick={() => setEditing('new')}>
            New schedule
          </button>
        )}
      </div>
      <ErrorBox error={q.error ?? run.error ?? remove.error} />
      {run.data && <div className="card card-body" style={{ marginBottom: 10 }}>{run.data.status}</div>}
      <div className="card table-wrap">
        <table className="data">
          <thead>
            <tr>
              <th>Name</th>
              <th>Report</th>
              <th>When (PKT)</th>
              <th>Devices</th>
              <th>E-mail</th>
              <th>Last run</th>
              <th>Next run</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(q.data ?? []).map((s) => (
              <tr key={s.id} style={{ opacity: s.isEnabled ? 1 : 0.55 }}>
                <td>
                  <strong>{s.name}</strong>
                  {!s.isEnabled && <span className="tag">disabled</span>}
                </td>
                <td>
                  {typeName(s.reportType)} <span className="muted">({s.format.toUpperCase()})</span>
                </td>
                <td className="nowrap">{when(s)}</td>
                <td>{groupName(s.groupId)}</td>
                <td className="muted">{s.emailTo ?? '—'}</td>
                <td>
                  {s.lastRunUtc ? (
                    <span title={s.lastStatus ?? ''} style={{ color: s.lastStatus?.startsWith('Failed') ? 'var(--crit)' : undefined }}>
                      {fmtAgo(s.lastRunUtc)} · {s.lastStatus?.startsWith('Failed') ? 'failed' : 'OK'}
                    </span>
                  ) : (
                    <span className="faint">never</span>
                  )}
                </td>
                <td className="muted nowrap">{s.isEnabled ? fmtDateTime(s.nextRunUtc) : '—'}</td>
                <td className="nowrap">
                  {can('Operator') && (
                    <>
                      <button className="small" title="Run now" disabled={run.isPending} onClick={() => run.mutate(s.id)}>
                        <Play size={13} />
                      </button>{' '}
                      <button className="small" onClick={() => setEditing(s)}>
                        Edit
                      </button>{' '}
                      <button className="small" title="Delete" onClick={() => window.confirm(`Delete schedule "${s.name}"?`) && remove.mutate(s.id)}>
                        <Trash2 size={13} />
                      </button>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {q.data?.length === 0 && <div className="empty">No scheduled reports yet.</div>}
      </div>
      {editing && <ScheduleDialog schedule={editing === 'new' ? undefined : editing} types={types} groups={groups} onClose={() => setEditing(null)} />}
    </>
  );
}

// ------------------------------------------------------------------ stored files

function FilesTab() {
  const { can } = useAuth();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['report-files'], queryFn: () => api.get<ReportFileInfo[]>('/api/reports/files'), refetchInterval: 60000 });
  const remove = useMutation({ mutationFn: (path: string) => api.del(`/api/reports/files/${path}`), onSuccess: () => void qc.invalidateQueries({ queryKey: ['report-files'] }) });
  return (
    <>
      <ErrorBox error={q.error ?? remove.error} />
      <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 260px)' }}>
        <table className="data">
          <thead>
            <tr>
              <th>File</th>
              <th>Created (PKT)</th>
              <th className="num">Size</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(q.data ?? []).map((f) => (
              <tr key={f.path}>
                <td className="mono">
                  <a href={`/api/reports/files/${f.path}`} download={f.name}>
                    {f.path}
                  </a>
                </td>
                <td className="muted nowrap">{fmtDateTime(f.createdUtc)}</td>
                <td className="num">{fmtBytes(f.sizeBytes)}</td>
                <td>
                  {can('Operator') && (
                    <button className="small" title="Delete" onClick={() => window.confirm(`Delete ${f.path}?`) && remove.mutate(f.path)}>
                      <Trash2 size={13} />
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {q.data?.length === 0 && <div className="empty">No stored reports. Scheduled reports appear here (kept for 400 days).</div>}
      </div>
    </>
  );
}

/** Reports: generate PDF/CSV on demand, manage schedules, browse stored reports. */
export function ReportsPage() {
  const [params, setParams] = useSearchParams();
  const tab = (params.get('tab') as Tab) ?? 'run';
  const types = useQuery({ queryKey: ['report-types'], queryFn: () => api.get<ReportTypeInfo[]>('/api/reports/types'), staleTime: Infinity });
  const groups = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });
  const tabs: { key: Tab; label: string }[] = [
    { key: 'run', label: 'Create report' },
    { key: 'schedules', label: 'Scheduled reports' },
    { key: 'files', label: 'Stored reports' },
  ];
  return (
    <>
      <div className="page-head">
        <h1>Reports</h1>
      </div>
      <ErrorBox error={types.error} />
      <div className="tabs">
        {tabs.map((t) => (
          <button key={t.key} className={tab === t.key ? 'active' : ''} onClick={() => setParams({ tab: t.key }, { replace: true })}>
            {t.label}
          </button>
        ))}
      </div>
      {tab === 'run' && <RunTab types={types.data ?? []} groups={groups.data ?? []} />}
      {tab === 'schedules' && <SchedulesTab types={types.data ?? []} groups={groups.data ?? []} />}
      {tab === 'files' && <FilesTab />}
    </>
  );
}
