import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../../api/client';
import type { Credential, DeviceDetail, DeviceInput, DeviceListItem, Group } from '../../api/types';
import { DEVICE_TYPES, OS_KINDS } from '../../api/types';
import { ErrorBox, Modal } from '../../components/Modal';
import { typeLabel } from '../../util/format';
import { groupOptions } from '../settings/GroupsTab';

/** Add/edit device dialog (Admin). */
export function DeviceForm({ device, onClose, onSaved }: { device?: DeviceDetail; onClose: () => void; onSaved?: (id: number) => void }) {
  const qc = useQueryClient();
  const groups = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });
  const creds = useQuery({ queryKey: ['credentials'], queryFn: () => api.get<Credential[]>('/api/credentials') });
  const devices = useQuery({ queryKey: ['devices', 'all'], queryFn: () => api.get<DeviceListItem[]>('/api/devices') });

  const [form, setForm] = useState<DeviceInput>(() => ({
    name: device?.name ?? '',
    ipAddress: device?.ipAddress ?? '',
    hostname: device?.hostname ?? '',
    type: device?.type ?? 'Switch',
    os: device?.os ?? 'NetworkOs',
    location: device?.location ?? '',
    groupId: device?.groupId ?? null,
    pollIntervalSec: device?.pollIntervalSec ?? 60,
    isEnabled: device?.isEnabled ?? true,
    parentDeviceId: device?.parentDeviceId ?? null,
    snmpCredentialId: device?.credentials.find((c) => c.purpose === 'Snmp')?.credentialProfileId ?? null,
    managementCredentialId: device?.credentials.find((c) => c.purpose === 'Management')?.credentialProfileId ?? null,
    vcenterCredentialId: device?.credentials.find((c) => c.purpose === 'VCenter')?.credentialProfileId ?? null,
    bmcAddress: device?.bmcAddress ?? '',
    bmcCredentialId: device?.credentials.find((c) => c.purpose === 'Bmc')?.credentialProfileId ?? null,
    notes: device?.notes ?? '',
    tags: device?.tags ?? [],
  }));
  const [tagText, setTagText] = useState((device?.tags ?? []).join(', '));
  const [servicesText, setServicesText] = useState((device?.watchedServices ?? []).join(', '));
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const set = <K extends keyof DeviceInput>(k: K, v: DeviceInput[K]) => setForm((f) => ({ ...f, [k]: v }));

  const save = async () => {
    setBusy(true);
    setError(null);
    const body: DeviceInput = {
      ...form,
      tags: tagText
        .split(/[,;]/)
        .map((t) => t.trim())
        .filter(Boolean),
      watchedServices: servicesText
        .split(/[,;\n]/)
        .map((t) => t.trim())
        .filter(Boolean),
    };
    try {
      let id = device?.id;
      if (device) await api.put(`/api/devices/${device.id}`, body);
      else id = (await api.post<{ id: number }>('/api/devices', body)).id;
      await qc.invalidateQueries({ queryKey: ['devices'] });
      await qc.invalidateQueries({ queryKey: ['device'] });
      onSaved?.(id!);
      onClose();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const snmpCreds = (creds.data ?? []).filter((c) => c.kind === 'SnmpV2c' || c.kind === 'SnmpV3');
  const managementCreds = (creds.data ?? []).filter((c) => c.kind === 'Windows' || c.kind === 'Ssh');
  const vcenterCreds = (creds.data ?? []).filter((c) => c.kind === 'VCenter');
  const redfishCreds = (creds.data ?? []).filter((c) => c.kind === 'Redfish');
  const vmwareType = form.type === 'VCenter' || form.type === 'Hypervisor';
  const serverType = ['Server', 'VM', 'DomainController', 'Hypervisor'].includes(form.type);
  const switchType = form.type === 'Switch' || form.type === 'Router';
  const sshCreds = (creds.data ?? []).filter((c) => c.kind === 'Ssh');

  return (
    <Modal
      title={device ? `Edit ${device.name}` : 'Add device'}
      onClose={onClose}
      wide
      footer={
        <>
          <button onClick={onClose}>Cancel</button>
          <button className="primary" onClick={() => void save()} disabled={busy}>
            {device ? 'Save' : 'Add device'}
          </button>
        </>
      }
    >
      <ErrorBox error={error} />
      <div className="form-grid">
        <label className="field">
          <span>Name *</span>
          <input value={form.name} onChange={(e) => set('name', e.target.value)} maxLength={200} />
        </label>
        <label className="field">
          <span>IP address *</span>
          <input className="mono" value={form.ipAddress} onChange={(e) => set('ipAddress', e.target.value)} placeholder="10.0.0.1" />
        </label>
        <label className="field">
          <span>Host name</span>
          <input value={form.hostname ?? ''} onChange={(e) => set('hostname', e.target.value)} />
        </label>
        <label className="field">
          <span>Type</span>
          <select value={form.type} onChange={(e) => set('type', e.target.value as DeviceInput['type'])}>
            {DEVICE_TYPES.map((t) => (
              <option key={t} value={t}>
                {typeLabel(t)}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Operating system</span>
          <select value={form.os} onChange={(e) => set('os', e.target.value as DeviceInput['os'])}>
            {OS_KINDS.map((t) => (
              <option key={t} value={t}>
                {typeLabel(t)}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Group / site</span>
          <select value={form.groupId ?? ''} onChange={(e) => set('groupId', e.target.value ? Number(e.target.value) : null)}>
            <option value="">— none —</option>
            {groupOptions(groups.data ?? []).map((g) => (
              <option key={g.id} value={g.id}>
                {g.label}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Location / rack</span>
          <input value={form.location ?? ''} onChange={(e) => set('location', e.target.value)} />
        </label>
        <label className="field">
          <span>SNMP credential</span>
          <select value={form.snmpCredentialId ?? ''} onChange={(e) => set('snmpCredentialId', e.target.value ? Number(e.target.value) : null)}>
            <option value="">— none (ping only) —</option>
            {snmpCreds.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name} ({c.kind === 'SnmpV3' ? 'v3' : 'v2c'})
              </option>
            ))}
          </select>
        </label>
        {(vmwareType || form.vcenterCredentialId) && (
          <label className="field">
            <span>vCenter / ESXi credential</span>
            <select value={form.vcenterCredentialId ?? ''} onChange={(e) => set('vcenterCredentialId', e.target.value ? Number(e.target.value) : null)}>
              <option value="">— none —</option>
              {vcenterCreds.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
            <span className="hint">Collects hosts, VMs, datastores, snapshots, alarms and events through the vSphere API (read-only role).</span>
          </label>
        )}
        {(form.type === 'Server' || form.type === 'Hypervisor' || form.bmcAddress) && (
          <>
            <label className="field">
              <span>iLO / iDRAC address</span>
              <input className="mono" value={form.bmcAddress ?? ''} onChange={(e) => set('bmcAddress', e.target.value)} placeholder="10.0.200.21" maxLength={255} />
              <span className="hint">Hardware health (fans, PSUs, temperatures, memory, RAID, drives) through Redfish; without a Redfish credential the HPE/Dell SNMP agent is asked instead.</span>
            </label>
            <label className="field">
              <span>iLO / iDRAC credential</span>
              <select value={form.bmcCredentialId ?? ''} onChange={(e) => set('bmcCredentialId', e.target.value ? Number(e.target.value) : null)}>
                <option value="">— none (SNMP fallback) —</option>
                {redfishCreds.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>
              <span className="hint">A read-only iLO / iDRAC account (Settings → Credentials → iLO / iDRAC).</span>
            </label>
          </>
        )}
        {switchType && (
          <label className="field">
            <span>SSH credential (CLI)</span>
            <select value={form.managementCredentialId ?? ''} onChange={(e) => set('managementCredentialId', e.target.value ? Number(e.target.value) : null)}>
              <option value="">— none —</option>
              {sshCreds.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name} (SSH)
                </option>
              ))}
            </select>
            <span className="hint">Switch admin user for adding/removing static MAC addresses on ports (TP-Link, Juniper). Same login as in PuTTY.</span>
          </label>
        )}
        {!switchType && (serverType || form.managementCredentialId) && (
          <label className="field">
            <span>Windows / SSH credential</span>
            <select value={form.managementCredentialId ?? ''} onChange={(e) => set('managementCredentialId', e.target.value ? Number(e.target.value) : null)}>
              <option value="">— none (ping/SNMP only) —</option>
              {managementCreds.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name} ({c.kind === 'Ssh' ? 'SSH' : 'Windows'})
                </option>
              ))}
            </select>
            <span className="hint">Disks, services, patches, event logs (WinRM/WMI) or Linux stats (SSH).</span>
          </label>
        )}
        {(serverType || servicesText) && (
          <label className="field">
            <span>Watched services</span>
            <input value={servicesText} onChange={(e) => setServicesText(e.target.value)} placeholder="W3SVC, MSSQLSERVER  or  sshd, nginx" />
            <span className="hint">Comma separated service names (Windows) or systemd units (Linux).</span>
          </label>
        )}
        <label className="field">
          <span>Polling interval (seconds)</span>
          <input type="number" min={15} max={3600} value={form.pollIntervalSec ?? 60} onChange={(e) => set('pollIntervalSec', Number(e.target.value))} />
        </label>
        <label className="field">
          <span>Upstream device (dependency)</span>
          <select value={form.parentDeviceId ?? ''} onChange={(e) => set('parentDeviceId', e.target.value ? Number(e.target.value) : null)}>
            <option value="">— none —</option>
            {(devices.data ?? [])
              .filter((d) => d.id !== device?.id && ['Switch', 'Router', 'Firewall'].includes(d.type))
              .map((d) => (
                <option key={d.id} value={d.id}>
                  {d.name} ({d.ipAddress})
                </option>
              ))}
          </select>
          <span className="hint">The switch this device connects through (dependency map).</span>
        </label>
        <label className="field">
          <span>Tags</span>
          <input value={tagText} onChange={(e) => setTagText(e.target.value)} placeholder="core, cisco" />
          <span className="hint">Comma separated.</span>
        </label>
        <label className="check" style={{ alignSelf: 'end' }}>
          <input type="checkbox" checked={form.isEnabled} onChange={(e) => set('isEnabled', e.target.checked)} />
          Monitoring enabled
        </label>
        <label className="field wide">
          <span>Notes</span>
          <textarea rows={2} value={form.notes ?? ''} onChange={(e) => set('notes', e.target.value)} />
        </label>
      </div>
    </Modal>
  );
}
