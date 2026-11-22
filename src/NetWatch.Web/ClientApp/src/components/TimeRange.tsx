import { useState } from 'react';

export type RangeKey = '1h' | '24h' | '7d' | '30d' | '90d' | '1y' | 'custom';

/** Presets for graphs (default) and for searches of stored events and alerts (up to the retention period). */
export const GRAPH_RANGES = ['1h', '24h', '7d', '30d'] as const;
export const SEARCH_RANGES = ['1h', '24h', '7d', '30d', '90d', '1y'] as const;

export interface TimeRangeValue {
  key: RangeKey;
  from: Date;
  to: Date;
}

const spans: Record<Exclude<RangeKey, 'custom'>, number> = {
  '1h': 3600e3,
  '24h': 24 * 3600e3,
  '7d': 7 * 24 * 3600e3,
  '30d': 30 * 24 * 3600e3,
  '90d': 90 * 24 * 3600e3,
  '1y': 365 * 24 * 3600e3,
};

export function rangeFor(key: Exclude<RangeKey, 'custom'>): TimeRangeValue {
  const to = new Date();
  return { key, from: new Date(to.getTime() - spans[key]), to };
}

function toLocalInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Preset (1h / 24h / 7d / 30d, optionally 90d / 1y) or custom range selector. */
export function TimeRange({
  value,
  onChange,
  presets = GRAPH_RANGES,
}: {
  value: TimeRangeValue;
  onChange: (v: TimeRangeValue) => void;
  presets?: readonly Exclude<RangeKey, 'custom'>[];
}) {
  const [customFrom, setCustomFrom] = useState(toLocalInput(new Date(Date.now() - 3 * 3600e3)));
  const [customTo, setCustomTo] = useState(toLocalInput(new Date()));

  return (
    <div className="toolbar">
      <div className="seg">
        {presets.map((k) => (
          <button key={k} className={value.key === k ? 'active' : ''} onClick={() => onChange(rangeFor(k))}>
            {k}
          </button>
        ))}
        <button className={value.key === 'custom' ? 'active' : ''} onClick={() => onChange({ key: 'custom', from: new Date(customFrom), to: new Date(customTo) })}>
          Custom
        </button>
      </div>
      {value.key === 'custom' && (
        <>
          <input type="datetime-local" value={customFrom} onChange={(e) => setCustomFrom(e.target.value)} aria-label="From" />
          <span className="muted">to</span>
          <input type="datetime-local" value={customTo} onChange={(e) => setCustomTo(e.target.value)} aria-label="To" />
          <button className="small" onClick={() => onChange({ key: 'custom', from: new Date(customFrom), to: new Date(customTo) })}>
            Apply
          </button>
        </>
      )}
    </div>
  );
}
