import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { MonitoringSettings, RetentionSettings, SecuritySettings } from '../../api/types';
import { ErrorBox } from '../../components/Modal';

interface AllSettings {
  monitoring: MonitoringSettings;
  security: SecuritySettings;
  retention: RetentionSettings;
}

type Field<T> = { key: keyof T; label: string; hint?: string; min: number; max: number };

function Section<T extends object>({ title, section, fields, initial }: { title: string; section: string; fields: Field<T>[]; initial: T }) {
  const qc = useQueryClient();
  const [value, setValue] = useState<T>(initial);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  useEffect(() => setValue(initial), [initial]);

  const save = async () => {
    setError(null);
    setSaved(false);
    try {
      const result = await api.put<T>(`/api/settings/${section}`, value);
      setValue(result);
      setSaved(true);
      await qc.invalidateQueries({ queryKey: ['settings'] });
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="card">
      <div className="card-head">
        <h2>{title}</h2>
        <span className="spacer" />
        {saved && <span style={{ color: 'var(--ok)' }}>Saved</span>}
        <button className="primary small" onClick={() => void save()}>
          Save
        </button>
      </div>
      <div className="card-body">
        <ErrorBox error={error} />
        <div className="form-grid">
          {fields.map((f) => (
            <label key={String(f.key)} className="field">
              <span>{f.label}</span>
              <input
                type="number"
                min={f.min}
                max={f.max}
                value={Number(value[f.key])}
                onChange={(e) => {
                  setSaved(false);
                  setValue((v) => ({ ...v, [f.key]: Number(e.target.value) }));
                }}
              />
              <span className="hint">
                {f.hint ? `${f.hint} ` : ''}({f.min}–{f.max})
              </span>
            </label>
          ))}
        </div>
      </div>
    </div>
  );
}

export function GeneralTab() {
  const q = useQuery({ queryKey: ['settings'], queryFn: () => api.get<AllSettings>('/api/settings') });
  if (q.error) return <ErrorBox error={q.error} />;
  if (!q.data) return <div className="empty">Loading…</div>;
  return (
    <div className="grid" style={{ gap: 16 }}>
      <Section<MonitoringSettings>
        title="Monitoring"
        section="monitoring"
        initial={q.data.monitoring}
        fields={[
          { key: 'defaultPollIntervalSec', label: 'Default polling interval (s)', min: 15, max: 3600, hint: 'For new devices.' },
          { key: 'downAfterFailedPolls', label: 'Device down after N failed polls', min: 1, max: 20 },
          { key: 'pingCount', label: 'Ping echoes per poll', min: 1, max: 10 },
          { key: 'pingTimeoutMs', label: 'Ping timeout (ms)', min: 100, max: 5000 },
          { key: 'snmpTimeoutMs', label: 'SNMP timeout (ms)', min: 500, max: 15000 },
          { key: 'snmpRetries', label: 'SNMP retries', min: 0, max: 5 },
          { key: 'maxConcurrentPolls', label: 'Parallel polls', min: 1, max: 512, hint: 'Takes effect after a service restart.' },
          { key: 'interfaceMetadataRefreshMin', label: 'Interface table refresh (min)', min: 5, max: 1440 },
          { key: 'inventoryRefreshHours', label: 'Inventory refresh (hours)', min: 1, max: 168 },
        ]}
      />
      <Section<SecuritySettings>
        title="Console security"
        section="security"
        initial={q.data.security}
        fields={[
          { key: 'sessionIdleTimeoutMin', label: 'Idle session timeout (min)', min: 5, max: 1440 },
          { key: 'sessionAbsoluteTimeoutHours', label: 'Maximum session length (h)', min: 1, max: 168 },
          { key: 'maxFailedLogins', label: 'Lock account after N failed sign-ins', min: 3, max: 50 },
          { key: 'lockoutMinutes', label: 'Lockout duration (min)', min: 1, max: 1440 },
          { key: 'minPasswordLength', label: 'Minimum password length', min: 8, max: 128 },
        ]}
      />
      <Section<RetentionSettings>
        title="Data retention"
        section="retention"
        initial={q.data.retention}
        fields={[
          { key: 'rawDays', label: 'Raw samples (days)', min: 1, max: 60 },
          { key: 'fiveMinuteDays', label: '5-minute rollups (days)', min: 7, max: 730 },
          { key: 'hourlyDays', label: 'Hourly rollups (days)', min: 30, max: 3650 },
          { key: 'eventDays', label: 'Events (days)', min: 7, max: 3650 },
          { key: 'auditDays', label: 'Audit trail (days)', min: 30, max: 3650 },
          { key: 'closedAlertDays', label: 'Resolved/closed alerts (days)', min: 7, max: 3650 },
        ]}
      />
    </div>
  );
}
