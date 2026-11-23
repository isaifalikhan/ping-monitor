import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { api } from '../../api/client';
import type { MacEntry, MacTable } from '../../api/types';
import { ErrorBox } from '../../components/Modal';
import { fmtAgo } from '../../util/format';

const EDGE_PORT_MAX_MACS = 4;

type Kind = 'all' | 'static' | 'dynamic';

export function MacTypeBadge({ entry }: { entry: Pick<MacEntry, 'static'> }) {
  return entry.static ? (
    <span className="badge" title="Configured on the switch (static address / MAC binding)" style={{ background: 'var(--accent-soft, rgba(76,154,255,.15))', color: 'var(--accent)' }}>
      Static
    </span>
  ) : (
    <span className="faint" title="Learned by the switch from traffic">
      Dynamic
    </span>
  );
}

/**
 * MAC addresses of a switch per port (BRIDGE / Q-BRIDGE-MIB): learned (dynamic) and configured (static), with IPs from
 * the ARP tables. With <paramref name="interfaceId"/> only that port is shown (port details).
 */
export function MacTablePanel({ deviceId, interfaceId }: { deviceId: number; interfaceId?: number }) {
  const q = useQuery({ queryKey: ['device', deviceId, 'mac-table'], queryFn: () => api.get<MacTable>(`/api/devices/${deviceId}/mac-table`), refetchInterval: 60_000 });
  const [filter, setFilter] = useState('');
  const [kind, setKind] = useState<Kind>('all');
  const [hideUplinks, setHideUplinks] = useState(true);
  const onePort = interfaceId !== undefined;

  const all = useMemo(() => (q.data?.entries ?? []).filter((e) => !onePort || e.interfaceId === interfaceId), [q.data, onePort, interfaceId]);
  // When a switch only has MACs on uplinks (e.g. a distribution switch), hiding them would show nothing: show them.
  const hasEdge = all.some((e) => e.macsOnPort <= EDGE_PORT_MAX_MACS);
  const hide = !onePort && hideUplinks && hasEdge;

  const rows = useMemo(() => {
    const f = filter.trim().toLowerCase();
    const hex = f.replace(/[:\-.\s]/g, '');
    return all.filter(
      (e) =>
        (!hide || e.macsOnPort <= EDGE_PORT_MAX_MACS) &&
        (kind === 'all' || (kind === 'static') === e.static) &&
        (!f ||
          e.port.toLowerCase().includes(f) ||
          (hex.length >= 2 && e.mac.replace(/:/g, '').toLowerCase().includes(hex)) ||
          (e.ip ?? '').includes(f) ||
          String(e.vlan ?? '') === f),
    );
  }, [all, filter, hide, kind]);

  if (q.error) return <ErrorBox error={q.error} />;
  const data = q.data;
  const ports = new Set(all.map((e) => e.port)).size;
  const statics = all.filter((e) => e.static).length;
  const uplinkMacs = all.filter((e) => e.macsOnPort > EDGE_PORT_MAX_MACS).length;

  const summary = data?.readUtc
    ? onePort
      ? `${all.length} MAC${all.length === 1 ? '' : 's'} on this port (${statics} static, ${all.length - statics} dynamic) · read ${fmtAgo(data.readUtc)}`
      : `${all.length} MACs on ${ports} ports (${statics} static, ${all.length - statics} dynamic${uplinkMacs ? `, ${uplinkMacs} on uplinks` : ''}) · read ${fmtAgo(data.readUtc)} · refreshed every ${data.refreshMinutes} min`
    : q.isLoading
      ? 'Loading…'
      : `Not read yet — the MAC table is read every ${data?.refreshMinutes ?? 15} min over SNMP (BRIDGE / Q-BRIDGE-MIB).`;

  return (
    <div className="card" style={{ padding: 14 }}>
      <div className="toolbar" style={{ marginBottom: 10, flexWrap: 'wrap', gap: 10 }}>
        {onePort ? (
          <strong>MAC addresses on this port</strong>
        ) : (
          <input placeholder="Filter: port, MAC, IP or VLAN" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ minWidth: 260 }} />
        )}
        <div className="seg" role="group" aria-label="MAC type">
          {(['all', 'static', 'dynamic'] as Kind[]).map((k) => (
            <button key={k} className={kind === k ? 'active' : ''} onClick={() => setKind(k)}>
              {k === 'all' ? 'All' : k === 'static' ? 'Static' : 'Dynamic'}
            </button>
          ))}
        </div>
        {!onePort && (
          <label className="muted" style={{ display: 'flex', alignItems: 'center', gap: 6 }} title={hasEdge ? undefined : 'This switch has MACs on uplinks/trunks only, so they are shown.'}>
            <input type="checkbox" checked={hideUplinks} disabled={!hasEdge} onChange={(e) => setHideUplinks(e.target.checked)} /> Hide uplinks/trunks (ports with more than {EDGE_PORT_MAX_MACS} MACs)
          </label>
        )}
        <span style={{ flex: 1 }} />
        <span className="faint">{summary}</span>
      </div>
      <div className="table-wrap" style={{ maxHeight: onePort ? '40vh' : '65vh' }}>
        <table className="data">
          <thead>
            <tr>
              {!onePort && <th>Port</th>}
              <th>MAC address</th>
              <th>Type</th>
              <th>VLAN</th>
              <th>IP address</th>
              {!onePort && <th>MACs on port</th>}
            </tr>
          </thead>
          <tbody>
            {rows.map((e) => (
              <tr key={`${e.port}|${e.vlan}|${e.mac}`}>
                {!onePort && <td>{e.port}</td>}
                <td className="mono">{e.mac}</td>
                <td>
                  <MacTypeBadge entry={e} />
                </td>
                <td>{e.vlan ?? '—'}</td>
                <td className="mono">{e.ip ?? <span className="faint">—</span>}</td>
                {!onePort && (
                  <td>
                    {e.macsOnPort}
                    {e.macsOnPort > EDGE_PORT_MAX_MACS && <span className="faint"> · uplink/trunk</span>}
                  </td>
                )}
              </tr>
            ))}
            {data?.readUtc && rows.length === 0 && (
              <tr>
                <td colSpan={onePort ? 4 : 6} className="faint">
                  {onePort && all.length === 0 ? 'No MAC address on this port in the last read (nothing connected, or the device has been silent for a few minutes).' : 'No MAC addresses match.'}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {!onePort && (
        <div className="faint" style={{ marginTop: 8 }}>
          Static = configured on the switch (static address / MAC binding); Dynamic = learned from traffic. IP addresses come from the ARP tables of the routed (Layer 3) switches and routers that are monitored.
        </div>
      )}
    </div>
  );
}
