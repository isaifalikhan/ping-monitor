import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../../api/client';
import type { MacEntry, MacTable, StaticMacResult } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { ErrorBox, Modal } from '../../components/Modal';
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

const SOURCE_TEXT: Record<string, string> = {
  device: 'Monitored in HIT SIEM (matched by IP)',
  lldp: 'Name the device announces over LLDP',
  dns: 'Name from DNS (reverse lookup of the IP)',
};

/** Name of the device behind a MAC; links to it when it is monitored in HIT SIEM. */
export function MacDeviceName({ entry }: { entry: Pick<MacEntry, 'name' | 'nameSource' | 'nameDeviceId'> }) {
  if (!entry.name) return <span className="faint">—</span>;
  const title = entry.nameSource ? SOURCE_TEXT[entry.nameSource] : undefined;
  return entry.nameDeviceId ? (
    <Link to={`/devices/${entry.nameDeviceId}`} title={title}>
      {entry.name}
    </Link>
  ) : (
    <span title={title}>{entry.name}</span>
  );
}

/** Add or remove a static MAC: confirmation, then the switch's answer (and what it printed when it failed). */
function StaticMacDialog({
  deviceId,
  interfaceId,
  port,
  remove,
  defaultVlan,
  onClose,
}: {
  deviceId: number;
  interfaceId: number;
  port: string;
  remove?: { mac: string; vlan: number | null };
  defaultVlan: number;
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const [mac, setMac] = useState(remove?.mac ?? '');
  const [vlan, setVlan] = useState(String(remove?.vlan ?? defaultVlan));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<StaticMacResult | null>(null);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await api.post<StaticMacResult>(`/api/devices/${deviceId}/static-macs${remove ? '/remove' : ''}`, { interfaceId, mac, vlan: Number(vlan) });
      setResult(r);
      if (r.ok) await qc.invalidateQueries({ queryKey: ['device', deviceId, 'mac-table'] });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const title = remove ? `Remove static MAC from ${port}` : `Add static MAC on ${port}`;
  return (
    <Modal
      title={title}
      onClose={onClose}
      footer={
        result?.ok ? (
          <button className="primary" onClick={onClose}>
            Close
          </button>
        ) : (
          <>
            <button onClick={onClose}>Cancel</button>
            <button className={remove ? 'danger' : 'primary'} onClick={submit} disabled={busy || (!remove && !mac.trim())}>
              {busy ? 'Working on the switch…' : remove ? 'Remove from switch' : 'Add to switch'}
            </button>
          </>
        )
      }
    >
      <div className="grid" style={{ gap: 12 }}>
        {result ? (
          <>
            <div className={result.ok ? 'notice ok' : 'notice error'} style={{ color: result.ok ? 'var(--ok)' : 'var(--crit)' }}>
              {result.ok ? '✔ ' : '✖ '}
              {result.message}
            </div>
            {result.command && (
              <div>
                <div className="faint" style={{ fontSize: 12 }}>
                  Command sent
                </div>
                <code className="mono">{result.command}</code>
              </div>
            )}
            {!result.ok && result.transcript && (
              <details open>
                <summary className="faint">What the switch printed</summary>
                <pre className="mono" style={{ maxHeight: 260, overflow: 'auto', fontSize: 12, whiteSpace: 'pre-wrap' }}>
                  {result.transcript}
                </pre>
              </details>
            )}
          </>
        ) : (
          <>
            <label className="field">
              <span>MAC address</span>
              <input value={mac} onChange={(e) => setMac(e.target.value)} placeholder="00:1A:2B:3C:4D:5E" disabled={!!remove} autoFocus={!remove} className="mono" />
            </label>
            <label className="field">
              <span>VLAN</span>
              <input type="number" min={1} max={4094} value={vlan} onChange={(e) => setVlan(e.target.value)} disabled={!!remove} style={{ width: 120 }} />
            </label>
            <div className="muted">
              HIT SIEM logs in to the switch over SSH (its SSH credential), {remove ? 'removes this static entry' : 'adds the static entry on this port'} and saves the
              configuration{remove ? '' : ' so it survives a restart'}. The change is written to the audit log.
            </div>
            {error != null && <ErrorBox error={error} />}
          </>
        )}
      </div>
    </Modal>
  );
}

/**
 * MAC addresses of a switch per port (BRIDGE / Q-BRIDGE-MIB): learned (dynamic) and configured (static), with the IP
 * (ARP) and name of the connected device. With <paramref name="interfaceId"/> only that port is shown (port details).
 * Admins can add and remove static MACs on TP-Link and Juniper switches.
 */
export function MacTablePanel({ deviceId, interfaceId, portName }: { deviceId: number; interfaceId?: number; portName?: string }) {
  const q = useQuery({ queryKey: ['device', deviceId, 'mac-table'], queryFn: () => api.get<MacTable>(`/api/devices/${deviceId}/mac-table`), refetchInterval: 60_000 });
  const { can } = useAuth();
  const [filter, setFilter] = useState('');
  const [kind, setKind] = useState<Kind>('all');
  const [hideUplinks, setHideUplinks] = useState(true);
  const [dialog, setDialog] = useState<null | { interfaceId: number; port: string; remove?: { mac: string; vlan: number | null } }>(null);
  const onePort = interfaceId !== undefined;
  const canChange = can('Admin') && !!q.data?.canEdit;

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
          (e.name ?? '').toLowerCase().includes(f) ||
          String(e.vlan ?? '') === f),
    );
  }, [all, filter, hide, kind]);

  if (q.error) return <ErrorBox error={q.error} />;
  const data = q.data;
  const ports = new Set(all.map((e) => e.port)).size;
  const statics = all.filter((e) => e.static).length;
  const uplinkMacs = all.filter((e) => e.macsOnPort > EDGE_PORT_MAX_MACS).length;
  // Default VLAN for a new entry: the one most used on this port.
  const vlanCounts = new Map<number, number>();
  all.forEach((e) => e.vlan != null && vlanCounts.set(e.vlan, (vlanCounts.get(e.vlan) ?? 0) + 1));
  const defaultVlan = [...vlanCounts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? 1;
  const cols = (onePort ? 5 : 7) + (canChange ? 1 : 0);

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
          <input placeholder="Filter: port, MAC, IP, name or VLAN" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ minWidth: 260 }} />
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
        {onePort && canChange && (
          <button className="primary small" onClick={() => setDialog({ interfaceId: interfaceId!, port: portName ?? 'this port' })}>
            + Add static MAC
          </button>
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
              <th>Device name</th>
              {!onePort && <th>MACs on port</th>}
              {canChange && <th />}
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
                <td>
                  <MacDeviceName entry={e} />
                </td>
                {!onePort && (
                  <td>
                    {e.macsOnPort}
                    {e.macsOnPort > EDGE_PORT_MAX_MACS && <span className="faint"> · uplink/trunk</span>}
                  </td>
                )}
                {canChange && (
                  <td style={{ textAlign: 'right' }}>
                    {e.static && e.interfaceId != null && (
                      <button className="small danger" onClick={() => setDialog({ interfaceId: e.interfaceId!, port: e.port, remove: { mac: e.mac, vlan: e.vlan } })}>
                        Remove
                      </button>
                    )}
                  </td>
                )}
              </tr>
            ))}
            {data?.readUtc && rows.length === 0 && (
              <tr>
                <td colSpan={cols} className="faint">
                  {onePort && all.length === 0 ? 'No MAC address on this port in the last read (nothing connected, or the device has been silent for a few minutes).' : 'No MAC addresses match.'}
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
      {!onePort && (
        <div className="faint" style={{ marginTop: 8 }}>
          Static = configured on the switch (static address / MAC binding); Dynamic = learned from traffic. IP addresses come from the ARP tables of the routed (Layer 3) switches and routers that
          are monitored; device names from HIT SIEM's own device list, LLDP, or DNS.{canChange ? ' To add a static MAC, open a port (Interfaces tab or the Switches page).' : ''}
        </div>
      )}
      {dialog && (
        <StaticMacDialog
          deviceId={deviceId}
          interfaceId={dialog.interfaceId}
          port={dialog.port}
          remove={dialog.remove}
          defaultVlan={dialog.remove?.vlan ?? defaultVlan}
          onClose={() => setDialog(null)}
        />
      )}
    </div>
  );
}
