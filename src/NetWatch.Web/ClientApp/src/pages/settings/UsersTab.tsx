import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { useState } from 'react';
import { api } from '../../api/client';
import type { Role, User } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Confirm, ErrorBox, Modal } from '../../components/Modal';
import { fmtDateTime } from '../../util/format';
import { KioskPanel } from './KioskPanel';

const ROLES: { value: Role; label: string }[] = [
  { value: 'Admin', label: 'Admin — everything' },
  { value: 'Operator', label: 'Operator — view + acknowledge alerts' },
  { value: 'Viewer', label: 'Viewer — read-only' },
];

function UserDialog({ user, onClose }: { user?: User; onClose: () => void }) {
  const qc = useQueryClient();
  const [username, setUsername] = useState(user?.username ?? '');
  const [displayName, setDisplayName] = useState(user?.displayName ?? '');
  const [email, setEmail] = useState(user?.email ?? '');
  const [role, setRole] = useState<Role>(user?.role ?? 'Operator');
  const [isEnabled, setEnabled] = useState(user?.isEnabled ?? true);
  const [password, setPassword] = useState('');
  const [mustChange, setMustChange] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const save = async () => {
    try {
      if (user) await api.put(`/api/users/${user.id}`, { displayName, email, role, isEnabled });
      else await api.post('/api/users', { username, displayName, email, role, password, mustChangePassword: mustChange });
      await qc.invalidateQueries({ queryKey: ['users'] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Modal
      title={user ? `Edit ${user.username}` : 'Add user'}
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
      <div className="form-grid">
        <label className="field">
          <span>User name</span>
          <input value={username} onChange={(e) => setUsername(e.target.value)} disabled={!!user} autoComplete="off" />
        </label>
        <label className="field">
          <span>Display name</span>
          <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </label>
        <label className="field">
          <span>E-mail</span>
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
        </label>
        <label className="field">
          <span>Role</span>
          <select value={role} onChange={(e) => setRole(e.target.value as Role)}>
            {ROLES.map((r) => (
              <option key={r.value} value={r.value}>
                {r.label}
              </option>
            ))}
          </select>
        </label>
        {user ? (
          <label className="check">
            <input type="checkbox" checked={isEnabled} onChange={(e) => setEnabled(e.target.checked)} /> Account enabled
          </label>
        ) : (
          <>
            <label className="field">
              <span>Initial password</span>
              <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            </label>
            <label className="check">
              <input type="checkbox" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} /> Must change at first sign-in
            </label>
          </>
        )}
      </div>
    </Modal>
  );
}

function ResetDialog({ user, onClose }: { user: User; onClose: () => void }) {
  const [password, setPassword] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState(false);
  const save = async () => {
    try {
      await api.post(`/api/users/${user.id}/reset-password`, { password, mustChangePassword: true });
      setDone(true);
    } catch (e) {
      setError(e);
    }
  };
  return (
    <Modal
      title={`Reset password for ${user.username}`}
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>{done ? 'Close' : 'Cancel'}</button>
          {!done && (
            <button className="primary" onClick={() => void save()}>
              Reset
            </button>
          )}
        </>
      }
    >
      <ErrorBox error={error} />
      {done ? (
        <div className="ok-box">Password reset. The user must change it at next sign-in; existing sessions were signed out.</div>
      ) : (
        <label className="field">
          <span>New temporary password</span>
          <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} autoFocus />
        </label>
      )}
    </Modal>
  );
}

export function UsersTab() {
  const qc = useQueryClient();
  const { user: me } = useAuth();
  const q = useQuery({ queryKey: ['users'], queryFn: () => api.get<User[]>('/api/users') });
  const [edit, setEdit] = useState<User | 'new' | null>(null);
  const [reset, setReset] = useState<User | null>(null);
  const [del, setDel] = useState<User | null>(null);
  const [error, setError] = useState<unknown>(null);

  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
      await qc.invalidateQueries({ queryKey: ['users'] });
    } catch (e) {
      setError(e);
    }
  };

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <span className="muted">Local accounts. Accounts are locked for a while after repeated failed sign-ins.</span>
        <span style={{ flex: 1 }} />
        <button className="primary" onClick={() => setEdit('new')}>
          <Plus size={16} /> Add user
        </button>
      </div>
      <ErrorBox error={error ?? q.error} />
      <div className="card">
        <table className="data">
          <thead>
            <tr>
              <th>User</th>
              <th>Role</th>
              <th>Status</th>
              <th>Last sign-in</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(q.data ?? []).map((u) => (
              <tr key={u.id}>
                <td>
                  <strong>{u.username}</strong> <span className="muted">{u.displayName !== u.username ? u.displayName : ''}</span>{' '}
                  {u.authSource === 'Ldap' && (
                    <span className="tag" title="Active Directory account: password and role come from AD (role is updated at each sign-in)">
                      AD
                    </span>
                  )}
                </td>
                <td>{u.role}</td>
                <td>
                  {!u.isEnabled ? (
                    <span className="badge s-unknown">disabled</span>
                  ) : u.isLocked ? (
                    <span className="badge s-down">locked</span>
                  ) : (
                    <span className="badge s-up">active</span>
                  )}{' '}
                  {u.mustChangePassword && <span className="tag">must change password</span>}
                </td>
                <td className="muted">{fmtDateTime(u.lastLoginUtc)}</td>
                <td className="nowrap" style={{ textAlign: 'right' }}>
                  {u.isLocked && (
                    <button className="small" onClick={() => void act(() => api.post(`/api/users/${u.id}/unlock`))}>
                      Unlock
                    </button>
                  )}{' '}
                  {u.authSource === 'Local' && (
                    <button className="small" onClick={() => setReset(u)}>
                      Reset password
                    </button>
                  )}{' '}
                  <button className="small" onClick={() => setEdit(u)}>
                    Edit
                  </button>{' '}
                  {u.id !== me?.id && (
                    <button className="small danger" onClick={() => setDel(u)}>
                      Delete
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {edit && <UserDialog user={edit === 'new' ? undefined : edit} onClose={() => setEdit(null)} />}
      {reset && <ResetDialog user={reset} onClose={() => setReset(null)} />}
      {del && (
        <Confirm
          title={`Delete user ${del.username}?`}
          danger
          confirmLabel="Delete"
          message="The account is removed. Its entries in the audit trail are kept."
          onClose={() => setDel(null)}
          onConfirm={() => {
            void act(() => api.del(`/api/users/${del.id}`));
            setDel(null);
          }}
        />
      )}
      <KioskPanel />
    </>
  );
}
