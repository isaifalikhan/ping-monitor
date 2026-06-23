/**
 * @jest-environment node
 */
import { resolveCheckTarget } from './target';

describe('resolveCheckTarget', () => {
  it('strips scheme and path for DNS checks', () => {
    expect(resolveCheckTarget('DNS', 'https://api.example.com/health')).toBe(
      'api.example.com',
    );
  });

  it('appends default SSL port', () => {
    expect(resolveCheckTarget('SSL', 'mail.example.com')).toBe('mail.example.com:443');
    expect(resolveCheckTarget('SSL', 'mail.example.com', 8443)).toBe(
      'mail.example.com:8443',
    );
  });

  it('appends host:port for TCP when port column is set', () => {
    expect(resolveCheckTarget('TCP', 'db.internal', 5432)).toBe('db.internal:5432');
  });

  it('preserves explicit host:port for TCP', () => {
    expect(resolveCheckTarget('TCP', '10.0.0.5:22')).toBe('10.0.0.5:22');
  });

  it('injects custom port into HTTP URLs', () => {
    expect(resolveCheckTarget('HTTP', 'status.example.com', 8080)).toBe(
      'http://status.example.com:8080/',
    );
  });
});
