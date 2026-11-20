import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { useState } from 'react';
import { api } from '../../api/client';
import type { DeviceType, EventFilter, Group, Operator, Rule, RuleKey, RuleScope, RuleType, Severity } from '../../api/types';
import { DEVICE_TYPES, SEVERITIES } from '../../api/types';
import { SeverityBadge } from '../../components/Badges';
import { Confirm, ErrorBox, Modal } from '../../components/Modal';
import { typeLabel } from '../../util/format';
import { groupOptions } from './GroupsTab';

const OPERATORS: { value: Operator; label: string }[] = [
  { value: 'GreaterThan', label: '>' },
  { value: 'GreaterOrEqual', label: '≥' },
  { value: 'LessThan', label: '<' },
  { value: 'LessOrEqual', label: '≤' },
  { value: 'Equal', label: '=' },
  { value: 'NotEqual', label: '≠' },
];

const emptyScope: RuleScope = { deviceIds: [], groupIds: [], deviceTypes: [], tags: [], importantInterfacesOnly: false };

const RULE_TYPES: { value: RuleType; label: string }[] = [
  { value: 'Threshold', label: 'Threshold (metric above/below a value)' },
  { value: 'StateChange', label: 'State change (down, not running…)' },
  { value: 'EventMatch', label: 'Event (syslog, trap, Windows event, reboot)' },
  { value: 'CountInWindow', label: 'Count in window (N events in M minutes)' },
  { value: 'Absence', label: 'Absence (no data for N minutes)' },
];

const span = (sec: number) => (sec >= 60 ? `${+(sec / 60).toFixed(1)} min` : `${sec} s`);

function filterText(f: EventFilter): string {
  const parts: string[] = [];
  if (f.minSeverity) parts.push(`severity ≥ ${f.minSeverity}`);
  if (f.pattern) parts.push(`matches /${f.pattern}/`);
  if (f.source) parts.push(`source contains “${f.source}”`);
  if (f.eventIds?.length) parts.push(`event ID ${f.eventIds.join(', ')}`);
  if (f.oid) parts.push(`OID ${f.oid}`);
  return parts.length ? ` where ${parts.join(', ')}` : '';
}

function describe(rule: Rule, keys: RuleKey[]): string {
  const name = keys.find((k) => k.key === rule.key)?.displayName ?? rule.key;
  const dur = rule.durationSec > 0 ? ` for ${span(rule.durationSec)}` : '';
  switch (rule.ruleType) {
    case 'Threshold': {
      const op = OPERATORS.find((o) => o.value === rule.operator)?.label ?? '?';
      return `${name} ${op} ${rule.threshold}${dur}${rule.clearThreshold != null ? ` (clears at ${rule.clearThreshold})` : ''}`;
    }
    case 'StateChange':
      return `${name}${dur}`;
    case 'CountInWindow':
      return `≥ ${rule.countThreshold} × ${name} in ${span(rule.windowSec)}${filterText(rule.filter)}`;
    case 'Absence':
      return `no ${name} data for ${span(rule.durationSec)}`;
    default:
      return `${name}${filterText(rule.filter)}`;
  }
}

export function scopeText(s: RuleScope, groups: Group[]): string {
  const parts: string[] = [];
  if (s.deviceTypes.length) parts.push(s.deviceTypes.map(typeLabel).join(', '));
  if (s.groupIds.length) parts.push(`groups: ${s.groupIds.map((id) => groups.find((g) => g.id === id)?.name ?? id).join(', ')}`);
  if (s.tags.length) parts.push(`tags: ${s.tags.join(', ')}`);
  if (s.deviceIds.length) parts.push(`${s.deviceIds.length} device(s)`);
  if (s.importantInterfacesOnly) parts.push('important ports only');
  return parts.length ? parts.join(' · ') : 'all devices';
}

