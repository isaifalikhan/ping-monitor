import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Trash2 } from 'lucide-react';
import { useEffect, useState } from 'react';
import { api } from '../../api/client';
import type { LdapBindMethod, LdapSecurity, LdapSettings, Role } from '../../api/types';
import { ErrorBox } from '../../components/Modal';

interface TestResult {
  success: boolean;
  error?: string | null;
  role?: Role | null;
  account?: string | null;
  displayName?: string | null;
  matchedGroups: string[];
}

/** Sign-in with Active Directory accounts (LDAP/LDAPS) and AD group → role mapping. */
export function DirectoryTab() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['settings'], queryFn: () => api.get<{ ldap: LdapSettings }>('/api/settings') });
  const [s, setS] = useState<LdapSettings | null>(null);
  const [servers, setServers] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [testUser, setTestUser] = useState('');
  const [testPassword, setTestPassword] = useState('');
  const [test, setTest] = useState<TestResult | null>(null);
  const [testing, setTesting] = useState(false);

  useEffect(() => {
    if (q.data) {
      setS(q.data.ldap);
      setServers(q.data.ldap.servers.join(', '));
    }
  }, [q.data]);

  if (q.error) return <ErrorBox error={q.error} />;
  if (!s) return <div className="empty">Loading…</div>;

  const set = <K extends keyof LdapSettings>(k: K, v: LdapSettings[K]) => {
    setSaved(false);
    setS((x) => (x ? { ...x, [k]: v } : x));
  };
  const body = (): LdapSettings => ({ ...s, servers: servers.split(/[,;\s]+/).map((x) => x.trim()).filter(Boolean) });

  const save = async () => {
    setError(null);
    try {
      await api.put('/api/settings/ldap', body());
      setSaved(true);
      await qc.invalidateQueries({ queryKey: ['settings'] });
    } catch (e) {
      setError(e);
    }
  };

  const runTest = async () => {
    setTesting(true);
    setTest(null);
    try {
      setTest(await api.post<TestResult>('/api/settings/ldap/test', { settings: body(), username: testUser, password: testPassword }));
    } catch (e) {
      setTest({ success: false, error: e instanceof Error ? e.message : String(e), matchedGroups: [] });
    } finally {
      setTesting(false);
      setTestPassword('');
    }
  };

  const setSecurity = (security: LdapSecurity) => {
    set('security', security);
    set('port', security === 'Ldaps' ? 636 : 389);
  };

  return (
    <div className="grid" style={{ gap: 16 }}>
      <div className="card">
        <div className="card-head">
          <h2>Active Directory sign-in</h2>
          <span className="spacer" />
          {saved && <span style={{ color: 'var(--ok)' }}>Saved</span>}
          <button className="primary small" onClick={() => void save()}>
            Save
          </button>
        </div>
        <div className="card-body">
          <ErrorBox error={error} />
          <div className="muted" style={{ marginBottom: 10 }}>
            Users sign in with their AD account (user, DOMAIN\user or user@domain). HIT SIEM binds as the user, so no directory service account or stored password is needed. The role is
            taken from the mapped AD groups at every sign-in (nested groups count); accounts in no mapped group are refused. Local accounts such as “admin” keep working.
          </div>
          <div className="form-grid">
            <label className="check wide">
              <input type="checkbox" checked={s.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Allow sign-in with Active Directory accounts
            </label>
            <label className="field">
              <span>Domain controllers</span>
              <input value={servers} onChange={(e) => setServers(e.target.value)} placeholder="dc01.corp.local, dc02.corp.local" />
              <span className="hint">Tried in order. Use names that match the DC certificates for LDAPS.</span>
            </label>
            <label className="field">
              <span>DNS domain</span>
              <input value={s.domain ?? ''} onChange={(e) => set('domain', e.target.value)} placeholder="corp.local" />
            </label>
            <label className="field">
              <span>Search base</span>
              <input value={s.baseDn ?? ''} onChange={(e) => set('baseDn', e.target.value)} placeholder="DC=corp,DC=local" />
            </label>
            <label className="field">
              <span>Connection security</span>
              <select value={s.security} onChange={(e) => setSecurity(e.target.value as LdapSecurity)}>
                <option value="Ldaps">LDAPS (TLS, port 636) — recommended</option>
                <option value="StartTls">StartTLS (port 389)</option>
                <option value="None">None — only with Negotiate (Kerberos signing/sealing)</option>
              </select>
            </label>
            <label className="field">
              <span>Port</span>
              <input type="number" min={1} max={65535} value={s.port} onChange={(e) => set('port', Number(e.target.value))} />
            </label>
            <label className="field">
              <span>Bind method</span>
              <select value={s.bindMethod} onChange={(e) => set('bindMethod', e.target.value as LdapBindMethod)}>
                <option value="Negotiate">Negotiate (Kerberos/NTLM)</option>
                <option value="Simple">Simple bind (user@domain, TLS only)</option>
              </select>
            </label>
            <label className="field">
              <span>Timeout (seconds)</span>
              <input type="number" min={2} max={60} value={s.timeoutSec} onChange={(e) => set('timeoutSec', Number(e.target.value))} />
            </label>
            <label className="check">
              <input type="checkbox" checked={s.validateCertificate} onChange={(e) => set('validateCertificate', e.target.checked)} /> Validate the DC certificate
              {!s.validateCertificate && <span style={{ color: 'var(--warn)' }}> — not secure, for testing only</span>}
            </label>
          </div>

          <h3 style={{ marginTop: 16 }}>Group → role mapping</h3>
          <table className="data">
            <thead>
              <tr>
                <th>AD group (distinguished name)</th>
                <th style={{ width: 160 }}>Role</th>
                <th style={{ width: 50 }} />
              </tr>
            </thead>
            <tbody>
              {s.roleMappings.map((m, i) => (
                <tr key={i}>
                  <td>
                    <input
                      className="mono"
                      style={{ width: '100%' }}
                      value={m.groupDn}
                      placeholder="CN=HIT SIEM Admins,OU=Groups,DC=corp,DC=local"
                      onChange={(e) => set('roleMappings', s.roleMappings.map((x, j) => (j === i ? { ...x, groupDn: e.target.value } : x)))}
                    />
                  </td>
                  <td>
                    <select value={m.role} onChange={(e) => set('roleMappings', s.roleMappings.map((x, j) => (j === i ? { ...x, role: e.target.value as Role } : x)))}>
                      <option value="Admin">Admin</option>
                      <option value="Operator">Operator</option>
                      <option value="Viewer">Viewer</option>
                    </select>
                  </td>
                  <td>
                    <button className="ghost icon" aria-label="Remove mapping" onClick={() => set('roleMappings', s.roleMappings.filter((_, j) => j !== i))}>
                      <Trash2 size={15} />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <button className="small" style={{ marginTop: 8 }} onClick={() => set('roleMappings', [...s.roleMappings, { groupDn: '', role: 'Viewer' }])}>
            <Plus size={14} /> Add mapping
          </button>
          <div className="hint" style={{ marginTop: 6 }}>
            When a user is in several mapped groups, the highest role wins.
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-head">
          <h2>Test</h2>
        </div>
        <div className="card-body">
          <div className="muted" style={{ marginBottom: 8 }}>
            Tests the settings above (saved or not) with a real account. The password is used once and not stored.
          </div>
          <div className="toolbar">
            <input autoComplete="off" placeholder="user name" value={testUser} onChange={(e) => setTestUser(e.target.value)} />
            <input type="password" autoComplete="new-password" placeholder="password" value={testPassword} onChange={(e) => setTestPassword(e.target.value)} />
            <button disabled={testing || !testUser || !testPassword} onClick={() => void runTest()}>
              {testing ? 'Testing…' : 'Test sign-in'}
            </button>
          </div>
          {test &&
            (test.success && test.role ? (
              <div className="ok-box" style={{ marginTop: 10 }}>
                Signed in as {test.displayName ?? test.account} — role <strong>{test.role}</strong> via {test.matchedGroups.join('; ')}
              </div>
            ) : (
              <div className="error-box" style={{ marginTop: 10 }}>
                {test.success ? `Credentials are valid, but: ${test.error}` : `Failed: ${test.error}`}
              </div>
            ))}
        </div>
      </div>
    </div>
  );
}
