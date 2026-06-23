/**
 * @jest-environment node
 */
import {
  accessTokenExpiresIn,
  clampSessionMinutes,
  getClientIp,
  isIpAllowed,
  mergePreferences,
  parseIpAllowlist,
  purgeExpiredCheckData,
  refreshTokenExpiresAt,
} from './enforce';
import { DEFAULT_SETTINGS_PREFS } from './types';

describe('settings enforce', () => {
  describe('parseIpAllowlist', () => {
    it('parses comma-separated IPs and trims whitespace', () => {
      expect(parseIpAllowlist(' 10.0.0.1 , 192.168.1.5 ')).toEqual([
        '10.0.0.1',
        '192.168.1.5',
      ]);
    });

    it('returns empty array for blank allowlist', () => {
      expect(parseIpAllowlist('  ,  ')).toEqual([]);
    });
  });

  describe('isIpAllowed', () => {
    it('allows all when allowlist is empty', () => {
      expect(isIpAllowed('203.0.113.9', '')).toBe(true);
    });

    it('allows only listed IPs', () => {
      const list = '10.0.0.1, 10.0.0.2';
      expect(isIpAllowed('10.0.0.1', list)).toBe(true);
      expect(isIpAllowed('10.0.0.3', list)).toBe(false);
    });
  });

  describe('getClientIp', () => {
    it('uses first x-forwarded-for address', () => {
      const headers = {
        get: (name: string) =>
          name === 'x-forwarded-for' ? '203.0.113.1, 10.0.0.1' : null,
      };
      expect(getClientIp(headers)).toBe('203.0.113.1');
    });

    it('falls back to x-real-ip then localhost', () => {
      expect(getClientIp({ get: () => null })).toBe('127.0.0.1');
    });
  });

  describe('session timeout helpers', () => {
    it('clamps session minutes between 15 and 1440', () => {
      expect(clampSessionMinutes(5)).toBe(15);
      expect(clampSessionMinutes(2000)).toBe(1440);
      expect(clampSessionMinutes(120)).toBe(120);
    });

    it('caps access token TTL at 60 minutes', () => {
      expect(accessTokenExpiresIn(480)).toBe('60m');
      expect(accessTokenExpiresIn(30)).toBe('30m');
    });

    it('computes refresh expiry from clamped minutes', () => {
      jest.useFakeTimers();
      jest.setSystemTime(new Date('2026-06-10T12:00:00.000Z'));
      const expires = refreshTokenExpiresAt(60);
      expect(expires.toISOString()).toBe('2026-06-10T13:00:00.000Z');
      jest.useRealTimers();
    });
  });

  describe('mergePreferences', () => {
    it('merges partial prefs with defaults', () => {
      const merged = mergePreferences({
        retentionDays: 30,
        brandPrimaryColor: '#ff0000',
      });
      expect(merged.retentionDays).toBe(30);
      expect(merged.brandPrimaryColor).toBe('#ff0000');
      expect(merged.defaultCheckInterval).toBe(
        DEFAULT_SETTINGS_PREFS.defaultCheckInterval,
      );
    });

    it('falls back to defaults for invalid numbers', () => {
      const merged = mergePreferences({ retentionDays: 0, defaultTimeout: NaN });
      expect(merged.retentionDays).toBe(DEFAULT_SETTINGS_PREFS.retentionDays);
      expect(merged.defaultTimeout).toBe(DEFAULT_SETTINGS_PREFS.defaultTimeout);
    });
  });

  describe('purgeExpiredCheckData', () => {
    it('deletes checks older than retention per organization', async () => {
      jest.useFakeTimers();
      jest.setSystemTime(new Date('2026-06-10T12:00:00.000Z'));

      const selectChecks = jest.fn().mockResolvedValue({
        data: [{ id: 'c1' }, { id: 'c2' }],
      });
      const lt = jest.fn().mockReturnValue({ select: selectChecks });
      const eq = jest.fn().mockReturnValue({ lt });
      const deleteFn = jest.fn().mockReturnValue({ eq });

      const db = {
        from: jest.fn((table: string) => {
          if (table === 'organization_settings') {
            return {
              select: jest.fn().mockResolvedValue({
                data: [
                  {
                    organization_id: 'org-1',
                    preferences: { retentionDays: 90 },
                  },
                ],
              }),
            };
          }
          if (table === 'monitor_checks') {
            return { delete: deleteFn };
          }
          throw new Error(`unexpected table ${table}`);
        }),
      };

      const result = await purgeExpiredCheckData(db as never);
      expect(result.purged).toBe(2);
      expect(eq).toHaveBeenCalledWith('organization_id', 'org-1');
      expect(lt).toHaveBeenCalledWith(
        'created_at',
        new Date('2026-03-12T12:00:00.000Z').toISOString(),
      );

      jest.useRealTimers();
    });
  });
});
