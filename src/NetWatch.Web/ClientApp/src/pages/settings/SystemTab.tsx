import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { ErrorBox } from '../../components/Modal';
import { fmtAgo, fmtBytes, fmtNum } from '../../util/format';

interface SystemStatus {
  version: string;
  startedUtc: string;
  devices: number;
  interfacesLive: number;
  polling: { totalPolls: number; failedPolls: number; overruns: number; inFlight: number; queued: number; avgDurationMs: number; maxDurationMs: number };
  metrics: { pendingWrites: number; droppedSamples: number };
  alertEngine: { queued: number; processed: number; dropped: number };
  storage: { configDbBytes: number; metricsDbBytes: number; eventsDbBytes: number; diskFreeBytes: number; diskTotalBytes: number; dataPath: string };
}

/** Service self-monitoring: collector load, queues, database sizes and disk space. */
export function SystemTab() {
  const q = useQuery({ queryKey: ['system'], queryFn: () => api.get<SystemStatus>('/api/system/status'), refetchInterval: 5000 });
  const s = q.data;
  if (q.error) return <ErrorBox error={q.error} />;
  if (!s) return <div className="empty">Loading…</div>;
  const diskPct = s.storage.diskTotalBytes ? (1 - s.storage.diskFreeBytes / s.storage.diskTotalBytes) * 100 : 0;
  return (
    <div className="grid cols-2">
      <div className="card card-body">
        <h2 style={{ marginBottom: 10 }}>Service</h2>
        <dl className="kv">
          <dt>Version</dt>
          <dd>{s.version}</dd>
          <dt>Running since</dt>
          <dd>{fmtAgo(s.startedUtc)}</dd>
          <dt>Devices</dt>
          <dd>{s.devices}</dd>
          <dt>Interfaces with live data</dt>
          <dd>{s.interfacesLive}</dd>
          <dt>Data folder</dt>
          <dd className="mono">{s.storage.dataPath}</dd>
        </dl>
      </div>
      <div className="card card-body">
        <h2 style={{ marginBottom: 10 }}>Polling</h2>
        <dl className="kv">
          <dt>Polls completed</dt>
          <dd>{s.polling.totalPolls.toLocaleString()}</dd>
          <dt>Failed polls</dt>
          <dd>{s.polling.failedPolls.toLocaleString()}</dd>
          <dt>In progress / queued</dt>
          <dd>
            {s.polling.inFlight} / {s.polling.queued}
          </dd>
          <dt>Average poll duration</dt>
          <dd>{fmtNum(s.polling.avgDurationMs, 0)} ms</dd>
          <dt>Peak poll duration</dt>
          <dd>{fmtNum(s.polling.maxDurationMs, 0)} ms</dd>
          <dt>Overruns</dt>
          <dd style={{ color: s.polling.overruns > 0 ? 'var(--warn)' : undefined }}>
            {s.polling.overruns} {s.polling.overruns > 0 && '(polls took longer than the interval — raise parallel polls or the interval)'}
          </dd>
        </dl>
      </div>
      <div className="card card-body">
        <h2 style={{ marginBottom: 10 }}>Queues</h2>
        <dl className="kv">
          <dt>Metric samples waiting</dt>
          <dd>{s.metrics.pendingWrites.toLocaleString()}</dd>
          <dt>Metric samples dropped</dt>
          <dd style={{ color: s.metrics.droppedSamples > 0 ? 'var(--crit)' : undefined }}>{s.metrics.droppedSamples.toLocaleString()}</dd>
          <dt>Alert signals waiting</dt>
          <dd>{s.alertEngine.queued.toLocaleString()}</dd>
          <dt>Alert signals processed</dt>
          <dd>{s.alertEngine.processed.toLocaleString()}</dd>
          <dt>Alert signals dropped</dt>
          <dd style={{ color: s.alertEngine.dropped > 0 ? 'var(--crit)' : undefined }}>{s.alertEngine.dropped.toLocaleString()}</dd>
        </dl>
      </div>
      <div className="card card-body">
        <h2 style={{ marginBottom: 10 }}>Storage</h2>
        <dl className="kv">
          <dt>Configuration DB</dt>
          <dd>{fmtBytes(s.storage.configDbBytes)}</dd>
          <dt>Metrics DB</dt>
          <dd>{fmtBytes(s.storage.metricsDbBytes)}</dd>
          <dt>Events DB</dt>
          <dd>{fmtBytes(s.storage.eventsDbBytes)}</dd>
          <dt>Disk free</dt>
          <dd style={{ color: diskPct > 90 ? 'var(--crit)' : undefined }}>
            {fmtBytes(s.storage.diskFreeBytes)} of {fmtBytes(s.storage.diskTotalBytes)} ({fmtNum(100 - diskPct, 0)}% free)
          </dd>
        </dl>
      </div>
    </div>
  );
}
