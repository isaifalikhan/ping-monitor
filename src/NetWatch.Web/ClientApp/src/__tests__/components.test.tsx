import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import type { GpuInfo, HardwareComponentInfo } from '../api/types';
import { SeverityBadge } from '../components/Badges';
import { GpuPanel, HardwarePanel, healthClass } from '../pages/devices/HardwarePanels';

const gpu = (over: Partial<GpuInfo> = {}): GpuInfo => ({
  index: 0,
  name: 'NVIDIA A10',
  vendor: 'NVIDIA',
  driverVersion: '551.78',
  utilizationPct: 40,
  memoryUsedMb: 8192,
  memoryTotalMb: 24576,
  temperatureC: 60,
  powerW: 90,
  fanPct: 35,
  problem: null,
  lastSeenUtc: '2026-11-21T10:00:00Z',
  ...over,
});

describe('GPU panel', () => {
  it('is not rendered for servers without a GPU', () => {
    expect(renderToStaticMarkup(<GpuPanel gpus={[]} />)).toBe('');
  });

  it('shows values and highlights problems', () => {
    const html = renderToStaticMarkup(<GpuPanel gpus={[gpu(), gpu({ index: 1, temperatureC: 91, problem: 'device problem code 43' })]} />);
    expect(html).toContain('NVIDIA A10');
    expect(html).toContain('8.0 / 24 GB');
    expect(html).toContain('91 °C');
    expect(html).toContain('device problem code 43');
    expect(html).toContain('2 GPU(s)');
  });
});

describe('hardware health panel', () => {
  const c = (category: string, name: string, health: string | null, state = 'Enabled', reading?: number, unit?: string): HardwareComponentInfo => ({
    source: 'Redfish',
    category,
    name,
    health,
    state,
    reading,
    unit,
    lastSeenUtc: '2026-11-21T10:00:00Z',
  });

  it('counts problems, ignores absent parts and groups by category', () => {
    const html = renderToStaticMarkup(
      <HardwarePanel components={[c('Fan', 'Fan 1', 'OK'), c('Fan', 'Fan 2', 'Critical'), c('Fan', 'Fan 7', 'Critical', 'Absent'), c('Temperature', 'Inlet', 'OK', 'Enabled', 22, 'C')]} />,
    );
    expect(html).toContain('1 problem(s)');
    expect(html).toContain('Fans');
    expect(html).toContain('Temperatures');
    expect(html).toContain('22 °C');
    expect(html).toContain('absent');
  });

  it('maps health to status colours', () => {
    expect(healthClass('OK')).toBe('s-up');
    expect(healthClass('Warning')).toBe('s-warning');
    expect(healthClass('Critical')).toBe('s-down');
    expect(healthClass('Critical', 'Absent')).toBe('s-unknown');
    expect(healthClass(null)).toBe('s-unknown');
  });
});

describe('badges', () => {
  it('renders the severity', () => {
    expect(renderToStaticMarkup(<SeverityBadge severity="Critical" />)).toContain('Critical');
  });
});
