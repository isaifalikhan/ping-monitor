import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../api/client';
import { ErrorBox } from '../../components/Modal';
import { fmtDateTime } from '../../util/format';

interface KioskDisplay {
  id: number;
  name: string;
  createdUtc: string;
  createdBy?: string | null;
  lastUsedUtc?: string | null;
  revoked: boolean;
}

/** NOC wall displays: one-time links that sign a screen in as a read-only kiosk account without an idle timeout. */
export function KioskPanel() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['kiosks'], queryFn: () => api.get<KioskDisplay[]>('/api/kiosks') });
  const [name, setName] = useState('');
  const [link, setLink] = useState<string | null>(null);
  const refresh = () => void qc.invalidateQueries({ queryKey: ['kiosks'] });
  const create = useMutation({
    mutationFn: (n: string) => api.post<{ path: string }>('/api/kiosks', { name: n }),
    onSuccess: (r) => {
      setLink(`${window.location.origin}${r.path}`);
      setName('');
      refresh();
    },
  });
  const revoke = useMutation({ mutationFn: (id: number) => api.del(`/api/kiosks/${id}`), onSuccess: refresh });

  return (
    <div className="card" style={{ marginTop: 16 }}>
      <div className="card-head">
        <h2>NOC wall displays</h2>
      </div>
      <div className="card-body">
        <p className="muted" style={{ marginTop: 0 }}>
          A display link signs a wall screen in to the NOC view as a read-only account that does not time out. Open the link once on the screen&apos;s browser; it stays signed in
          until you revoke the display. Treat the link like a password.
        </p>
        <ErrorBox error={q.error ?? create.error ?? revoke.error} />
        <div className="toolbar">
          <input placeholder="Display name, e.g. NOC wall left" value={name} maxLength={60} onChange={(e) => setName(e.target.value)} />
          <button className="primary" disabled={!name.trim() || create.isPending} onClick={() => create.mutate(name.trim())}>
            Create display link
          </button>
        </div>
        {link && (
          <div className="card card-body" style={{ margin: '10px 0', borderColor: 'var(--warn)' }}>
            <strong>Copy this link now: it is shown only once.</strong>
            <div className="mono" style={{ wordBreak: 'break-all', marginTop: 6 }}>
              {link}
            </div>
            <button className="small" style={{ marginTop: 6 }} onClick={() => void navigator.clipboard?.writeText(link)}>
              Copy
            </button>
          </div>
        )}
        <table className="data" style={{ marginTop: 10 }}>
          <thead>
            <tr>
              <th>Display</th>
              <th>Created</th>
              <th>Last used</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {(q.data ?? []).map((k) => (
              <tr key={k.id} style={{ opacity: k.revoked ? 0.5 : 1 }}>
                <td>{k.name}</td>
                <td className="muted">
                  {fmtDateTime(k.createdUtc)} {k.createdBy ? `by ${k.createdBy}` : ''}
                </td>
                <td className="muted">{k.lastUsedUtc ? fmtDateTime(k.lastUsedUtc) : 'never'}</td>
                <td>
                  {k.revoked ? (
                    <span className="badge s-unknown">revoked</span>
                  ) : (
                    <button className="small danger" onClick={() => window.confirm(`Revoke "${k.name}"? The screen is signed out at once.`) && revoke.mutate(k.id)}>
                      Revoke
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {q.data?.length === 0 && <div className="empty">No displays yet.</div>}
      </div>
    </div>
  );
}
