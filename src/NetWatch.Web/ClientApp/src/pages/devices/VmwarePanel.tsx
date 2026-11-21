import { useQuery, useQueryClient } from '@tanstack/react-query';
import { KeyRound } from 'lucide-react';
import { useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../../api/client';
import type { DeviceDetail, VmwareSnapshot, VmwareVm } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { Confirm, ErrorBox } from '../../components/Modal';
import { fmtBytes, fmtDateTime, fmtNum } from '../../util/format';

/**
 * VMware information on the device page: for a VM that vCenter reports (matched by IP or name) its host, tools,
 * CPU ready and snapshots; for a vCenter/ESXi device the link to the VMware page and the pinned certificate.
 */
export function VmwarePanel({ device }: { device: DeviceDetail }) {
  const { can } = useAuth();
  const qc = useQueryClient();
  const [reset, setReset] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const isSource = device.credentials.some((c) => c.purpose === 'VCenter');
  const vm = useQuery({
    queryKey: ['device', device.id, 'vmware'],
    queryFn: () => api.get<{ vm: VmwareVm; snapshots: VmwareSnapshot[] } | undefined>(`/api/vmware/vm-by-device/${device.id}`),
    enabled: !isSource,
    refetchInterval: 60000,
  });

  if (isSource) {
    return (
      <div className="card">
        <div className="card-head">
          <h3>VMware</h3>
          <span className="spacer" />
          <Link to="/vmware">Hosts, VMs, datastores →</Link>
        </div>
        <div className="card-body">
          <ErrorBox error={error} />
          <dl className="kv">
            <dt>vCenter credential</dt>
            <dd>{device.credentials.find((c) => c.purpose === 'VCenter')?.profileName}</dd>
            <dt>Certificate</dt>
            <dd>
              {device.tlsThumbprint ? (
                <>
                  pinned <span className="mono" style={{ fontSize: 12 }}>SHA-256 {device.tlsThumbprint}</span>{' '}
                  {can('Admin') && (
                    <button className="small" onClick={() => setReset(true)} title="Accept a new certificate at the next connection (after a renewal)">
                      <KeyRound size={13} /> Reset
                    </button>
                  )}
                </>
              ) : (
                'trusted by a CA on this server (or not connected yet)'
              )}
            </dd>
          </dl>
        </div>
        {reset && (
          <Confirm
            title="Reset the pinned vCenter certificate?"
            message="The next connection accepts and pins the certificate the server presents. Only do this after a planned certificate renewal — an unexpected change can mean the connection is being intercepted."
            confirmLabel="Reset certificate"
            onClose={() => setReset(false)}
            onConfirm={() =>
              void (async () => {
                try {
                  await api.post(`/api/devices/${device.id}/tls-thumbprint/reset`);
                  await qc.invalidateQueries({ queryKey: ['device', device.id] });
                } catch (e) {
                  setError(e);
                }
                setReset(false);
              })()
            }
          />
        )}
      </div>
    );
  }

  const data = vm.data;
  if (!data) return null;
  const v = data.vm;
  return (
    <div className="card">
      <div className="card-head">
        <h3>VMware</h3>
        <span className="spacer" />
        <Link to="/vmware?tab=vms">All VMs →</Link>
      </div>
      <div className="card-body">
        <dl className="kv">
          <dt>Power</dt>
          <dd>{v.powerState}</dd>
          <dt>ESXi host</dt>
          <dd>{v.hostName ?? '—'}</dd>
          <dt>Guest OS</dt>
          <dd>{v.guestOs ?? '—'}</dd>
          <dt>vCPU / memory</dt>
          <dd>
            {v.numCpu} vCPU · {fmtBytes((v.memoryMb ?? 0) * 1048576)}
            {v.balloonedMb ? <span style={{ color: 'var(--warn)' }}> · {v.balloonedMb} MB ballooned</span> : null}
          </dd>
          <dt>CPU ready</dt>
          <dd style={{ color: (v.cpuReadyPct ?? 0) > 10 ? 'var(--crit)' : undefined }}>{v.cpuReadyPct != null ? `${fmtNum(v.cpuReadyPct, 1)}% per vCPU` : '—'}</dd>
          <dt>VMware Tools</dt>
          <dd>
            {v.toolsRunningStatus === 'guestToolsRunning' ? 'running' : 'not running'} <span className="muted">({v.toolsVersionStatus})</span>
          </dd>
          <dt>Storage</dt>
          <dd>
            {fmtBytes(v.committedBytes)} used on {v.datastores ?? '—'}
            {v.guestDiskMaxPct != null && <span className="muted"> · fullest guest disk {fmtNum(v.guestDiskMaxPct, 0)}%</span>}
          </dd>
          <dt>Snapshots</dt>
          <dd>
            {data.snapshots.length === 0
              ? 'none'
              : data.snapshots.map((s) => (
                  <div key={s.id}>
                    {s.name} — {fmtDateTime(s.createdUtc)} ({fmtNum(s.ageDays, 0)} days{s.sizeBytes != null ? `, ${fmtBytes(s.sizeBytes)}` : ''})
                  </div>
                ))}
          </dd>
        </dl>
      </div>
    </div>
  );
}
