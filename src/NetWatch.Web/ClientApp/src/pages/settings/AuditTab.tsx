import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api, qs } from '../../api/client';
import type { AuditEntry, Paged } from '../../api/types';
import { ErrorBox } from '../../components/Modal';
import { fmtDateTime } from '../../util/format';

export function AuditTab() {
  const [user, setUser] = useState('');
  const [action, setAction] = useState('');
  const [page, setPage] = useState(1);
  const q = useQuery({
    queryKey: ['audit', user, action, page],
    queryFn: () => api.get<Paged<AuditEntry>>(`/api/audit${qs({ user, action, page, pageSize: 100 })}`),
  });
  const total = q.data?.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / 100));

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input
          type="search"
          placeholder="User…"
          value={user}
          onChange={(e) => {
            setUser(e.target.value);
            setPage(1);
          }}
        />
        <input
          type="search"
          placeholder="Action (e.g. Login, Device)…"
          value={action}
          onChange={(e) => {
            setAction(e.target.value);
            setPage(1);
          }}
        />
        <span style={{ flex: 1 }} />
        <span className="muted">{total} entries</span>
        <button disabled={page <= 1} onClick={() => setPage(page - 1)}>
          ‹
        </button>
        <span className="muted">
          {page}/{pages}
        </span>
        <button disabled={page >= pages} onClick={() => setPage(page + 1)}>
          ›
        </button>
      </div>
      <ErrorBox error={q.error} />
      <div className="card table-wrap" style={{ maxHeight: 'calc(100vh - 270px)' }}>
        <table className="data">
          <thead>
            <tr>
              <th>Time (PKT)</th>
              <th>User</th>
              <th>Action</th>
              <th>Target</th>
              <th>Details</th>
              <th>Client IP</th>
            </tr>
          </thead>
          <tbody>
            {(q.data?.items ?? []).map((a) => (
              <tr key={a.id}>
                <td className="nowrap muted">{fmtDateTime(a.timestampUtc)}</td>
                <td>{a.username}</td>
                <td>
                  <span className={a.action.includes('Fail') || a.action.includes('Lock') ? 'badge s-down' : 'tag'}>{a.action}</span>
                </td>
                <td className="muted">{a.targetType ? `${a.targetType} ${a.targetId ?? ''}` : ''}</td>
                <td className="muted" style={{ maxWidth: 480, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={a.details ?? ''}>
                  {a.details}
                </td>
                <td className="mono muted">{a.clientIp}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
