import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../api/client';
import { ErrorBox } from '../../components/Modal';
import { fmtDateTime } from '../../util/format';

interface CertificateInfo {
  subject?: string | null;
  notAfterUtc?: string | null;
  thumbprint?: string | null;
  imported: boolean;
}

interface CertificateCheck {
  subject: string;
  issuer: string;
  thumbprint: string;
  notAfterUtc: string;
  names: string[];
  selfSigned: boolean;
  warnings: string[];
}

/** HTTPS certificate of the console: shows the one in use and imports a PFX issued by the internal CA. */
export function CertificatePanel() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['certificate'], queryFn: () => api.get<CertificateInfo>('/api/settings/certificate') });
  const [file, setFile] = useState<File | null>(null);
  const [password, setPassword] = useState('');
  const [restart, setRestart] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<CertificateCheck | null>(null);

  const upload = async () => {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const form = new FormData();
      form.append('file', file);
      form.append('password', password);
      form.append('restart', restart ? 'true' : 'false');
      setResult(await api.upload<CertificateCheck>('/api/settings/certificate', form));
      setPassword('');
      void qc.invalidateQueries({ queryKey: ['certificate'] });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const c = q.data;
  return (
    <div className="card card-body" style={{ marginBottom: 16 }}>
      <h2 style={{ marginTop: 0 }}>HTTPS certificate</h2>
      {c && (
        <dl className="kv">
          <dt>In use</dt>
          <dd>{c.subject ?? '—'}</dd>
          <dt>Valid until</dt>
          <dd>{c.notAfterUtc ? fmtDateTime(c.notAfterUtc) : '—'}</dd>
          <dt>Thumbprint</dt>
          <dd className="mono">{c.thumbprint ?? '—'}</dd>
          <dt>Imported certificate</dt>
          <dd>{c.imported ? 'yes (used unless appsettings.json names another one)' : 'no'}</dd>
        </dl>
      )}
      <p className="muted">
        Import a .pfx issued by your internal CA (with its private key, for the server name operators use). It is checked here, stored encrypted for this server and used from the next start.
      </p>
      <ErrorBox error={q.error ?? error} />
      <div className="form-grid">
        <label className="field">
          <span>.pfx file</span>
          <input type="file" accept=".pfx,.p12" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        </label>
        <label className="field">
          <span>PFX password</span>
          <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
        <label className="check">
          <input type="checkbox" checked={restart} onChange={(e) => setRestart(e.target.checked)} /> Restart NetWatch now to use it
        </label>
      </div>
      <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
        <button className="primary" disabled={!file || busy} onClick={() => void upload()}>
          {busy ? 'Checking…' : 'Import certificate'}
        </button>
        {c?.imported && (
          <button
            className="danger"
            onClick={() =>
              window.confirm('Remove the imported certificate? NetWatch uses its self-signed certificate after the next start.') &&
              void api.del('/api/settings/certificate').then(() => qc.invalidateQueries({ queryKey: ['certificate'] }))
            }
          >
            Remove imported certificate
          </button>
        )}
      </div>
      {result && (
        <div className="card card-body" style={{ marginTop: 10, borderColor: result.warnings.length ? 'var(--warn)' : 'var(--ok)' }}>
          <strong>Imported: {result.subject}</strong>
          <div className="muted">
            Issued by {result.issuer} · valid until {fmtDateTime(result.notAfterUtc)} · names: {result.names.join(', ') || '—'}
          </div>
          {result.warnings.map((w) => (
            <div key={w} style={{ color: 'var(--warn)' }}>
              {w}
            </div>
          ))}
          <div className="faint">{restart ? 'NetWatch restarts now; reload the page in a minute.' : 'Restart the NetWatch service to use it.'}</div>
        </div>
      )}
    </div>
  );
}
