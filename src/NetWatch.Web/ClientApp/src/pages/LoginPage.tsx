import { Network } from 'lucide-react';
import { useEffect, useState, type FormEvent } from 'react';
import { useAuth } from '../auth/AuthContext';

export function LoginPage() {
  const { login } = useAuth();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [domain, setDomain] = useState<string | null>(null);

  useEffect(() => {
    fetch('/api/auth/options', { credentials: 'same-origin' })
      .then((r) => (r.ok ? r.json() : null))
      .then((o: { ldapEnabled: boolean; domain?: string | null } | null) => setDomain(o?.ldapEnabled ? (o.domain ?? '') : null))
      .catch(() => setDomain(null));
  }, []);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(username, password);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign-in failed');
      setPassword('');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="login-wrap">
      <form className="login" onSubmit={submit}>
        <div className="brand">
          <Network size={26} />
          <span>NetWatch</span>
        </div>
        <div className="muted">Infrastructure monitoring — sign in to continue.</div>
        {error && <div className="error-box">{error}</div>}
        <label className="field">
          <span>User name</span>
          <input autoFocus autoComplete="username" value={username} onChange={(e) => setUsername(e.target.value)} required />
          {domain !== null && <span className="hint">Local account or your {domain ? `${domain} ` : ''}domain account.</span>}
        </label>
        <label className="field">
          <span>Password</span>
          <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
        </label>
        <button className="primary" type="submit" disabled={busy} style={{ justifyContent: 'center' }}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </div>
  );
}
