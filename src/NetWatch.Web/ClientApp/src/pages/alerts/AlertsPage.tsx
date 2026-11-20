import { useQuery, useQueryClient } from '@tanstack/react-query';
import { CheckCheck, XCircle } from 'lucide-react';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { api, qs } from '../../api/client';
import type { Alert, AlertHistory, Paged, Severity } from '../../api/types';
import { SEVERITIES } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { SeverityBadge } from '../../components/Badges';
import { DataTable, type Column } from '../../components/DataTable';
import { ErrorBox, Modal } from '../../components/Modal';
import { fmtAgo, fmtDateTime, fmtDuration, toMs } from '../../util/format';

type View = 'active' | 'acknowledged' | 'history';

const viewStates: Record<View, string> = {
  active: 'Active',
  acknowledged: 'Acknowledged',
  history: 'Resolved,Closed',
};

function AckDialog({ ids, action, onClose }: { ids: number[]; action: 'ack' | 'close'; onClose: () => void }) {
  const qc = useQueryClient();
  const [comment, setComment] = useState('');
  const [includeOpen, setIncludeOpen] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const submit = async () => {
    try {
      await api.post(action === 'ack' ? '/api/alerts/ack' : '/api/alerts/close', { ids, comment, includeOpen });
      await qc.invalidateQueries({ queryKey: ['alerts'] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };
  return (
    <Modal
      title={action === 'ack' ? `Acknowledge ${ids.length} alert(s)` : `Close ${ids.length} alert(s)`}
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Cancel</button>
          <button className="primary" onClick={() => void submit()}>
            {action === 'ack' ? 'Acknowledge' : 'Close'}
          </button>
        </>
      }
    >
      <ErrorBox error={error} />
      <label className="field">
        <span>Comment {action === 'ack' ? '(what is being done?)' : ''}</span>
        <textarea rows={3} value={comment} onChange={(e) => setComment(e.target.value)} autoFocus maxLength={2000} />
      </label>
      {action === 'close' && (
        <label className="check">
          <input type="checkbox" checked={includeOpen} onChange={(e) => setIncludeOpen(e.target.checked)} />
          Also close alerts whose condition is still active (they will be raised again if the problem persists)
        </label>
      )}
    </Modal>
  );
}

/** Escalated / flapping markers. */
export function AlertFlags({ alert }: { alert: Alert }) {
  return (
    <>
      {alert.escalatedUtc && alert.state === 'Active' && (
        <span className="badge sev-critical" title={`Escalated ${fmtDateTime(alert.escalatedUtc)}: not acknowledged in time`}>
          escalated
        </span>
      )}{' '}
      {alert.isFlapping && (
        <span className="badge s-warning" title={`${alert.flapCount} state changes — stays open until the condition is stable`}>
          flapping
        </span>
      )}
    </>
  );
}

/** Alert detail with history and comments; used here and from the device page. */
export function AlertDetailDialog({ alertId, onClose }: { alertId: number; onClose: () => void }) {
  const qc = useQueryClient();
  const { can } = useAuth();
  const [comment, setComment] = useState('');
  const [error, setError] = useState<unknown>(null);
  const q = useQuery({
    queryKey: ['alerts', 'detail', alertId],
    queryFn: () => api.get<{ alert: Alert; history: AlertHistory[] }>(`/api/alerts/${alertId}`),
  });

  const act = async (fn: () => Promise<unknown>) => {
    setError(null);
    try {
      await fn();
      setComment('');
      await qc.invalidateQueries({ queryKey: ['alerts'] });
    } catch (e) {
      setError(e);
    }
  };

  const a = q.data?.alert;
  return (
    <Modal
      title={a ? a.title : 'Alert'}
      onClose={onClose}
      wide
      footer={
        a &&
        can('Operator') && (
          <>
            {a.state === 'Active' && (
              <button className="primary" onClick={() => void act(() => api.post('/api/alerts/ack', { ids: [a.id], comment }))}>
                Acknowledge
              </button>
            )}
            {(a.state === 'Resolved' || !a.autoResolve) && a.state !== 'Closed' && (
              <button onClick={() => void act(() => api.post('/api/alerts/close', { ids: [a.id], comment }))}>Close</button>
            )}
          </>
        )
      }
    >
      <ErrorBox error={error ?? q.error} />
      {a && (
        <>
          <dl className="kv">
            <dt>Severity</dt>
            <dd>
              <SeverityBadge severity={a.severity} />
            </dd>
            <dt>State</dt>
            <dd>
              {a.state} <AlertFlags alert={a} />
            </dd>
            <dt>Device</dt>
            <dd>{a.deviceId ? <Link to={`/devices/${a.deviceId}`} onClick={onClose}>{a.deviceName ?? `#${a.deviceId}`}</Link> : '—'}</dd>
            <dt>Message</dt>
            <dd>{a.message}</dd>
            <dt>First seen</dt>
            <dd>{fmtDateTime(a.firstSeenUtc)}</dd>
            <dt>Last seen</dt>
            <dd>
              {fmtDateTime(a.lastSeenUtc)} {a.occurrenceCount > 1 && <span className="muted">({a.occurrenceCount} occurrences)</span>}
            </dd>
            {a.ackUtc && (
              <>
                <dt>Acknowledged</dt>
                <dd>
                  {fmtDateTime(a.ackUtc)} by {a.ackByUsername} {a.ackComment && <span className="muted">— {a.ackComment}</span>}
                </dd>
              </>
            )}
            {a.resolvedUtc && (
              <>
                <dt>Resolved</dt>
                <dd>
                  {fmtDateTime(a.resolvedUtc)} <span className="muted">(duration {fmtDuration((toMs(a.resolvedUtc) - toMs(a.firstSeenUtc)) / 1000)})</span>
                </dd>
              </>
            )}
            {a.closedUtc && (
              <>
                <dt>Closed</dt>
                <dd>
                  {fmtDateTime(a.closedUtc)} by {a.closedByUsername}
                </dd>
              </>
            )}
          </dl>
          <h3>History</h3>
          <div className="table-wrap" style={{ maxHeight: 260 }}>
            <table className="data">
              <tbody>
                {(q.data?.history ?? []).map((h, i) => (
                  <tr key={i}>
                    <td className="nowrap muted" style={{ width: 170 }}>
                      {fmtDateTime(h.timestampUtc)}
                    </td>
                    <td style={{ width: 120 }}>{h.action}</td>
                    <td className="muted" style={{ width: 120 }}>
                      {h.username ?? 'system'}
                    </td>
                    <td>{h.comment}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {can('Operator') && (
            <div className="toolbar">
              <input style={{ flex: 1 }} placeholder="Add a comment…" value={comment} onChange={(e) => setComment(e.target.value)} maxLength={2000} />
              <button disabled={!comment.trim()} onClick={() => void act(() => api.post(`/api/alerts/${a.id}/comment`, { comment }))}>
                Add comment
              </button>
            </div>
          )}
        </>
      )}
    </Modal>
  );
}

export function AlertsPage() {
  const { can } = useAuth();
  const [view, setView] = useState<View>('active');
  const [minSeverity, setMinSeverity] = useState<Severity | ''>('');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<Set<string | number>>(new Set());
  const [dialog, setDialog] = useState<'ack' | 'close' | null>(null);
  const [detail, setDetail] = useState<number | null>(null);

  const q = useQuery({
    queryKey: ['alerts', 'list', view, minSeverity],
    queryFn: () => api.get<Paged<Alert>>(`/api/alerts${qs({ state: viewStates[view], minSeverity, pageSize: view === 'history' ? 1000 : 2000 })}`),
    refetchInterval: 30000,
  });

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (q.data?.items ?? []).filter((a) => !term || a.title.toLowerCase().includes(term) || a.message.toLowerCase().includes(term) || (a.deviceName ?? '').toLowerCase().includes(term));
  }, [q.data, search]);

  const sevOrder = (s: Severity) => SEVERITIES.indexOf(s);
  const columns: Column<Alert>[] = [
    { key: 'sev', header: 'Severity', render: (a) => <SeverityBadge severity={a.severity} />, sort: (a) => -sevOrder(a.severity), width: 100 },
    { key: 'device', header: 'Device', render: (a) => (a.deviceId ? <Link to={`/devices/${a.deviceId}`} onClick={(e) => e.stopPropagation()}>{a.deviceName ?? `#${a.deviceId}`}</Link> : '—'), sort: (a) => a.deviceName ?? '' },
    {
      key: 'title',
      header: 'Alert',
      render: (a) => (
        <div>
          <div>
            {a.title} <AlertFlags alert={a} />
          </div>
          <div className="muted" style={{ fontSize: 12.5 }}>
            {a.message}
          </div>
        </div>
      ),
      sort: (a) => a.title,
    },
    { key: 'first', header: 'Since', render: (a) => <span className="nowrap" title={fmtDateTime(a.firstSeenUtc)}>{view === 'history' ? fmtDateTime(a.firstSeenUtc) : fmtAgo(a.firstSeenUtc)}</span>, sort: (a) => a.firstSeenUtc },
    ...(view === 'acknowledged'
      ? [{ key: 'ack', header: 'Acknowledged', render: (a: Alert) => <span className="muted">{a.ackByUsername} {a.ackComment ? `— ${a.ackComment}` : ''}</span>, sort: (a: Alert) => a.ackUtc ?? '' }]
      : []),
    ...(view === 'history'
      ? [
          { key: 'state', header: 'State', render: (a: Alert) => a.state, sort: (a: Alert) => a.state },
          { key: 'resolved', header: 'Resolved', render: (a: Alert) => <span className="nowrap muted">{fmtDateTime(a.resolvedUtc ?? a.closedUtc)}</span>, sort: (a: Alert) => a.resolvedUtc ?? '' },
        ]
      : []),
  ];

  const ids = [...selected].map(Number);
  return (
    <>
      <div className="page-head">
        <h1>Alerts</h1>
        <div className="seg">
          {(['active', 'acknowledged', 'history'] as View[]).map((v) => (
            <button
              key={v}
              className={view === v ? 'active' : ''}
              onClick={() => {
                setView(v);
                setSelected(new Set());
              }}
            >
              {v[0].toUpperCase() + v.slice(1)}
            </button>
          ))}
        </div>
        <span className="spacer" />
        {can('Operator') && selected.size > 0 && (
          <div className="toolbar">
            {view === 'active' && (
              <button className="primary" onClick={() => setDialog('ack')}>
                <CheckCheck size={16} /> Acknowledge ({selected.size})
              </button>
            )}
            <button onClick={() => setDialog('close')}>
              <XCircle size={16} /> Close ({selected.size})
            </button>
          </div>
        )}
      </div>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <input type="search" placeholder="Search alerts…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <select value={minSeverity} onChange={(e) => setMinSeverity(e.target.value as Severity | '')} aria-label="Minimum severity">
          <option value="">All severities</option>
          {SEVERITIES.map((s) => (
            <option key={s} value={s}>
              {s} and above
            </option>
          ))}
        </select>
        <span className="muted">{rows.length} alert(s)</span>
      </div>
      <ErrorBox error={q.error} />
      <div className="card">
        <DataTable
          rows={rows}
          columns={columns}
          rowKey={(a) => a.id}
          onRowClick={(a) => setDetail(a.id)}
          initialSort={{ key: 'sev' }}
          selectable={can('Operator') && view !== 'history'}
          selected={selected}
          onSelectedChange={setSelected}
          maxHeight="calc(100vh - 230px)"
          empty={q.isLoading ? 'Loading…' : view === 'active' ? 'No active alerts. 🎉' : 'Nothing here.'}
        />
      </div>
      {dialog && (
        <AckDialog
          ids={ids}
          action={dialog}
          onClose={() => {
            setDialog(null);
            setSelected(new Set());
          }}
        />
      )}
      {detail !== null && <AlertDetailDialog alertId={detail} onClose={() => setDetail(null)} />}
    </>
  );
}