function RuleDialog({ rule, keys, groups, onClose }: { rule?: Rule; keys: RuleKey[]; groups: Group[]; onClose: () => void }) {
  const qc = useQueryClient();
  const [r, setR] = useState({
    name: rule?.name ?? '',
    description: rule?.description ?? '',
    isEnabled: rule?.isEnabled ?? true,
    ruleType: (rule?.ruleType ?? 'Threshold') as RuleType,
    severity: (rule?.severity ?? 'Warning') as Severity,
    key: rule?.key ?? keys.find((k) => k.ruleType === 'Threshold')?.key ?? '',
    operator: (rule?.operator ?? 'GreaterThan') as Operator,
    threshold: rule?.threshold ?? 90,
    clearThreshold: rule?.clearThreshold ?? null,
    durationSec: rule?.durationSec ?? 300,
    scope: rule?.scope ?? emptyScope,
    windowSec: rule?.windowSec || 300,
    countThreshold: rule?.countThreshold || 10,
    escalateAfterMin: rule?.escalateAfterMin ?? null,
  });
  const [filter, setFilter] = useState<EventFilter>(rule?.filter ?? { eventIds: [] });
  const [eventIds, setEventIds] = useState((rule?.filter?.eventIds ?? []).join(', '));
  const [tags, setTags] = useState((rule?.scope.tags ?? []).join(', '));
  const [error, setError] = useState<unknown>(null);
  const builtIn = !!rule?.isBuiltIn;

  const set = <K extends keyof typeof r>(k: K, v: (typeof r)[K]) => setR((s) => ({ ...s, [k]: v }));
  const setScope = <K extends keyof RuleScope>(k: K, v: RuleScope[K]) => setR((s) => ({ ...s, scope: { ...s.scope, [k]: v } }));

  const save = async () => {
    try {
      const isEvent = r.ruleType === 'EventMatch' || r.ruleType === 'CountInWindow' || r.ruleType === 'Absence';
      const body = {
        ...r,
        scope: { ...r.scope, tags: tags.split(',').map((t) => t.trim()).filter(Boolean) },
        filter: isEvent
          ? {
              ...filter,
              pattern: filter.pattern?.trim() || null,
              source: filter.source?.trim() || null,
              oid: filter.oid?.trim() || null,
              eventIds: eventIds.split(/[,\s]+/).map(Number).filter((n) => Number.isInteger(n) && n > 0),
            }
          : null,
      };
      if (rule) await api.put(`/api/rules/${rule.id}`, body);
      else await api.post('/api/rules', body);
      await qc.invalidateQueries({ queryKey: ['rules'] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Modal
      title={rule ? `Edit rule: ${rule.name}` : 'New alert rule'}
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
          <input value={r.name} onChange={(e) => set('name', e.target.value)} maxLength={200} />
        </label>
        <label className="field">
          <span>Rule type</span>
          <select
            value={r.ruleType}
            disabled={builtIn}
            onChange={(e) => {
              const t = e.target.value as RuleType;
              setR((s) => ({ ...s, ruleType: t, key: keys.find((k) => k.ruleType === t)?.key ?? '' }));
            }}
          >
            {RULE_TYPES.map((t) => (
              <option key={t.value} value={t.value}>
                {t.label}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>{r.ruleType === 'Threshold' ? 'Metric' : r.ruleType === 'StateChange' ? 'State' : r.ruleType === 'Absence' ? 'Data source' : 'Event'}</span>
          <select value={r.key} disabled={builtIn} onChange={(e) => set('key', e.target.value)}>
            {keys
              .filter((k) => k.ruleType === r.ruleType)
              .map((k) => (
                <option key={k.key} value={k.key}>
                  {k.displayName}
                </option>
              ))}
          </select>
        </label>
        <label className="field">
          <span>Severity</span>
          <select value={r.severity} onChange={(e) => set('severity', e.target.value as Severity)}>
            {SEVERITIES.map((s) => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </label>
        {r.ruleType === 'Threshold' && (
          <>
            <label className="field">
              <span>Condition</span>
              <div style={{ display: 'flex', gap: 6 }}>
                <select value={r.operator} onChange={(e) => set('operator', e.target.value as Operator)} style={{ width: 70 }}>
                  {OPERATORS.map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </select>
                <input type="number" value={r.threshold ?? ''} onChange={(e) => set('threshold', Number(e.target.value))} style={{ flex: 1 }} />
              </div>
            </label>
            <label className="field">
              <span>Clear threshold (hysteresis, optional)</span>
              <input type="number" value={r.clearThreshold ?? ''} onChange={(e) => set('clearThreshold', e.target.value === '' ? null : Number(e.target.value))} />
            </label>
          </>
        )}
        {(r.ruleType === 'Threshold' || r.ruleType === 'StateChange') && (
          <label className="field">
            <span>Must persist for (seconds)</span>
            <input type="number" min={0} max={86400} value={r.durationSec} onChange={(e) => set('durationSec', Number(e.target.value))} />
          </label>
        )}
        {r.ruleType === 'Absence' && (
          <label className="field">
            <span>Alert after no data for (seconds)</span>
            <input type="number" min={60} max={86400} value={r.durationSec} onChange={(e) => set('durationSec', Number(e.target.value))} />
            <span className="hint">Only sources that delivered data since the service started are watched.</span>
          </label>
        )}
        {r.ruleType === 'CountInWindow' && (
          <>
            <label className="field">
              <span>At least (events)</span>
              <input type="number" min={2} max={100000} value={r.countThreshold} onChange={(e) => set('countThreshold', Number(e.target.value))} />
            </label>
            <label className="field">
              <span>Within (seconds)</span>
              <input type="number" min={10} max={86400} value={r.windowSec} onChange={(e) => set('windowSec', Number(e.target.value))} />
              <span className="hint">Resolves when no matching event occurred for one window.</span>
            </label>
          </>
        )}
        <label className="field">
          <span>Escalate after (minutes, empty = global)</span>
          <input
            type="number"
            min={0}
            max={1440}
            value={r.escalateAfterMin ?? ''}
            onChange={(e) => set('escalateAfterMin', e.target.value === '' ? null : Number(e.target.value))}
          />
        </label>
        {(r.ruleType === 'EventMatch' || r.ruleType === 'CountInWindow' || (r.ruleType === 'Absence' && (r.key === 'syslog.message' || r.key === 'snmp.trap'))) && (
          <>
            <label className="field wide">
              <span>Message pattern (regular expression, optional)</span>
              <input className="mono" value={filter.pattern ?? ''} onChange={(e) => setFilter({ ...filter, pattern: e.target.value })} placeholder="authentication fail|%SYS-5-CONFIG" />
            </label>
            <label className="field">
              <span>Minimum event severity</span>
              <select value={filter.minSeverity ?? ''} onChange={(e) => setFilter({ ...filter, minSeverity: (e.target.value || null) as Severity | null })}>
                <option value="">any</option>
                {SEVERITIES.map((s) => (
                  <option key={s} value={s}>
                    {s} and above
                  </option>
                ))}
              </select>
              <span className="hint">Syslog 0–2 = Critical, 3 = Major, 4 = Warning; Windows Critical/Error/Warning.</span>
            </label>
            <label className="field">
              <span>Source contains (app, provider, trap name)</span>
              <input value={filter.source ?? ''} onChange={(e) => setFilter({ ...filter, source: e.target.value })} />
            </label>
            {(r.key === 'windows.event' || eventIds) && (
              <label className="field">
                <span>Windows event IDs</span>
                <input value={eventIds} onChange={(e) => setEventIds(e.target.value)} placeholder="7031, 7034" />
              </label>
            )}
            {(r.key === 'snmp.trap' || filter.oid) && (
              <label className="field">
                <span>Trap OID (prefix)</span>
                <input className="mono" value={filter.oid ?? ''} onChange={(e) => setFilter({ ...filter, oid: e.target.value })} placeholder="1.3.6.1.6.3.1.1.5.3" />
              </label>
            )}
          </>
        )}
        <label className="check" style={{ alignSelf: 'end' }}>
          <input type="checkbox" checked={r.isEnabled} onChange={(e) => set('isEnabled', e.target.checked)} /> Enabled
        </label>
        <label className="field wide">
          <span>Description</span>
          <input value={r.description} onChange={(e) => set('description', e.target.value)} />
        </label>

        <div className="field wide">
          <span>Scope (empty = all devices)</span>
          <div className="toolbar">
            {DEVICE_TYPES.map((t) => (
              <label key={t} className="check">
                <input
                  type="checkbox"
                  checked={r.scope.deviceTypes.includes(t)}
                  onChange={(e) => setScope('deviceTypes', e.target.checked ? [...r.scope.deviceTypes, t] : r.scope.deviceTypes.filter((x: DeviceType) => x !== t))}
                />
                {typeLabel(t)}
              </label>
            ))}
          </div>
        </div>
        <label className="field">
          <span>Groups</span>
          <select
            multiple
            size={5}
            value={r.scope.groupIds.map(String)}
            onChange={(e) => setScope('groupIds', Array.from(e.target.selectedOptions).map((o) => Number(o.value)))}
          >
            {groupOptions(groups).map((g) => (
              <option key={g.id} value={g.id}>
                {g.label}
              </option>
            ))}
          </select>
          <span className="hint">Ctrl+click to select several. Includes sub-groups.</span>
        </label>
        <label className="field">
          <span>Device tags</span>
          <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="core, erp" />
        </label>
        <label className="check" style={{ alignSelf: 'start', marginTop: 22 }}>
          <input type="checkbox" checked={r.scope.importantInterfacesOnly} onChange={(e) => setScope('importantInterfacesOnly', e.target.checked)} />
          Interface rules: important ports only
        </label>
      </div>
    </Modal>
  );
}

export function RulesTab() {
  const qc = useQueryClient();
  const rules = useQuery({ queryKey: ['rules'], queryFn: () => api.get<Rule[]>('/api/rules') });
  const keys = useQuery({ queryKey: ['rules', 'keys'], queryFn: () => api.get<RuleKey[]>('/api/rules/keys') });
  const groups = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });
  const [edit, setEdit] = useState<Rule | 'new' | null>(null);
  const [del, setDel] = useState<Rule | null>(null);
  const [error, setError] = useState<unknown>(null);

  const toggle = async (rule: Rule) => {
    try {
      await api.put(`/api/rules/${rule.id}`, { ...rule, isEnabled: !rule.isEnabled });
      await qc.invalidateQueries({ queryKey: ['rules'] });
    } catch (e) {
      setError(e);
    }
  };

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <span className="muted">Built-in rules can be tuned or disabled. Changes apply within seconds.</span>
        <span style={{ flex: 1 }} />
        <button className="primary" onClick={() => setEdit('new')} disabled={!keys.data}>
          <Plus size={16} /> New rule
        </button>
      </div>
      <ErrorBox error={error ?? rules.error} />
      <div className="card">
        <table className="data">
          <thead>
            <tr>
              <th>On</th>
              <th>Rule</th>
              <th>Condition</th>
              <th>Scope</th>
              <th>Severity</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(rules.data ?? []).map((r) => (
              <tr key={r.id}>
                <td>
                  <input type="checkbox" checked={r.isEnabled} onChange={() => void toggle(r)} aria-label="Enabled" />
                </td>
                <td>
                  <strong>{r.name}</strong> {r.isBuiltIn && <span className="tag">built-in</span>}
                  {r.description && (
                    <div className="muted" style={{ fontSize: 12.5 }}>
                      {r.description}
                    </div>
                  )}
                </td>
                <td>{describe(r, keys.data ?? [])}</td>
                <td className="muted">{scopeText(r.scope, groups.data ?? [])}</td>
                <td>
                  <SeverityBadge severity={r.severity} />
                </td>
                <td className="nowrap" style={{ textAlign: 'right' }}>
                  <button className="small" onClick={() => setEdit(r)}>
                    Edit
                  </button>{' '}
                  {!r.isBuiltIn && (
                    <button className="small danger" onClick={() => setDel(r)}>
                      Delete
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {edit && keys.data && <RuleDialog rule={edit === 'new' ? undefined : edit} keys={keys.data} groups={groups.data ?? []} onClose={() => setEdit(null)} />}
      {del && (
        <Confirm
          title={`Delete rule ${del.name}?`}
          danger
          confirmLabel="Delete"
          message="Open alerts raised by this rule are resolved. Alert history is kept."
          onClose={() => setDel(null)}
          onConfirm={() =>
            void (async () => {
              try {
                await api.del(`/api/rules/${del.id}`);
                await qc.invalidateQueries({ queryKey: ['rules'] });
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
