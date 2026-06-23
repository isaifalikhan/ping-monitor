/**
 * @jest-environment node
 */
import { executeCheck, isServerlessCheckType } from './executor';

describe('monitor executor', () => {
  describe('isServerlessCheckType', () => {
    it('recognizes supported check types', () => {
      for (const type of ['HTTP', 'HTTPS', 'API', 'TCP', 'DNS', 'SSL', 'PING']) {
        expect(isServerlessCheckType(type)).toBe(true);
      }
    });

    it('rejects unsupported types', () => {
      expect(isServerlessCheckType('SNMP')).toBe(false);
      expect(isServerlessCheckType('CUSTOM')).toBe(false);
    });
  });

  describe('executeCheck', () => {
    it('returns DOWN for unsupported monitor types without network I/O', async () => {
      const result = await executeCheck('SNMP', '10.0.0.1', 5);
      expect(result).toEqual({
        status: 'DOWN',
        responseTime: null,
        errorMessage: 'Unsupported type: SNMP',
      });
    });
  });
});
