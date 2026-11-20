import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../api/client';
import type { Credential, DeviceType, DiscoveryJob, Group } from '../../api/types';
import { DEVICE_TYPES } from '../../api/types';
import { ErrorBox, Modal } from '../../components/Modal';
import { fmtMs, typeLabel } from '../../util/format';
import { groupOptions } from '../settings/GroupsTab';

interface Pick {
  name: string;
  type: DeviceType;
}

/** Subnet discovery: ICMP sweep + SNMP sysObjectID probe, then add the selected hosts. */
export function DiscoveryDialog({ onClose }: { onClose: () => void }) {
  const qc = useQueryClient();
  const creds = useQuery({ queryKey: ['credentials'], queryFn: () => api.get<Credential[]>('/api/credentials') });
  const groups = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });
  const [cidr, setCidr] = useState('');
  const [credIds, setCredIds] = useState<number[]>([]);
  const [jobId, setJobId] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [picks, setPicks] = useState<Record<string, Pick>>({});
  const [groupId, setGroupId] = useState<number | null>(null);
  const [added, setAdded] = useState<number | null>(null);

  const job = useQuery({
    queryKey: ['discovery', jobId],
    queryFn: () => api.get<DiscoveryJob>(`/api/discovery/${jobId}`),
    enabled: !!jobId,
    refetchInterval: (q) => (q.state.data?.state === 'Running' ? 1500 : false),
  });

  const snmpCreds = (creds.data ?? []).filter((c) => c.kind === 'SnmpV2c' || c.kind === 'SnmpV3');

  const start = async () => {
    setError(null);
    setPicks({});
    setAdded(null);
    try {
      const r = await api.post<{ id: string }>('/api/discovery', { cidr: cidr.trim(), credentialProfileIds: credIds });
      setJobId(r.id);
    } catch (e) {
      setError(e);
    }
  };

  const add = async () => {
    const results = job.data?.results ?? [];
    const items = Object.entries(picks).map(([ip, p]) => ({
      ipAddress: ip,
      name: p.name,
      type: p.type,
      snmpCredentialId: results.find((r) => r.ipAddress === ip)?.snmpCredentialId ?? null,
      groupId,
    }));
    try {
      const r = await api.post<{ created: number }>(`/api/discovery/${jobId}/add`, { items });
      setAdded(r.created);
      setPicks({});
      await qc.invalidateQueries({ queryKey: ['devices'] });
      await job.refetch();
    } catch (e) {
      setError(e);
    }
  };

  const data = job.data;
  const togglePick = (ip: string, name: string, type: DeviceType) =>
    setPicks((p) => {
      const next = { ...p };
      if (next[ip]) delete next[ip];
      else next[ip] = { name, type };
      return next;
    });

  return (
    <Modal
      title="Discover devices"
      onClose={onClose}
      wide
      footer={
        <>
          <button onClick={onClose}>Close</button>
          {data && Object.keys(picks).length > 0 && (
            <button className="primary" onClick={() => void add()}>
              Add {Object.keys(picks).length} device(s)
            </button>
          )}
        </>
      }
    >
      <ErrorBox error={error ?? job.error} />
      <div className="form-grid">
        <label className="field">
          <span>Subnet (CIDR)</span>
          <input className="mono" placeholder="10.0.1.0/24" value={cidr} onChange={(e) => setCidr(e.target.value)} />
          <span className="hint">Up to /22 (1024 addresses) per scan.</span>
        </label>
        <div className="field">
          <span>Try SNMP credentials</span>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {snmpCreds.length === 0 && <span className="faint">No SNMP profiles yet — ICMP only.</span>}
            {snmpCreds.map((c) => (
              <label key={c.id} className="check">
                <input
                  type="checkbox"
                  checked={credIds.includes(c.id)}
                  onChange={(e) => setCredIds((ids) => (e.target.checked ? [...ids, c.id] : ids.filter((i) => i !== c.id)))}
                />
                {c.name}
              </label>
            ))}
          </div>
        </div>
        <div style={{ alignSelf: 'end' }}>
          <button className="primary" onClick={() => void start()} disabled={!cidr.trim() || data?.state === 'Running'}>
            {data?.state === 'Running' ? 'Scanning…' : 'Start scan'}
          </button>
        </div>
      </div>

      {data && (
        <>
          <div className="toolbar">
            <span className="muted">
              {data.state === 'Running' ? `Scanning ${data.scanned}/${data.total}…` : `${data.state}: ${data.results.length} host(s) found in ${data.total} addresses.`}
            </span>
            {data.error && <span className="error-box">{data.error}</span>}
            {added !== null && <span className="ok-box">{added} device(s) added.</span>}
            <span style={{ flex: 1 }} />
            <label className="field" style={{ minWidth: 260 }}>
              <span>Add to group</span>
              <select value={groupId ?? ''} onChange={(e) => setGroupId(e.target.value ? Number(e.target.value) : null)}>
                <option value="">— none —</option>
                {groupOptions(groups.data ?? []).map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.label}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="table-wrap" style={{ maxHeight: 420 }}>
            <table className="data">
              <thead>
                <tr>
                  <th />
                  <th>IP</th>
                  <th>Name</th>
                  <th>Type</th>
                  <th>Vendor / model</th>
                  <th className="num">RTT</th>
                  <th>SNMP</th>
                </tr>
              </thead>
              <tbody>
                {data.results.map((r) => {
                  const pick = picks[r.ipAddress];
                  const defaultName = r.sysName || r.ipAddress;
                  return (
                    <tr key={r.ipAddress}>
                      <td>
                        <input
                          type="checkbox"
                          disabled={r.alreadyMonitored}
                          checked={!!pick}
                          onChange={() => togglePick(r.ipAddress, defaultName, r.suggestedType)}
                          aria-label={`Select ${r.ipAddress}`}
                        />
                      </td>
                      <td className="mono">{r.ipAddress}</td>
                      <td>
                        {pick ? (
                          <input value={pick.name} onChange={(e) => setPicks((p) => ({ ...p, [r.ipAddress]: { ...pick, name: e.target.value } }))} />
                        ) : (
                          <>
                            {defaultName} {r.alreadyMonitored && <span className="tag">already monitored</span>}
                          </>
                        )}
                      </td>
                      <td>
                        {pick ? (
                          <select value={pick.type} onChange={(e) => setPicks((p) => ({ ...p, [r.ipAddress]: { ...pick, type: e.target.value as DeviceType } }))}>
                            {DEVICE_TYPES.map((t) => (
                              <option key={t} value={t}>
                                {typeLabel(t)}
                              </option>
                            ))}
                          </select>
                        ) : (
                          typeLabel(r.suggestedType)
                        )}
                      </td>
                      <td className="muted" title={r.sysDescription ?? ''}>
                        {[r.vendor, r.model].filter(Boolean).join(' ') || '—'}
                      </td>
                      <td className="num">{r.respondsToPing ? fmtMs(r.rttMs) : 'no ping'}</td>
                      <td>{r.snmpCredentialId ? <span className="badge s-up">yes</span> : <span className="faint">no</span>}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </>
      )}
    </Modal>
  );
}
