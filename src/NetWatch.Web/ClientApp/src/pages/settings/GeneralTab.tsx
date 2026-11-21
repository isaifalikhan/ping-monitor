import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { AlertingSettings, MonitoringSettings, ReceiverSettings, RetentionSettings, SecuritySettings, ServerMonitoringSettings, VmwareSettings } from '../../api/types';
import { ErrorBox } from '../../components/Modal';

interface AllSettings {
  monitoring: MonitoringSettings;
  security: SecuritySettings;
  retention: RetentionSettings;
  alerting: AlertingSettings;
  receivers: ReceiverSettings;
  servers: ServerMonitoringSettings;
  vmware: VmwareSettings;
}

type Field<T> =
  | { key: keyof T; label: string; hint?: string; min: number; max: number; kind?: 'number' }
  | { key: keyof T; label: string; hint?: string; kind: 'bool' };

function Section<T extends object>({ title, section, fields, initial, note }: { title: string; section: string; fields: Field<T>[]; initial: T; note?: string }) {
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

  const change = (key: keyof T, v: unknown) => {
    setSaved(false);
    setValue((x) => ({ ...x, [key]: v }));
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
        {note && (
          <div className="muted" style={{ marginBottom: 8 }}>
            {note}
          </div>
        )}
        <div className="form-grid">
          {fields.map((f) =>
            f.kind === 'bool' ? (
              <label key={String(f.key)} className="check" title={f.hint}>
                <input type="checkbox" checked={Boolean(value[f.key])} onChange={(e) => change(f.key, e.target.checked)} />
                {f.label}
              </label>
            ) : (
              <label key={String(f.key)} className="field">
                <span>{f.label}</span>
                <input type="number" min={f.min} max={f.max} value={Number(value[f.key])} onChange={(e) => change(f.key, Number(e.target.value))} />
                <span className="hint">
                  {f.hint ? `${f.hint} ` : ''}({f.min}–{f.max})
                </span>
              </label>
            ),
          )}
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
      <Section<AlertingSettings>
        title="Alert lifecycle"
        section="alerting"
        initial={q.data.alerting}
        note="Flapping: an alert that is raised and resolved too often stays open (marked flapping) until its condition is stable. Escalation: unacknowledged alerts are flagged, notified again and repeat the alarm sound."
        fields={[
          { key: 'flapThreshold', label: 'Flapping after N state changes', min: 3, max: 50 },
          { key: 'flapWindowMin', label: '…within (min)', min: 5, max: 1440 },
          { key: 'flapStableMin', label: 'Resolve flapping alerts after stable for (min)', min: 1, max: 1440 },
          { key: 'escalateCriticalAfterMin', label: 'Escalate Critical after (min, 0 = never)', min: 0, max: 1440 },
          { key: 'escalateMajorAfterMin', label: 'Escalate Major after (min, 0 = never)', min: 0, max: 1440 },
          { key: 'repeatSoundEveryMin', label: 'Repeat alarm sound for escalated alerts every (min)', min: 0, max: 120 },
          { key: 'dependencySuppression', label: 'Suppress alerts of devices behind an unreachable upstream device', kind: 'bool' },
        ]}
      />
      <Section<ServerMonitoringSettings>
        title="Server monitoring (WinRM/WMI, event logs, SSH)"
        section="servers"
        initial={q.data.servers}
        fields={[
          { key: 'winRmPort', label: 'WinRM port', min: 1, max: 65535, hint: '5985 (HTTP, Kerberos-encrypted) or 5986 (HTTPS).' },
          { key: 'sshPort', label: 'SSH port', min: 1, max: 65535 },
          { key: 'timeoutSec', label: 'Collection timeout (s)', min: 5, max: 120 },
          { key: 'maxEventsPerPoll', label: 'Max. event-log entries per log and poll', min: 10, max: 5000 },
          { key: 'winRmHttps', label: 'Use WinRM over HTTPS', kind: 'bool' },
          { key: 'dcomFallback', label: 'Fall back to WMI over DCOM when WinRM is unreachable', kind: 'bool' },
          { key: 'readEventLogs', label: 'Read System/Application event logs (Critical, Error)', kind: 'bool' },
          { key: 'includeWarnings', label: 'Also store Warning-level entries', kind: 'bool' },
        ]}
      />
      <Section<VmwareSettings>
        title="VMware (vCenter / ESXi)"
        section="vmware"
        initial={q.data.vmware}
        note="Devices of type vCenter (or Hypervisor for standalone ESXi) with a vCenter credential are polled through the vSphere API. Use an account with the vCenter Read-only role."
        fields={[
          { key: 'timeoutSec', label: 'API timeout (s)', min: 10, max: 300 },
          { key: 'maxEventsPerPoll', label: 'Max. vCenter events per poll', min: 50, max: 5000 },
          { key: 'importEvents', label: 'Import vCenter events', kind: 'bool' },
          { key: 'importAlarms', label: 'Import triggered vCenter alarms', kind: 'bool' },
          { key: 'collectCpuReady', label: 'Collect VM CPU ready (performance counter)', kind: 'bool' },
          { key: 'trustOnFirstUse', label: 'Pin self-signed vCenter certificates on first connection', kind: 'bool', hint: 'Off: only CA-trusted certificates are accepted.' },
        ]}
      />
      <Section<ReceiverSettings>
        title="Syslog and SNMP trap receivers"
        section="receivers"
        initial={q.data.receivers}
        note="Port and enable changes take effect after a service restart. Messages from addresses not in the inventory are stored without a device (no alerts)."
        fields={[
          { key: 'syslogUdpPort', label: 'Syslog UDP port', min: 1, max: 65535 },
          { key: 'syslogTcpPort', label: 'Syslog TCP port', min: 1, max: 65535 },
          { key: 'trapPort', label: 'SNMP trap port (UDP)', min: 1, max: 65535 },
          { key: 'maxMessagesPerSecond', label: 'Maximum stored messages per second', min: 50, max: 50000, hint: 'Flood protection; the rest is counted and dropped.' },
          { key: 'syslogEnabled', label: 'Receive syslog (UDP)', kind: 'bool' },
          { key: 'syslogTcpEnabled', label: 'Receive syslog over TCP', kind: 'bool' },
          { key: 'trapsEnabled', label: 'Receive SNMP traps', kind: 'bool' },
          { key: 'acceptUnknownSources', label: 'Store messages from unknown sources', kind: 'bool' },
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
          { key: 'eventDays', label: 'Events, syslog, traps, Windows events (days)', min: 7, max: 3650 },
          { key: 'auditDays', label: 'Audit trail (days)', min: 30, max: 3650 },
          { key: 'closedAlertDays', label: 'Resolved/closed alerts (days)', min: 7, max: 3650 },
        ]}
      />
    </div>
  );
}
