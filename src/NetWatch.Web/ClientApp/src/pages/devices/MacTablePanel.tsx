import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { api } from '../../api/client';
import type { MacTable } from '../../api/types';
import { ErrorBox } from '../../components/Modal';
import { fmtAgo } from '../../util/format';

const EDGE_PORT_MAX_MACS = 4;

/** Learned MAC addresses of a switch per port (BRIDGE / Q-BRIDGE-MIB), with IPs from the ARP tables. */
export function MacTablePanel({ deviceId }: { deviceId: number }) {
  const q = useQuery({ queryKey: ['device', deviceId, 'mac-table'], queryFn: () => api.get<MacTable>(`/api/devices/${deviceId}/mac-table`), refetchInterval: 60_000 });
  const [filter, setFilter] = useState('');
  const [hideUplinks, setHideUplinks] = useState(true);

  const rows = useMemo(() => {
    const f = filter.trim().toLowerCase();
    const hex = f.replace(/[:\-.\s]/g, '');
    return (q.data?.entries ?? []).filter(
      (e) =>
        (!hideUplinks || e.macsOnPort <= EDGE_PORT_MAX_MACS) &&
        (!f ||
          e.port.toLowerCase().includes(f) ||
          (hex.length >= 2 && e.mac.replace(/:/g, '').toLowerCase().includes(hex)) ||
          (e.ip ?? '').includes(f) ||
          String(e.vlan ?? '') === f),
    );
  }, [q.data, filter, hideUplinks]);

  if (q.error) return <ErrorBox error={q.error} />;
  const data = q.data;
  const ports = new Set((data?.entries ?? []).map((e) => e.port)).size;
  const uplinkMacs = (data?.entries ?? []).filter((e) => e.macsOnPort > EDGE_PORT_MAX_MACS).length;

  return (
    <div className="card" style={{ padding: 14 }}>
      <div className="toolbar" style={{ marginBottom: 10, flexWrap: 'wrap', gap: 10 }}>
        <input placeholder="Filter: port, MAC, IP or VLAN" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ minWidth: 260 }} />
        <label className="muted" style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <input type="checkbox" checked={hideUplinks} onChange={(e) => setHideUplinks(e.target.checked)} /> Hide uplinks/trunks (ports with more than {EDGE_PORT_MAX_MACS} MACs)
        </label>
        <span style={{ flex: 1 }} />
        <span className="faint">
          {data?.readUtc
            ? `${data.entries.length} MACs on ${ports} ports${uplinkMacs ? ` (${uplinkMacs} on uplinks)` : ''} · read ${fmtAgo(data.readUtc)} · refreshed every ${data.refreshMinutes} min`
            : q.isLoading
              ? 'Loading…'
              : `Not read yet — the MAC table is read every ${data?.refreshMinutes ?? 15} min over SNMP (BRIDGE / Q-BRIDGE-MIB).`}
        </span>
      </div>
      <div className="table-wrap" style={{ maxHeight: '65vh' }}>
        <table className="data">
          <thead>
            <tr>
              <th>Port</th>
              <th>MAC address</th>
              <th>VLAN</th>
              <th>IP address</th>
              <th>MACs on port</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((e) => (
              <tr key={`${e.port}|${e.vlan}|${e.mac}`}>
                <td>{e.port}</td>
                <td className="mono">{e.mac}</td>
                <td>{e.vlan ?? '—'}</td>
                <td className="mono">{e.ip ?? <span className="faint">—</span>}</td>
                <td>
                  {e.macsOnPort}
                  {e.macsOnPort > EDGE_PORT_MAX_MACS && <span className="faint"> · uplink/trunk</span>}
                </td>
              </tr>
            ))}
            {data?.readUtc && rows.length === 0 && (
              <tr>
                <td colSpan={5} className="faint">
                  No MAC addresses match.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      <div className="faint" style={{ marginTop: 8 }}>
        IP addresses come from the ARP tables of the routed (Layer 3) switches and routers that are monitored.
      </div>
    </div>
  );
}
