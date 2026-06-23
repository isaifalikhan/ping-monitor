'use client';

import type { UseFormRegister } from 'react-hook-form';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select } from '@/components/ui/select';
import {
  CRITICALITY_LEVELS,
  SNMP_VERSIONS,
  type CreateMonitorFormData,
} from '@/lib/validations/monitor';

type Props = {
  register: UseFormRegister<CreateMonitorFormData>;
  showSnmp?: boolean;
};

export function MonitorDeviceFields({ register, showSnmp = true }: Props) {
  return (
    <div className="space-y-4 rounded-lg border p-4">
      <div>
        <h3 className="text-sm font-medium">Device details</h3>
        <p className="text-xs text-muted-foreground mt-1">
          Track location, ownership, and hardware info for switches, routers, firewalls, and servers.
          Use PING or TCP type with the management IP as the check target.
        </p>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <div className="space-y-2">
          <Label htmlFor="ipAddress">Management IP</Label>
          <Input id="ipAddress" {...register('ipAddress')} placeholder="10.0.1.50" />
        </div>
        <div className="space-y-2">
          <Label htmlFor="criticality">Criticality</Label>
          <Select id="criticality" {...register('criticality')}>
            {CRITICALITY_LEVELS.map((c) => (
              <option key={c} value={c}>{c}</option>
            ))}
          </Select>
        </div>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <div className="space-y-2">
          <Label htmlFor="deviceLocation">Location</Label>
          <Input id="deviceLocation" {...register('deviceLocation')} placeholder="DC1 · Row A" />
        </div>
        <div className="space-y-2">
          <Label htmlFor="rackCabinet">Rack / cabinet</Label>
          <Input id="rackCabinet" {...register('rackCabinet')} placeholder="R12-U24" />
        </div>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <div className="space-y-2">
          <Label htmlFor="switchPort">Switch port</Label>
          <Input id="switchPort" {...register('switchPort')} placeholder="Gi1/0/24" />
        </div>
        <div className="space-y-2">
          <Label htmlFor="deviceOwner">Device owner</Label>
          <Input id="deviceOwner" {...register('deviceOwner')} placeholder="Network team" />
        </div>
      </div>

      <div className="grid grid-cols-2 gap-4">
        <div className="space-y-2">
          <Label htmlFor="vendor">Vendor</Label>
          <Input id="vendor" {...register('vendor')} placeholder="Cisco" />
        </div>
        <div className="space-y-2">
          <Label htmlFor="model">Model</Label>
          <Input id="model" {...register('model')} placeholder="C9300-48P" />
        </div>
      </div>

      {showSnmp && (
        <div className="space-y-4 border-t pt-4">
          <div>
            <h4 className="text-sm font-medium text-muted-foreground">SNMP (future)</h4>
            <p className="text-xs text-muted-foreground mt-1">
              Stored for future interface and bandwidth monitoring. Not used by checks yet.
            </p>
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="snmpVersion">SNMP version</Label>
              <Select id="snmpVersion" {...register('snmpVersion')}>
                <option value="">—</option>
                {SNMP_VERSIONS.map((v) => (
                  <option key={v} value={v}>{v}</option>
                ))}
              </Select>
            </div>
            <div className="space-y-2">
              <Label htmlFor="snmpCommunity">SNMP community</Label>
              <Input id="snmpCommunity" {...register('snmpCommunity')} placeholder="Optional" />
            </div>
          </div>
          <div className="grid grid-cols-3 gap-4">
            <div className="space-y-2">
              <Label htmlFor="interfaceName">Interface name</Label>
              <Input id="interfaceName" {...register('interfaceName')} placeholder="Gi1/0/1" />
            </div>
            <div className="space-y-2">
              <Label htmlFor="interfaceStatus">Interface status</Label>
              <Input id="interfaceStatus" {...register('interfaceStatus')} placeholder="Future" disabled />
            </div>
            <div className="space-y-2">
              <Label htmlFor="bandwidthUsageMbps">Bandwidth (Mbps)</Label>
              <Input id="bandwidthUsageMbps" type="number" {...register('bandwidthUsageMbps')} placeholder="Future" disabled />
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
