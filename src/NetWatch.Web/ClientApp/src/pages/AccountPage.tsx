import { useState, type FormEvent } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/AuthContext';

/** Change own password. Also shown full-screen when the account must change its password (first login). */
export function AccountPage({ forced }: { forced?: boolean }) {
  const { user, refresh, logout } = useAuth();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    if (next !== confirm) {
      setError('The new passwords do not match.');
      return;
    }
    setBusy(true);
    try {
      await api.post('/api/auth/change-password', { currentPassword: current, newPassword: next });
      setDone(true);
      setCurrent('');
      setNext('');
      setConfirm('');
      await refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed');
    } finally {
      setBusy(false);
    }
  };

  const form = (
    <form className={forced ? 'login' : 'card card-body'} onSubmit={submit} style={forced ? undefined : { maxWidth: 460, display: 'flex', flexDirection: 'column', gap: 14 }}>
      <h2>{forced ? 'Choose a new password' : 'Change password'}</h2>
      {forced && <div className="muted">Your password must be changed before you can continue.</div>}
      {error && <div className="error-box">{error}</div>}
      {done && <div className="ok-box">Password changed. Other sessions of this account were signed out.</div>}
      <label className="field">
        <span>Current password</span>
        <input type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
      </label>
      <label className="field">
        <span>New password</span>
        <input type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} required />
        <span className="hint">At least 10 characters with three of: lower case, upper case, digit, symbol.</span>
      </label>
      <label className="field">
        <span>Repeat new password</span>
        <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
      </label>
      <div className="toolbar">
        <button className="primary" type="submit" disabled={busy}>
          Change password
        </button>
        {forced && (
          <button type="button" className="ghost" onClick={() => void logout()}>
            Sign out
          </button>
        )}
      </div>
    </form>
  );

  if (forced) return <div className="login-wrap">{form}</div>;
  return (
    <>
      <div className="page-head">
        <h1>Account</h1>
      </div>
      <div className="card card-body" style={{ maxWidth: 460, marginBottom: 16 }}>
        <dl className="kv">
          <dt>User name</dt>
          <dd>{user?.username}</dd>
          <dt>Display name</dt>
          <dd>{user?.displayName}</dd>
          <dt>Role</dt>
          <dd>{user?.role}</dd>
          <dt>Idle timeout</dt>
          <dd>{user?.sessionIdleTimeoutMin} minutes</dd>
        </dl>
      </div>
      {form}
    </>
  );
}
