import { useMemo } from 'react';
import type { Series } from '../api/types';
import { fmtShort, fmtTime, TIME_ZONE } from '../util/format';
import { Chart, cssVar, type EChartsOption } from './Chart';

export interface SeriesSpec {
  /** Column in the series (camelCase, e.g. "inBps"). */
  column: string;
  label: string;
  color?: string;
  /** Multiplier applied before plotting (e.g. 100 for 0..1 ratios). */
  scale?: number;
  area?: boolean;
}

interface Props {
  data: Series | undefined;
  series: SeriesSpec[];
  formatValue: (v: number) => string;
  height?: number;
  yMax?: number;
  showPeak?: boolean;
  threshold?: number;
}

const hourFmt = new Intl.DateTimeFormat('en-GB', { timeZone: TIME_ZONE, hour: '2-digit', minute: '2-digit', hour12: false });
const dayFmt = new Intl.DateTimeFormat('en-GB', { timeZone: TIME_ZONE, day: '2-digit', month: 'short' });

/** Time-series line chart for metrics returned by /metrics endpoints (raw or rolled-up). */
export function MetricChart({ data, series, formatValue, height = 240, yMax, showPeak, threshold }: Props) {
  const option = useMemo<EChartsOption>(() => {
    const palette = [cssVar('--chart-1'), cssVar('--chart-2'), cssVar('--chart-3'), cssVar('--chart-4')];
    const text = cssVar('--text-muted');
    const grid = cssVar('--chart-grid');
    const ts = data?.ts ?? [];
    const span = ts.length > 1 ? ts[ts.length - 1] - ts[0] : 0;
    const multiDay = span > 36 * 3600 * 1000;

    // Canvas cannot resolve CSS variables, so "var(--x)" colours are looked up here.
    const resolve = (c: string) => (c.startsWith('var(') ? cssVar(c.slice(4, -1)) : c);
    const lines = series.flatMap((s, i) => {
      const color = resolve(s.color ?? palette[i % palette.length]);
      const values = data?.avg[s.column] ?? [];
      const scale = s.scale ?? 1;
      const avgLine = {
        name: s.label,
        type: 'line',
        showSymbol: false,
        sampling: 'lttb',
        connectNulls: false,
        lineStyle: { width: 1.6, color },
        itemStyle: { color },
        areaStyle: s.area ? { opacity: 0.12, color } : undefined,
        data: ts.map((t, idx) => [t, values[idx] === null || values[idx] === undefined ? null : (values[idx] as number) * scale]),
        markLine:
          threshold !== undefined && i === 0
            ? { silent: true, symbol: 'none', lineStyle: { type: 'dashed', color: cssVar('--crit') }, data: [{ yAxis: threshold }], label: { show: false } }
            : undefined,
      };
      const maxValues = data?.max?.[s.column];
      if (!showPeak || !maxValues) return [avgLine];
      return [
        avgLine,
        {
          name: `${s.label} (peak)`,
          type: 'line',
          showSymbol: false,
          sampling: 'lttb',
          lineStyle: { width: 1, type: 'dotted', color },
          itemStyle: { color },
          data: ts.map((t, idx) => [t, maxValues[idx] === null || maxValues[idx] === undefined ? null : (maxValues[idx] as number) * scale]),
        },
      ];
    });

    return {
      animation: false,
      grid: { left: 64, right: 16, top: 30, bottom: 28 },
      legend: { top: 0, right: 0, textStyle: { color: text }, itemWidth: 14, itemHeight: 8 },
      tooltip: {
        trigger: 'axis',
        backgroundColor: cssVar('--bg-elev'),
        borderColor: cssVar('--border'),
        textStyle: { color: cssVar('--text') },
        valueFormatter: (v: unknown) => (typeof v === 'number' ? formatValue(v) : '—'),
        axisPointer: { type: 'line' },
        formatter: undefined,
      },
      xAxis: {
        type: 'time',
        axisLine: { lineStyle: { color: grid } },
        axisLabel: {
          color: text,
          hideOverlap: true,
          formatter: (v: number) => (multiDay ? dayFmt.format(new Date(v)) : hourFmt.format(new Date(v))),
        },
        axisPointer: { label: { formatter: (p: { value: number }) => (multiDay ? fmtShort(p.value) : fmtTime(p.value)) } },
        splitLine: { show: false },
      },
      yAxis: {
        type: 'value',
        min: 0,
        max: yMax,
        axisLabel: { color: text, formatter: (v: number) => formatValue(v) },
        splitLine: { lineStyle: { color: grid } },
      },
      series: lines,
    };
  }, [data, series, formatValue, yMax, showPeak, threshold]);

  if (data && data.ts.length === 0) {
    return (
      <div className="empty" style={{ height }}>
        No data for this period yet.
      </div>
    );
  }
  return <Chart option={option} height={height} />;
}
