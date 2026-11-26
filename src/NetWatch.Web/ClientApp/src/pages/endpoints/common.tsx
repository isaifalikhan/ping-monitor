import type { EndpointListItem } from '../../api/types';
import { toMs } from '../../util/format';

/** Change kinds recorded between two inventories. */
export const CHANGE_LABELS: Record<string, string> = {
  enrolled: 'Enrolled',
  softwareInstalled: 'Software installed',
  softwareRemoved: 'Software removed',
  softwareUpdated: 'Software updated',
  localAdminAdded: 'Local admin added',
  localAdminRemoved: 'Local admin removed',
  localUserCreated: 'Local user created',
  localUserDeleted: 'Local user deleted',
  serviceInstalled: 'Service installed',
  serviceRemoved: 'Service removed',
  startupAdded: 'Startup program added',
  startupRemoved: 'Startup program removed',
  portOpened: 'Listening port opened',
  portClosed: 'Listening port closed',
  usbStorage: 'USB storage',
  usbDevice: 'USB device',
  firewallDisabled: 'Firewall turned off',
  firewallEnabled: 'Firewall turned on',
  updateInstalled: 'Update installed',
  osUpdated: 'OS build changed',
  fileCreated: 'File created',
  executableAdded: 'Program/script file added',
  fileModified: 'File modified',
  fileDeleted: 'File deleted',
  hostsModified: 'Hosts file changed',
};

/** Changes shown in red/amber in the change lists. */
export const RISKY_CHANGES = new Set(['localAdminAdded', 'usbStorage', 'firewallDisabled', 'localUserCreated', 'serviceInstalled', 'startupAdded', 'softwareInstalled', 'executableAdded', 'hostsModified']);

export function ChangeBadge({ kind }: { kind: string }) {
  const cls = kind === 'localAdminAdded' || kind === 'firewallDisabled' || kind === 'hostsModified' ? 's-down' : RISKY_CHANGES.has(kind) ? 's-warning' : 's-unknown';
  return <span className={`badge ${cls}`}>{CHANGE_LABELS[kind] ?? kind}</span>;
}

/** Workstation IPs in 0.0.0.0/8 are placeholders (address unknown / taken by another machine). */
export function ipText(ip?: string | null): string {
  return !ip || ip.startsWith('0.') ? '—' : ip;
}

export function shortOs(os?: string | null): string {
  return os ? os.replace(/^Microsoft\s+/i, '') : '—';
}

export function daysSince(utc?: string | null): number | null {
  const ms = toMs(utc);
  return Number.isFinite(ms) ? Math.floor((Date.now() - ms) / 86400e3) : null;
}

/** Problems of a machine, most important first (for the list's "Problems" column and filter). */
export function problems(e: EndpointListItem): string[] {
  const list: string[] = [];
  if (e.stale) list.push('not reporting');
  if (e.firewallOff) list.push(`firewall off (${e.firewallOff})`);
  if (e.avProblem) list.push(e.avProblem);
  if ((e.missingUpdates ?? 0) > 0) list.push(`${e.missingUpdates} security updates missing`);
  const age = daysSince(e.lastPatchUtc);
  if (age != null && age > 45) list.push(`not patched for ${age} days`);
  if ((e.worstDiskPct ?? 0) >= 90) list.push(`disk ${Math.round(e.worstDiskPct!)} % full`);
  return list;
}

export function YesNo({ value, good = true }: { value: boolean | null | undefined; good?: boolean }) {
  if (value == null) return <span className="faint">—</span>;
  const ok = value === good;
  return <span style={{ color: ok ? 'var(--ok)' : 'var(--crit)' }}>{value ? 'Yes' : 'No'}</span>;
}
