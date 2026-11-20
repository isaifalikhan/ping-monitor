import { useQuery, useQueryClient } from '@tanstack/react-query';
import { KeyRound, Plus } from 'lucide-react';
import { useState } from 'react';
import { api } from '../../api/client';
import type { Credential, CredentialKind, SnmpAuth, SnmpPriv } from '../../api/types';
import { Confirm, ErrorBox, Modal } from '../../components/Modal';
import { fmtDateTime } from '../../util/format';

const KINDS: { value: CredentialKind; label: string }[] = [
  { value: 'SnmpV2c', label: 'SNMP v2c' },
  { value: 'SnmpV3', label: 'SNMP v3' },
  { value: 'Windows', label: 'Windows / WinRM' },
  { value: 'Ssh', label: 'SSH (Linux)' },
  { value: 'VCenter', label: 'vCenter' },
];

interface FormState {
  name: string;
  kind: CredentialKind;
  community: string;
  username: string;
  password: string;
  domain: string;
  authProtocol: SnmpAuth;
  authPassword: string;
  privProtocol: SnmpPriv;
  privPassword: string;
  contextName: string;
  privateKey: string;
}

function CredentialDialog({ credential, onClose }: { credential?: Credential; onClose: () => void }) {
  const qc = useQueryClient();
  const [replaceSecret, setReplaceSecret] = useState(!credential);
  const [f, setF] = useState<FormState>({
    name: credential?.name ?? '',
    kind: credential?.kind ?? 'SnmpV3',
    community: '',
    username: '',
    password: '',
    domain: '',
    authProtocol: 'SHA256',
    authPassword: '',
    privProtocol: 'AES128',
    privPassword: '',
    contextName: '',
    privateKey: '',
  });
  const [error, setError] = useState<unknown>(null);
  const set = <K extends keyof FormState>(k: K, v: FormState[K]) => setF((s) => ({ ...s, [k]: v }));

  const save = async () => {
    try {
      const body = { ...f, replaceSecret };
      if (credential) await api.put(`/api/credentials/${credential.id}`, body);
      else await api.post('/api/credentials', body);
      await qc.invalidateQueries({ queryKey: ['credentials'] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };

  const secretFields = (
    <>
      {f.kind === 'SnmpV2c' && (
        <label className="field">
          <span>Community string</span>
          <input type="password" autoComplete="off" value={f.community} onChange={(e) => set('community', e.target.value)} />
        </label>
      )}
      {f.kind === 'SnmpV3' && (
        <>
          <label className="field">
            <span>User name</span>
            <input autoComplete="off" value={f.username} onChange={(e) => set('username', e.target.value)} />
          </label>
          <label className="field">
            <span>Context name (optional)</span>
            <input autoComplete="off" value={f.contextName} onChange={(e) => set('contextName', e.target.value)} />
          </label>
          <label className="field">
            <span>Authentication</span>
            <select value={f.authProtocol} onChange={(e) => set('authProtocol', e.target.value as SnmpAuth)}>
              {(['None', 'MD5', 'SHA1', 'SHA256', 'SHA384', 'SHA512'] as SnmpAuth[]).map((a) => (
                <option key={a}>{a}</option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Auth password</span>
            <input type="password" autoComplete="new-password" value={f.authPassword} onChange={(e) => set('authPassword', e.target.value)} disabled={f.authProtocol === 'None'} />
          </label>
          <label className="field">
            <span>Privacy (encryption)</span>
            <select value={f.privProtocol} onChange={(e) => set('privProtocol', e.target.value as SnmpPriv)}>
              {(['None', 'DES', 'AES128', 'AES192', 'AES256'] as SnmpPriv[]).map((a) => (
                <option key={a}>{a}</option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Privacy password</span>
            <input type="password" autoComplete="new-password" value={f.privPassword} onChange={(e) => set('privPassword', e.target.value)} disabled={f.privProtocol === 'None'} />
          </label>
        </>
      )}
      {(f.kind === 'Windows' || f.kind === 'Ssh' || f.kind === 'VCenter') && (
        <>
          <label className="field">
            <span>User name</span>
            <input autoComplete="off" value={f.username} onChange={(e) => set('username', e.target.value)} />
          </label>
          {f.kind === 'Windows' && (
            <label className="field">
              <span>Domain</span>
              <input autoComplete="off" value={f.domain} onChange={(e) => set('domain', e.target.value)} />
            </label>
          )}
          <label className="field">
            <span>Password</span>
            <input type="password" autoComplete="new-password" value={f.password} onChange={(e) => set('password', e.target.value)} />
          </label>
          {f.kind === 'Ssh' && (
            <label className="field wide">
              <span>Private key (optional, OpenSSH/PEM)</span>
              <textarea rows={4} className="mono" value={f.privateKey} onChange={(e) => set('privateKey', e.target.value)} />
            </label>
          )}
        </>
      )}
    </>
  );

  return (
    <Modal
      title={credential ? `Edit ${credential.name}` : 'Add credential profile'}
      onClose={onClose}
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
      <div className="muted">Secrets are encrypted with Windows DPAPI on this server and are never shown again after saving.</div>
      <div className="form-grid">
        <label className="field">
          <span>Profile name</span>
          <input value={f.name} onChange={(e) => set('name', e.target.value)} maxLength={128} />
        </label>
        <label className="field">
          <span>Type</span>
          <select value={f.kind} onChange={(e) => set('kind', e.target.value as CredentialKind)} disabled={!!credential && !replaceSecret}>
            {KINDS.map((k) => (
              <option key={k.value} value={k.value}>
                {k.label}
              </option>
            ))}
          </select>
        </label>
        {credential && (
          <label className="check wide">
            <input type="checkbox" checked={replaceSecret} onChange={(e) => setReplaceSecret(e.target.checked)} />
            Replace the stored secret (current: {credential.displayHint})
          </label>
        )}
        {replaceSecret && secretFields}
      </div>
    </Modal>
  );
}

function TestDialog({ credential, onClose }: { credential: Credential; onClose: () => void }) {
  const [ip, setIp] = useState('');
  const [result, setResult] = useState<{ success: boolean; error?: string; sysName?: string; sysDescription?: string; vendor?: string; model?: string } | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const test = async () => {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      setResult(await api.post(`/api/credentials/${credential.id}/test`, { ipAddress: ip }));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal
      title={`Test ${credential.name}`}
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Close</button>
          <button className="primary" disabled={!ip || busy} onClick={() => void test()}>
            {busy ? 'Testing…' : 'Test'}
          </button>
        </>
      }
    >
      <label className="field">
        <span>Device IP address</span>
        <input className="mono" value={ip} onChange={(e) => setIp(e.target.value)} autoFocus />
      </label>
      <ErrorBox error={error} />
      {result &&
        (result.success ? (
          <div className="ok-box">
            {credential.kind === 'Windows' || credential.kind === 'Ssh' ? 'Connected' : 'SNMP OK'} — {result.sysName} {result.vendor && `(${result.vendor} ${result.model ?? ''})`}
            <div className="muted" style={{ marginTop: 4 }}>
              {result.sysDescription}
            </div>
          </div>
        ) : (
          <div className="error-box">Failed: {result.error}</div>
        ))}
    </Modal>
  );
}

export function CredentialsTab() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['credentials'], queryFn: () => api.get<Credential[]>('/api/credentials') });
  const [edit, setEdit] = useState<Credential | 'new' | null>(null);
  const [test, setTest] = useState<Credential | null>(null);
  const [del, setDel] = useState<Credential | null>(null);
  const [error, setError] = useState<unknown>(null);

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <span className="muted">Use read-only accounts: SNMP read-only community / v3 user, WMI read + Event Log Readers (Windows), an unprivileged SSH user (Linux), vCenter read-only role.</span>
        <span style={{ flex: 1 }} />
        <button className="primary" onClick={() => setEdit('new')}>
          <Plus size={16} /> Add profile
        </button>
      </div>
      <ErrorBox error={error ?? q.error} />
      <div className="card">
        <table className="data">
          <thead>
            <tr>
              <th>Name</th>
              <th>Type</th>
              <th>Details</th>
              <th className="num">Used by</th>
              <th>Updated</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(q.data ?? []).map((c) => (
              <tr key={c.id}>
                <td>
                  <KeyRound size={14} className="muted" /> <strong>{c.name}</strong>
                </td>
                <td>{KINDS.find((k) => k.value === c.kind)?.label}</td>
                <td className="muted mono">{c.displayHint}</td>
                <td className="num">{c.usedBy}</td>
                <td className="muted">{fmtDateTime(c.updatedUtc)}</td>
                <td className="nowrap" style={{ textAlign: 'right' }}>
                  {c.kind !== 'VCenter' && (
                    <button className="small" onClick={() => setTest(c)}>
                      Test
                    </button>
                  )}{' '}
                  <button className="small" onClick={() => setEdit(c)}>
                    Edit
                  </button>{' '}
                  <button className="small danger" onClick={() => setDel(c)}>
                    Delete
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {q.data?.length === 0 && <div className="empty">No credential profiles yet.</div>}
      </div>
      {edit && <CredentialDialog credential={edit === 'new' ? undefined : edit} onClose={() => setEdit(null)} />}
      {test && <TestDialog credential={test} onClose={() => setTest(null)} />}
      {del && (
        <Confirm
          title={`Delete ${del.name}?`}
          danger
          confirmLabel="Delete"
          message="The encrypted secret is removed permanently. Profiles still assigned to devices cannot be deleted."
          onClose={() => setDel(null)}
          onConfirm={() =>
            void (async () => {
              try {
                await api.del(`/api/credentials/${del.id}`);
                await qc.invalidateQueries({ queryKey: ['credentials'] });
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
