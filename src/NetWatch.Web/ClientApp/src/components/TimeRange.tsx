import { useState } from 'react';

export type RangeKey = '1h' | '24h' | '7d' | '30d' | 'custom';

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
};

export function rangeFor(key: Exclude<RangeKey, 'custom'>): TimeRangeValue {
  const to = new Date();
  return { key, from: new Date(to.getTime() - spans[key]), to };
}

function toLocalInput(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** 1h / 24h / 7d / 30d / custom range selector. */
export function TimeRange({ value, onChange }: { value: TimeRangeValue; onChange: (v: TimeRangeValue) => void }) {
  const [customFrom, setCustomFrom] = useState(toLocalInput(new Date(Date.now() - 3 * 3600e3)));
  const [customTo, setCustomTo] = useState(toLocalInput(new Date()));

  return (
    <div className="toolbar">
      <div className="seg">
        {(['1h', '24h', '7d', '30d'] as const).map((k) => (
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
