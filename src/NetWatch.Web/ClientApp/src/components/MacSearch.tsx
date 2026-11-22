import { useQuery } from '@tanstack/react-query';
import { Search } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api, qs } from '../api/client';
import type { MacLocation } from '../api/types';
import { fmtAgo } from '../util/format';
import { ErrorBox } from './Modal';

/** "Where is this device plugged in?" — search the learned MAC tables of all switches by MAC or IP address. */
export function MacSearch() {
  const [text, setText] = useState('');
  const [query, setQuery] = useState('');
  const q = useQuery({
    queryKey: ['mac-search', query],
    queryFn: () => api.get<MacLocation[]>(`/api/mac-search${qs({ q: query })}`),
    enabled: query.length >= 4,
  });

  return (
    <div className="card" style={{ marginBottom: 16, padding: 14 }}>
      <form
        className="toolbar"
        style={{ gap: 10 }}
        onSubmit={(e) => {
          e.preventDefault();
          setQuery(text.trim());
        }}
      >
        <strong>Find a MAC or IP address</strong>
        <input
          placeholder="e.g. 3C:10:00:2A:00:05, 3c10.002a.0005, last digits 0005 or 10.20.10.5"
          value={text}
          onChange={(e) => setText(e.target.value)}
          style={{ minWidth: 380, flex: 1 }}
        />
        <button className="primary" type="submit" disabled={text.trim().length < 4}>
          <Search size={15} /> Search
        </button>
      </form>
      {q.error && <ErrorBox error={q.error} />}
      {q.data && (
        <div className="table-wrap" style={{ marginTop: 10, maxHeight: 320 }}>
          {q.data.length === 0 ? (
            <div className="faint">Not found in the MAC tables read so far (they are refreshed every 15 minutes).</div>
          ) : (
            <table className="data">
              <thead>
                <tr>
                  <th>MAC address</th>
                  <th>IP address</th>
                  <th>Switch</th>
                  <th>Port</th>
                  <th>VLAN</th>
                  <th>Port type</th>
                  <th>Seen</th>
                </tr>
              </thead>
              <tbody>
                {q.data.map((r) => (
                  <tr key={`${r.deviceId}|${r.port}|${r.vlan}|${r.mac}`}>
                    <td className="mono">{r.mac}</td>
                    <td className="mono">{r.ip ?? '—'}</td>
                    <td>
                      <Link to={`/devices/${r.deviceId}?tab=macs`}>{r.deviceName}</Link>
                    </td>
                    <td>{r.port}</td>
                    <td>{r.vlan ?? '—'}</td>
                    <td>{r.edgePort ? <strong>access port (connected here)</strong> : <span className="faint">uplink/trunk · {r.macsOnPort} MACs</span>}</td>
                    <td className="faint">{fmtAgo(r.readUtc)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  );
}
