/**
 * @jest-environment node
 */
import { createMonitorApiSchema } from './api-monitor';

describe('createMonitorApiSchema', () => {
  it('accepts a minimal HTTPS monitor', () => {
    const result = createMonitorApiSchema.safeParse({
      name: 'Corporate site',
      type: 'HTTPS',
      target: 'https://example.com',
    });
    expect(result.success).toBe(true);
    if (result.success) {
      expect(result.data.checkInterval).toBe(60);
      expect(result.data.timeout).toBe(30);
    }
  });

  it('rejects invalid port and monitor type', () => {
    const portResult = createMonitorApiSchema.safeParse({
      name: 'DB',
      type: 'TCP',
      target: 'db.internal',
      port: 70000,
    });
    expect(portResult.success).toBe(false);

    const typeResult = createMonitorApiSchema.safeParse({
      name: 'Unknown',
      type: 'FOOBAR',
      target: 'x',
    });
    expect(typeResult.success).toBe(false);
  });
});
