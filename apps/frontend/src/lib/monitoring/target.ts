/** Default ports when the port column is not set */
const DEFAULT_PORTS: Record<string, number> = {
  PING: 80,
  TCP: 80,
  SSL: 443,
};

/**
 * Build the target string passed to the check executor from stored monitor fields.
 */
export function resolveCheckTarget(type: string, target: string, port?: number | null): string {
  const trimmed = target.trim();
  if (!trimmed) return trimmed;

  if (type === 'DNS') {
    return trimmed.replace(/^https?:\/\//, '').split('/')[0].split(':')[0];
  }

  if (type === 'SSL') {
    const host = trimmed.replace(/^https?:\/\//, '').split('/')[0].split(':')[0];
    const p = port ?? DEFAULT_PORTS.SSL;
    return `${host}:${p}`;
  }

  if (type === 'TCP' || type === 'PING') {
    if (trimmed.includes(':') && !trimmed.startsWith('http')) {
      return trimmed;
    }
    const host = trimmed.replace(/^https?:\/\//, '').split('/')[0].split(':')[0];
    const p = port ?? DEFAULT_PORTS[type] ?? 80;
    return `${host}:${p}`;
  }

  if ((type === 'HTTP' || type === 'HTTPS' || type === 'API') && port) {
    try {
      const withScheme = trimmed.startsWith('http') ? trimmed : `http://${trimmed}`;
      const url = new URL(withScheme);
      if (!url.port) {
        url.port = String(port);
        return url.toString();
      }
    } catch {
      /* use trimmed target as-is */
    }
  }

  return trimmed;
}
