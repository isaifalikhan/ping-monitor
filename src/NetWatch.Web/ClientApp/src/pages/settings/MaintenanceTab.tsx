import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { useState } from 'react';
import { api } from '../../api/client';
import type { DeviceListItem, DeviceType, Group, MaintenanceWindow, Recurrence, RuleScope } from '../../api/types';
import { DEVICE_TYPES } from '../../api/types';
import { Confirm, ErrorBox, Modal } from '../../components/Modal';
import { fmtDateTime, toMs, typeLabel } from '../../util/format';
import { groupOptions } from './GroupsTab';
import { scopeText } from './RulesTab';

const DAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const PKT_OFFSET_MS = 5 * 3600e3;

/** UTC timestamp → "YYYY-MM-DDTHH:mm" in Pakistan time (for datetime-local inputs). */
function toPktInput(utc?: string | null): string {
  const ms = toMs(utc);
  if (!Number.isFinite(ms)) return '';
  return new Date(ms + PKT_OFFSET_MS).toISOString().slice(0, 16);
}

/** "YYYY-MM-DDTHH:mm" entered in Pakistan time → ISO UTC. */
function fromPktInput(value: string): string | null {
  if (!value) return null;
  const d = new Date(`${value}:00+05:00`);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

interface FormState {
  name: string;
  isEnabled: boolean;
  recurrence: Recurrence;
  start: string;
  end: string;
  startTimeLocal: string;
  days: string[];
  cronExpression: string;
  durationMin: number;
  reason: string;
  scope: RuleScope;
}

function WindowDialog({ window: w, onClose }: { window?: MaintenanceWindow; onClose: () => void }) {
  const qc = useQueryClient();
  const devices = useQuery({ queryKey: ['devices', 'all'], queryFn: () => api.get<DeviceListItem[]>('/api/devices') });
  const groups = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });
  const now = Date.now();
  const [f, setF] = useState<FormState>(() => ({
    name: w?.name ?? '',
    isEnabled: w?.isEnabled ?? true,
    recurrence: w?.recurrence ?? 'Once',
    start: toPktInput(w?.startUtc ?? new Date(now + 3600e3).toISOString()),
    end: toPktInput(w?.endUtc ?? new Date(now + 3 * 3600e3).toISOString()),
    startTimeLocal: w?.startTimeLocal ?? '02:00',
    days: (w?.daysOfWeek ?? 'Sun').split(',').map((d) => d.trim()).filter(Boolean),
    cronExpression: w?.cronExpression ?? '0 2 1 * *',
    durationMin: w?.durationMin || 120,
    reason: w?.reason ?? '',
    scope: w?.scope ?? { deviceIds: [], groupIds: [], deviceTypes: [], tags: [], importantInterfacesOnly: false },
  }));
  const [tags, setTags] = useState((w?.scope.tags ?? []).join(', '));
  const [error, setError] = useState<unknown>(null);
  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setF((s) => ({ ...s, [k]: v }));
  const setScope = <K extends keyof RuleScope>(k: K, v: RuleScope[K]) => setF((s) => ({ ...s, scope: { ...s.scope, [k]: v } }));

  const save = async () => {
    try {
      const body = {
        name: f.name,
        isEnabled: f.isEnabled,
        recurrence: f.recurrence,
        startUtc: f.recurrence === 'Once' ? fromPktInput(f.start) : null,
        endUtc: f.recurrence === 'Once' ? fromPktInput(f.end) : null,
        startTimeLocal: f.startTimeLocal,
        daysOfWeek: f.days.join(','),
        cronExpression: f.cronExpression,
        durationMin: f.durationMin,
        reason: f.reason,
        scope: { ...f.scope, tags: tags.split(',').map((t) => t.trim()).filter(Boolean) },
      };
      if (w) await api.put(`/api/maintenance/${w.id}`, body);
      else await api.post('/api/maintenance', body);
      await qc.invalidateQueries({ queryKey: ['maintenance'] });
      await qc.invalidateQueries({ queryKey: ['devices'] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Modal
      title={w ? `Edit maintenance window: ${w.name}` : 'New maintenance window'}
      onClose={onClose}
      wide
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
      <div className="form-grid">
        <label className="field">
          <span>Name</span>
          <input value={f.name} onChange={(e) => set('name', e.target.value)} maxLength={200} placeholder="Monthly patching – DC servers" />
        </label>
        <label className="field">
          <span>Schedule</span>
          <select value={f.recurrence} onChange={(e) => set('recurrence', e.target.value as Recurrence)}>
            <option value="Once">One-off (start and end)</option>
            <option value="Daily">Every day</option>
            <option value="Weekly">Weekly on selected days</option>
            <option value="Cron">Advanced (cron expression)</option>
          </select>
        </label>
        {f.recurrence === 'Once' && (
          <>
            <label className="field">
              <span>Start (PKT)</span>
              <input type="datetime-local" value={f.start} onChange={(e) => set('start', e.target.value)} />
            </label>
            <label className="field">
              <span>End (PKT)</span>
              <input type="datetime-local" value={f.end} onChange={(e) => set('end', e.target.value)} />
            </label>
          </>
        )}
        {(f.recurrence === 'Daily' || f.recurrence === 'Weekly') && (
          <label className="field">
            <span>Start time (PKT)</span>
            <input type="time" value={f.startTimeLocal} onChange={(e) => set('startTimeLocal', e.target.value)} />
          </label>
        )}
        {f.recurrence === 'Cron' && (
          <label className="field">
            <span>Cron expression (PKT)</span>
            <input className="mono" value={f.cronExpression} onChange={(e) => set('cronExpression', e.target.value)} />
            <span className="hint">minute hour day-of-month month day-of-week, e.g. “0 2 1-7 * 0” = 02:00 on the first Sunday of the month.</span>
          </label>
        )}
        {f.recurrence !== 'Once' && (
          <label className="field">
            <span>Duration (minutes)</span>
            <input type="number" min={1} max={10080} value={f.durationMin} onChange={(e) => set('durationMin', Number(e.target.value))} />
          </label>
        )}
        {f.recurrence === 'Weekly' && (
          <div className="field wide">
            <span>Days</span>
            <div className="toolbar">
              {DAYS.map((d) => (
                <label key={d} className="check">
                  <input type="checkbox" checked={f.days.includes(d)} onChange={(e) => set('days', e.target.checked ? [...f.days, d] : f.days.filter((x) => x !== d))} />
                  {d}
                </label>
              ))}
            </div>
          </div>
        )}
        <label className="field wide">
          <span>Reason</span>
          <input value={f.reason} onChange={(e) => set('reason', e.target.value)} maxLength={1000} placeholder="Change request / ticket" />
        </label>

        <div className="field wide">
          <span>Applies to — device types (fill in at least one criterion below)</span>
          <div className="toolbar">
            {DEVICE_TYPES.map((t) => (
              <label key={t} className="check">
                <input
                  type="checkbox"
                  checked={f.scope.deviceTypes.includes(t)}
                  onChange={(e) => setScope('deviceTypes', e.target.checked ? [...f.scope.deviceTypes, t] : f.scope.deviceTypes.filter((x: DeviceType) => x !== t))}
                />
                {typeLabel(t)}
              </label>
            ))}
          </div>
        </div>
        <label className="field">
          <span>Devices</span>
          <select multiple size={7} value={f.scope.deviceIds.map(String)} onChange={(e) => setScope('deviceIds', Array.from(e.target.selectedOptions).map((o) => Number(o.value)))}>
            {(devices.data ?? [])
              .slice()
              .sort((a, b) => a.name.localeCompare(b.name))
              .map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name} ({d.ipAddress})
                </option>
              ))}
          </select>
          <span className="hint">Ctrl+click to select several.</span>
        </label>
        <label className="field">
          <span>Groups (including sub-groups)</span>
          <select multiple size={7} value={f.scope.groupIds.map(String)} onChange={(e) => setScope('groupIds', Array.from(e.target.selectedOptions).map((o) => Number(o.value)))}>
            {groupOptions(groups.data ?? []).map((g) => (
              <option key={g.id} value={g.id}>
                {g.label}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Device tags</span>
          <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="erp, dc" />
        </label>
        <label className="check" style={{ alignSelf: 'end' }}>
          <input type="checkbox" checked={f.isEnabled} onChange={(e) => set('isEnabled', e.target.checked)} /> Enabled
        </label>
      </div>
      <div className="muted" style={{ marginTop: 8 }}>
        Within the scope filters, every filled-in criterion must match (e.g. type Server <em>and</em> group “Data Center”). During the window devices are still polled and graphed, but no new
        alerts are raised; open alerts still resolve normally.
      </div>
    </Modal>
  );
}

export function MaintenanceTab() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['maintenance'], queryFn: () => api.get<MaintenanceWindow[]>('/api/maintenance'), refetchInterval: 30000 });
  const groups = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });
  const [edit, setEdit] = useState<MaintenanceWindow | 'new' | null>(null);
  const [del, setDel] = useState<MaintenanceWindow | null>(null);
  const [error, setError] = useState<unknown>(null);

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <span className="muted">Scheduled or one-off periods without new alerts. For a quick ad-hoc pause of single devices use the Maintenance button on the device list.</span>
        <span style={{ flex: 1 }} />
        <button className="primary" onClick={() => setEdit('new')}>
          <Plus size={16} /> New window
        </button>
      </div>
      <ErrorBox error={error ?? q.error} />
      <div className="card">
        <table className="data">
          <thead>
            <tr>
              <th>State</th>
              <th>Name</th>
              <th>Schedule</th>
              <th>Applies to</th>
              <th className="num">Devices</th>
              <th>Next / until</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(q.data ?? []).map((w) => (
              <tr key={w.id}>
                <td>
                  {!w.isEnabled ? <span className="badge s-unknown">disabled</span> : w.isActive ? <span className="badge s-maintenance">active</span> : <span className="badge s-up">scheduled</span>}
                </td>
                <td>
                  <strong>{w.name}</strong>
                  {w.reason && (
                    <div className="muted" style={{ fontSize: 12.5 }}>
                      {w.reason}
                    </div>
                  )}
                </td>
                <td className="muted">{w.recurrence === 'Once' ? `${fmtDateTime(w.startUtc)} → ${fmtDateTime(w.endUtc)}` : w.schedule}</td>
                <td className="muted">{scopeText(w.scope, groups.data ?? [])}</td>
                <td className="num">{w.devicesInScope}</td>
                <td className="muted nowrap">{w.isActive ? `until ${fmtDateTime(w.activeUntilUtc)}` : w.nextStartUtc ? `next ${fmtDateTime(w.nextStartUtc)}` : 'ended'}</td>
                <td className="nowrap" style={{ textAlign: 'right' }}>
                  <button className="small" onClick={() => setEdit(w)}>
                    Edit
                  </button>{' '}
                  <button className="small danger" onClick={() => setDel(w)}>
                    Delete
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {q.data?.length === 0 && <div className="empty">No maintenance windows.</div>}
      </div>
      {edit && <WindowDialog window={edit === 'new' ? undefined : edit} onClose={() => setEdit(null)} />}
      {del && (
        <Confirm
          title={`Delete ${del.name}?`}
          danger
          confirmLabel="Delete"
          message="If the window is active, alerts for its devices are raised again from the next poll."
          onClose={() => setDel(null)}
          onConfirm={() =>
            void (async () => {
              try {
                await api.del(`/api/maintenance/${del.id}`);
                await qc.invalidateQueries({ queryKey: ['maintenance'] });
              } catch (e) {
                setError(e);
              }
              setDel(null);
            })()
          }
        />
      )}
    </>
  );
}
