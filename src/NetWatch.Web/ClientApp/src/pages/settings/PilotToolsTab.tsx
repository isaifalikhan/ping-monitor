import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download } from 'lucide-react';
import { useMemo, useState } from 'react';
import { api } from '../../api/client';
import type { Credential, CredentialKind, DeviceListItem } from '../../api/types';
import { ErrorBox } from '../../components/Modal';
import { fmtDateTime } from '../../util/format';

type TestKind = 'Ping' | 'Snmp' | 'WinRm' | 'EventLog' | 'Ssh' | 'VCenter' | 'Redfish' | 'Ldap' | 'Smtp' | 'Receivers';

interface TestResult {
  id: string;
  kind: TestKind;
  target: string;
  ok: boolean;
  durationMs: number;
  summary: string;
  steps: { name: string; ok: boolean; detail: string }[];
  error?: string | null;
  timestampUtc: string;
  requestedBy?: string | null;
}

const TESTS: { kind: TestKind; label: string; credentials?: CredentialKind[]; needsTarget: boolean }[] = [
  { kind: 'Ping', label: 'Ping', needsTarget: true },
  { kind: 'Snmp', label: 'SNMP v2c/v3 (sysDescr + IF-MIB)', credentials: ['SnmpV2c', 'SnmpV3'], needsTarget: true },
  { kind: 'WinRm', label: 'WinRM / WMI query', credentials: ['Windows'], needsTarget: true },
  { kind: 'EventLog', label: 'Remote Event Log (1 Security event)', credentials: ['Windows'], needsTarget: true },
  { kind: 'Ssh', label: 'SSH command', credentials: ['Ssh'], needsTarget: true },
  { kind: 'VCenter', label: 'vCenter login and inventory', credentials: ['VCenter'], needsTarget: true },
  { kind: 'Redfish', label: 'iLO / iDRAC Redfish', credentials: ['Redfish'], needsTarget: true },
  { kind: 'Ldap', label: 'LDAP / LDAPS bind', needsTarget: false },
  { kind: 'Smtp', label: 'SMTP test e-mail', needsTarget: false },
  { kind: 'Receivers', label: 'Syslog / traps received', needsTarget: true },
];

