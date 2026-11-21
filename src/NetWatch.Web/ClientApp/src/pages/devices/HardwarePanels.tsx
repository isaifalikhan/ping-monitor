import type { GpuInfo, HardwareComponentInfo } from '../../api/types';
import { fmtAgo, fmtNum } from '../../util/format';

const CATEGORY_LABEL: Record<string, string> = {
  System: 'System',
  Processor: 'Processors',
  Memory: 'Memory',
  Temperature: 'Temperatures',
  Fan: 'Fans',
  PowerSupply: 'Power supplies',
  StorageController: 'Storage controllers',
  Drive: 'Drives',
};

const ORDER = ['System', 'Processor', 'Memory', 'PowerSupply', 'Fan', 'Temperature', 'StorageController', 'Drive'];

export function healthClass(health?: string | null, state?: string | null): string {
  if (state === 'Absent') return 's-unknown';
  if (!health) return 's-unknown';
  return health === 'OK' ? 's-up' : health === 'Warning' ? 's-warning' : 's-down';
}

/** GPU panel: only rendered when the server has at least one GPU. */
export function GpuPanel({ gpus }: { gpus: GpuInfo[] }) {
  if (gpus.length === 0) return null;
  return (
    <div className="card">
      <div className="card-head">
        <h3>GPUs</h3>
        <span className="muted">{gpus.length} GPU(s)</span>
      </div>
      <div className="table-wrap">
        <table className="data">
          <thead>
            <tr>
              <th>#</th>
              <th>GPU</th>
              <th className="num">Utilization</th>
              <th className="num">Memory</th>
              <th className="num">Temperature</th>
              <th className="num">Power</th>
              <th className="num">Fan</th>
              <th>Driver</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {gpus.map((g) => {
              const memPct = g.memoryUsedMb != null && g.memoryTotalMb ? (g.memoryUsedMb / g.memoryTotalMb) * 100 : null;
              return (
                <tr key={g.index}>
                  <td className="num">{g.index}</td>
                  <td>
                    <strong>{g.name}</strong> <span className="faint">{g.vendor}</span>
                  </td>
                  <td className="num" style={{ color: (g.utilizationPct ?? 0) > 95 ? 'var(--warn)' : undefined }}>
                    {g.utilizationPct == null ? '—' : `${fmtNum(g.utilizationPct, 0)} %`}
                  </td>
                  <td className="num" style={{ color: (memPct ?? 0) > 90 ? 'var(--warn)' : undefined }}>
                    {g.memoryUsedMb == null ? '—' : `${fmtNum(g.memoryUsedMb / 1024, 1)}${g.memoryTotalMb ? ` / ${fmtNum(g.memoryTotalMb / 1024, 0)}` : ''} GB`}
                  </td>
                  <td className="num" style={{ color: (g.temperatureC ?? 0) > 85 ? 'var(--crit)' : undefined }}>
                    {g.temperatureC == null ? '—' : `${fmtNum(g.temperatureC, 0)} °C`}
                  </td>
                  <td className="num">{g.powerW == null ? '—' : `${fmtNum(g.powerW, 0)} W`}</td>
                  <td className="num">{g.fanPct == null ? '—' : `${fmtNum(g.fanPct, 0)} %`}</td>
                  <td className="muted">{g.driverVersion ?? '—'}</td>
                  <td>{g.problem ? <span className="badge s-down" title={g.problem}>{g.problem}</span> : <span className="badge s-up">OK</span>}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** Hardware health from iLO / iDRAC (Redfish) or the HPE/Dell SNMP agent. */
export function HardwarePanel({ components }: { components: HardwareComponentInfo[] }) {
  if (components.length === 0) return null;
  const groups = [...new Set(components.map((c) => c.category))].sort((a, b) => (ORDER.indexOf(a) + 99) % 99 - (ORDER.indexOf(b) + 99) % 99);
  const problems = components.filter((c) => c.state !== 'Absent' && c.health && c.health !== 'OK').length;
  const newest = components.reduce((m, c) => (c.lastSeenUtc > m ? c.lastSeenUtc : m), components[0].lastSeenUtc);
  return (
    <div className="card">
      <div className="card-head">
        <h3>Hardware health</h3>
        <span className="muted">
          {components[0].source} · read {fmtAgo(newest)}
        </span>
        <span className="spacer" />
        {problems > 0 ? <span className="badge s-down">{problems} problem(s)</span> : <span className="badge s-up">All OK</span>}
      </div>
      <div className="card-body" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', gap: 12 }}>
        {groups.map((g) => (
          <div key={g}>
            <div className="label" style={{ marginBottom: 4 }}>
              {CATEGORY_LABEL[g] ?? g}
            </div>
            {components
              .filter((c) => c.category === g)
              .map((c) => (
                <div key={c.name} style={{ display: 'flex', gap: 6, alignItems: 'baseline', fontSize: 13, padding: '2px 0' }} title={c.details ?? ''}>
                  <span className={`badge ${healthClass(c.health, c.state)}`}>{c.state === 'Absent' ? 'absent' : (c.health ?? '?')}</span>
                  <span className="clip" style={{ flex: 1 }}>
                    {c.name}
                  </span>
                  {c.reading != null && (
                    <span className="mono muted">
                      {fmtNum(c.reading, c.unit === 'C' ? 0 : 0)} {c.unit === 'C' ? '°C' : c.unit}
                    </span>
                  )}
                </div>
              ))}
          </div>
        ))}
      </div>
    </div>
  );
}
