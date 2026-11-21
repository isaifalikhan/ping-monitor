import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { AlertingSettings, MonitoringSettings, ReceiverSettings, RetentionSettings, SecuritySettings, ServerMonitoringSettings, VmwareSettings, DcSettings } from '../../api/types';
import { ErrorBox } from '../../components/Modal';

interface AllSettings {
  monitoring: MonitoringSettings;
  security: SecuritySettings;
  retention: RetentionSettings;
  alerting: AlertingSettings;
  receivers: ReceiverSettings;
  servers: ServerMonitoringSettings;
  vmware: VmwareSettings;
  dc: DcSettings;
}

type Field<T> =
  | { key: keyof T; label: string; hint?: string; min: number; max: number; kind?: 'number' }
  | { key: keyof T; label: string; hint?: string; kind: 'bool' }
  | { key: keyof T; label: string; hint?: string; kind: 'text'; pattern?: string }
  | { key: keyof T; label: string; hint?: string; kind: 'list'; numeric?: boolean };

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
            ) : f.kind === 'text' ? (
              <label key={String(f.key)} className="field">
                <span>{f.label}</span>
                <input value={String(value[f.key] ?? '')} pattern={f.pattern} onChange={(e) => change(f.key, e.target.value)} />
                {f.hint && <span className="hint">{f.hint}</span>}
              </label>
            ) : f.kind === 'list' ? (
              <ListField key={String(f.key)} label={f.label} hint={f.hint} numeric={f.numeric} value={(value[f.key] as unknown as (string | number)[]) ?? []} onChange={(v) => change(f.key, v)} />
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

/** Comma-separated list editor; keeps the text while typing and parses it on every change. */
function ListField({ label, hint, numeric, value, onChange }: { label: string; hint?: string; numeric?: boolean; value: (string | number)[]; onChange: (v: (string | number)[]) => void }) {
  const parse = (t: string) => {
    const items = t.split(',').map((s) => s.trim()).filter(Boolean);
    return numeric ? items.map(Number).filter((n) => Number.isFinite(n)) : items;
  };
  const [text, setText] = useState(value.join(', '));
  // Only replace the text when the value changed from outside (load, save), not while typing.
  useEffect(() => setText((t) => (parse(t).join(',') === value.join(',') ? t : value.join(', '))), [value]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <label className="field">
      <span>{label}</span>
      <input
        value={text}
        onChange={(e) => {
          setText(e.target.value);
          onChange(parse(e.target.value));
        }}
      />
      {hint && <span className="hint">{hint}</span>}
    </label>
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
      <Section<DcSettings>
        title="Domain controllers (Security log, replication)"
        section="dc"
        initial={q.data.dc}
        note="Devices of type Domain controller with a Windows credential are read with that account (member of Event Log Readers). Successful logons (4624) are stored for investigation but never alerted on. Working hours are Asia/Karachi time."
        fields={[
          { key: 'maxSecurityEventsPerPoll', label: 'Max. Security events per DC and poll', min: 100, max: 50000 },
          { key: 'replicationCheckMin', label: 'Check replication and clock every (min)', min: 1, max: 1440 },
          { key: 'replicationStaleHours', label: 'Alert when a partner has not replicated for (h)', min: 1, max: 336 },
          { key: 'checkServicePorts', label: 'Check LDAP 389, Kerberos 88 and DNS 53 on every poll', kind: 'bool' },
          { key: 'workingDays', label: 'Working days', kind: 'text', hint: 'Comma-separated: Mon,Tue,Wed,Thu,Fri' },
          { key: 'workingHoursStart', label: 'Working hours start', kind: 'text', pattern: '[0-2][0-9]:[0-5][0-9]', hint: 'HH:mm' },
          { key: 'workingHoursEnd', label: 'Working hours end', kind: 'text', pattern: '[0-2][0-9]:[0-5][0-9]', hint: 'HH:mm' },
          { key: 'successfulLogonTypes', label: 'Logon types of 4624 to store', kind: 'list', numeric: true, hint: '2 interactive, 3 network, 7 unlock, 10 RDP, 11 cached; empty = all' },
          { key: 'privilegedGroups', label: 'Privileged groups (new members = CRITICAL)', kind: 'list' },
          { key: 'readSecurityLog', label: 'Read the Security log of each DC remotely', kind: 'bool' },
          { key: 'useForwardedEvents', label: 'Use Windows Event Forwarding (ForwardedEvents log on this server) for all DCs', kind: 'bool' },
          { key: 'forwardedDcs', label: 'DCs that use Event Forwarding (when not all)', kind: 'list', hint: 'DC name or IP; the other DCs are read remotely' },
          { key: 'successfulLogonIgnoreSources', label: 'Do not store successful logons from', kind: 'list', hint: 'IP, CIDR (10.0.20.0/24) or workstation name, e.g. application servers' },
          { key: 'storeSuccessfulLogons', label: 'Store successful logons (4624, 4768, 4776)', kind: 'bool' },
          { key: 'excludeComputerAccounts', label: 'Ignore computer accounts (name$) in logon events', kind: 'bool' },
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