function ResultCard({ r }: { r: TestResult }) {
  return (
    <div className="card card-body" style={{ borderLeft: `4px solid ${r.ok ? 'var(--ok)' : 'var(--crit)'}`, marginTop: 10 }}>
      <div style={{ display: 'flex', gap: 8, alignItems: 'baseline' }}>
        <span className={`badge ${r.ok ? 's-up' : 's-down'}`}>{r.ok ? 'PASS' : 'FAIL'}</span>
        <strong>{TESTS.find((t) => t.kind === r.kind)?.label}</strong>
        <span className="mono muted">{r.target}</span>
        <span className="spacer" />
        <span className="faint">
          {Math.round(r.durationMs)} ms · {fmtDateTime(r.timestampUtc)}
        </span>
      </div>
      {r.error && <div style={{ color: 'var(--crit)', marginTop: 6 }}>{r.error}</div>}
      <table className="data" style={{ marginTop: 6 }}>
        <tbody>
          {r.steps.map((s, i) => (
            <tr key={i}>
              <td style={{ width: 24 }}>{s.ok ? '✔' : '✘'}</td>
              <td className="nowrap">{s.name}</td>
              <td className={s.ok ? 'muted' : undefined} style={{ color: s.ok ? undefined : 'var(--crit)' }}>
                {s.detail}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Pilot tools: connection tests with exact errors, and the diagnostic bundle to bring out of the air gap. */
export function PilotToolsTab() {
  const qc = useQueryClient();
  const devices = useQuery({ queryKey: ['devices', 'all'], queryFn: () => api.get<DeviceListItem[]>('/api/devices') });
  const creds = useQuery({ queryKey: ['credentials'], queryFn: () => api.get<Credential[]>('/api/credentials') });
  const history = useQuery({ queryKey: ['diagnostics', 'tests'], queryFn: () => api.get<TestResult[]>('/api/diagnostics/tests') });
  const [kind, setKind] = useState<TestKind>('Snmp');
  const [deviceId, setDeviceId] = useState('');
  const [address, setAddress] = useState('');
  const [credentialId, setCredentialId] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [minutes, setMinutes] = useState(15);
  const [walk, setWalk] = useState<Set<number>>(new Set());
  const spec = TESTS.find((t) => t.kind === kind)!;
  const usable = useMemo(() => (creds.data ?? []).filter((c) => spec.credentials?.includes(c.kind)), [creds.data, spec]);

  const run = useMutation({
    mutationFn: () =>
      api.post<TestResult>('/api/diagnostics/test', {
        kind,
        deviceId: deviceId ? Number(deviceId) : null,
        address: address.trim() || null,
        credentialId: credentialId ? Number(credentialId) : null,
        username: kind === 'Ldap' ? username : null,
        password: kind === 'Ldap' ? password : null,
        minutes,
      }),
    onSettled: () => void qc.invalidateQueries({ queryKey: ['diagnostics', 'tests'] }),
  });

  const snmpDevices = (devices.data ?? []).filter((d) => ['Switch', 'Router', 'Firewall', 'Server', 'Hypervisor', 'Other'].includes(d.type));

  return (
    <div className="grid cols-2" style={{ alignItems: 'start' }}>
      <div className="card card-body">
        <h2 style={{ marginTop: 0 }}>Connection test</h2>
        <p className="muted">Runs one check against one device with one credential and shows every step with the exact error. Nothing is changed or stored (certificates are not pinned by a test).</p>
        <ErrorBox error={run.error} />
        <div className="form-grid">
          <label className="field">
            <span>Test</span>
            <select value={kind} onChange={(e) => { setKind(e.target.value as TestKind); setCredentialId(''); }}>
              {TESTS.map((t) => (
                <option key={t.kind} value={t.kind}>
                  {t.label}
                </option>
              ))}
            </select>
          </label>
          {spec.needsTarget && (
            <>
              <label className="field">
                <span>Device</span>
                <select value={deviceId} onChange={(e) => setDeviceId(e.target.value)}>
                  <option value="">— enter an address —</option>
                  {(devices.data ?? []).map((d) => (
                    <option key={d.id} value={d.id}>
                      {d.name} ({d.ipAddress})
                    </option>
                  ))}
                </select>
              </label>
              <label className="field">
                <span>{kind === 'Redfish' ? 'iLO / iDRAC address (empty = from the device)' : 'Address (empty = the device’s)'}</span>
                <input className="mono" value={address} onChange={(e) => setAddress(e.target.value)} placeholder="10.0.0.1" />
              </label>
            </>
          )}
          {spec.credentials && (
            <label className="field">
              <span>Credential</span>
              <select value={credentialId} onChange={(e) => setCredentialId(e.target.value)}>
                <option value="">— choose —</option>
                {usable.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name} ({c.displayHint})
                  </option>
                ))}
              </select>
            </label>
          )}
          {kind === 'Ldap' && (
            <>
              <label className="field">
                <span>Directory user</span>
                <input autoComplete="off" value={username} onChange={(e) => setUsername(e.target.value)} placeholder="CORP\\jdoe" />
              </label>
              <label className="field">
                <span>Password (not stored)</span>
                <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
              </label>
            </>
          )}
          {kind === 'Receivers' && (
            <label className="field">
              <span>Look back (minutes)</span>
              <input type="number" min={1} max={1440} value={minutes} onChange={(e) => setMinutes(Number(e.target.value))} />
            </label>
          )}
        </div>
        <button className="primary" style={{ marginTop: 10 }} disabled={run.isPending} onClick={() => run.mutate()}>
          {run.isPending ? 'Testing…' : 'Run test'}
        </button>
        {run.data && <ResultCard r={run.data} />}
      </div>

      <div className="card card-body">
        <h2 style={{ marginTop: 0 }}>Diagnostic bundle</h2>
        <p className="muted">
          One zip to bring out on USB: logs of the last 24 h, connection-test results, SNMP walks of the devices ticked below (system, ENTITY-MIB, sensors, vendor health tables, LLDP),
          poll statistics, the inventory and the configuration. Every password, community and key is removed; credential secrets are never included.
        </p>
        <div className="table-wrap" style={{ maxHeight: 260 }}>
          <table className="data">
            <tbody>
              {snmpDevices.map((d) => (
                <tr key={d.id}>
                  <td style={{ width: 24 }}>
                    <input
                      type="checkbox"
                      aria-label={`Walk ${d.name}`}
                      checked={walk.has(d.id)}
                      disabled={!walk.has(d.id) && walk.size >= 20}
                      onChange={(e) => {
                        const next = new Set(walk);
                        if (e.target.checked) next.add(d.id);
                        else next.delete(d.id);
                        setWalk(next);
                      }}
                    />
                  </td>
                  <td>{d.name}</td>
                  <td className="mono muted">{d.ipAddress}</td>
                  <td className="muted">{[d.vendor, d.model].filter(Boolean).join(' ')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <a className="btn primary" style={{ marginTop: 10 }} href={`/api/diagnostics/bundle?walk=${[...walk].join(',')}`} download>
          <Download size={15} /> Download bundle{walk.size ? ` (${walk.size} SNMP walk${walk.size > 1 ? 's' : ''})` : ''}
        </a>
        <p className="faint">Up to 20 devices can be walked; each walk takes a few seconds per device.</p>

        <h3 style={{ marginTop: 16 }}>Recent tests</h3>
        {(history.data ?? []).slice(0, 10).map((r) => (
          <div key={r.id} style={{ display: 'flex', gap: 8, fontSize: 13, padding: '2px 0' }} title={r.error ?? r.summary}>
            <span className={`badge ${r.ok ? 's-up' : 's-down'}`}>{r.ok ? 'pass' : 'fail'}</span>
            <span>{TESTS.find((t) => t.kind === r.kind)?.label}</span>
            <span className="mono muted">{r.target}</span>
            <span className="spacer" />
            <span className="faint">{fmtDateTime(r.timestampUtc)}</span>
          </div>
        ))}
        {history.data?.length === 0 && <div className="faint">No tests run since NetWatch started.</div>}
      </div>
    </div>
  );
}
