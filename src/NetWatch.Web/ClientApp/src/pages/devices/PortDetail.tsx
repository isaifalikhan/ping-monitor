import type { NetInterface } from '../../api/types';
import { UtilBar } from '../../components/Badges';
import { fmtAgo, fmtBps, fmtDateTime, fmtSpeed, ifStatusText } from '../../util/format';
import { duplexText } from '../SwitchesPage';
import { MacTablePanel } from './MacTablePanel';

/** Details of one switch port: state, speed, duplex, traffic, errors, flaps, and the MAC addresses on it. */
export function PortDetail({ deviceId, iface, onClose }: { deviceId: number; iface: NetInterface; onClose: () => void }) {
  const up = iface.operStatus === 1;
  const adminDown = iface.adminStatus !== 1;
  const util = up ? Math.max(iface.inUtilPct ?? 0, iface.outUtilPct ?? 0) : null;
  const slow = up && iface.expectedSpeedBps != null && iface.speedBps < iface.expectedSpeedBps;

  const facts: [string, React.ReactNode][] = [
    ['Status', adminDown ? <span className="badge s-down">admin down</span> : <span className={`badge ${up ? 's-up' : 's-down'}`}>{ifStatusText(iface.operStatus)}</span>],
    ['Speed', <>{fmtSpeed(iface.speedBps)}bps{iface.expectedSpeedBps ? <span className={slow ? '' : 'faint'} style={slow ? { color: 'var(--warn)' } : undefined}> (expected {fmtSpeed(iface.expectedSpeedBps)}bps)</span> : null}</>],
    ['Duplex', up ? <span style={iface.duplex === 2 ? { color: 'var(--warn)' } : undefined}>{duplexText(iface.duplex)}</span> : '—'],
    ['Traffic in / out', up ? `${fmtBps(iface.inBps)} / ${fmtBps(iface.outBps)}` : '—'],
    ['Utilization', <UtilBar key="u" value={util} />],
    ['Errors', iface.errorsPs != null ? `${iface.errorsPs.toFixed(2)} /s` : '—'],
    ['Up/down changes (24 h)', <span style={(iface.flapCount24h ?? 0) >= 4 ? { color: 'var(--warn)' } : undefined}>{iface.flapCount24h ?? 0}</span>],
    ['Last change', iface.lastChangeUtc ? `${fmtDateTime(iface.lastChangeUtc)} PKT (${fmtAgo(iface.lastChangeUtc)})` : '—'],
    ['Port MAC', iface.mac ? <span className="mono">{iface.mac}</span> : '—'],
    ['Important port', iface.isImportant ? '★ yes — raises port-down alerts' : 'no'],
  ];

  return (
    <div className="grid" style={{ gap: 16 }}>
      <div className="card">
        <div className="card-head">
          <h3 style={{ margin: 0 }}>
            Port {iface.name}
            {iface.alias ? <span className="muted"> — {iface.alias}</span> : null}
          </h3>
          <span className="spacer" />
          <span className="faint mono">ifIndex {iface.ifIndex}</span>
          <button className="small" onClick={onClose}>
            Close
          </button>
        </div>
        <div className="card-body">
          {iface.description && iface.description !== iface.name && <div className="muted" style={{ marginBottom: 8 }}>{iface.description}</div>}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))', gap: '10px 24px' }}>
            {facts.map(([label, value]) => (
              <div key={label}>
                <div className="faint" style={{ fontSize: 12 }}>
                  {label}
                </div>
                <div>{value}</div>
              </div>
            ))}
          </div>
        </div>
      </div>
      <MacTablePanel deviceId={deviceId} interfaceId={iface.id} />
    </div>
  );
}
