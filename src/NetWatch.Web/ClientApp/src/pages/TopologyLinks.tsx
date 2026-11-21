import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { GraphChart } from 'echarts/charts';
import { TooltipComponent } from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import type { DependencySuggestion, DeviceStatus, TopologyGraph, TopologyLink } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBox } from '../components/Modal';
import { fmtSpeed, typeLabel } from '../util/format';

echarts.use([GraphChart, TooltipComponent, CanvasRenderer]);

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

export function nodeColor(status: DeviceStatus): string {
  return status === 'Down' ? cssVar('--crit', '#e5484d') : status === 'Warning' ? cssVar('--warn', '#f5a524') : status === 'Up' ? cssVar('--ok', '#30a46c') : cssVar('--text-faint', '#7b8794');
}

/** Link colour: down = red, ≥ 80 % = amber, up = green, unknown = grey. */
export function linkColor(l: TopologyLink): string {
  if (l.operStatus != null && l.operStatus !== 1) return cssVar('--crit', '#e5484d');
  if ((l.utilPct ?? 0) >= 80) return cssVar('--warn', '#f5a524');
  if (l.operStatus === 1) return cssVar('--ok', '#30a46c');
  return cssVar('--border-strong', '#3a4656');
}

/** Physical topology from LLDP/CDP neighbours, plus dependency suggestions derived from it. */
export function TopologyLinksView() {
  const navigate = useNavigate();
  const { can } = useAuth();
  const qc = useQueryClient();
  const ref = useRef<HTMLDivElement>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const q = useQuery({ queryKey: ['topology', 'links'], queryFn: () => api.get<TopologyGraph>('/api/topology/links'), refetchInterval: 30000 });
  const s = useQuery({ queryKey: ['topology', 'suggestions'], queryFn: () => api.get<DependencySuggestion[]>('/api/topology/suggestions') });
  const [picked, setPicked] = useState<Set<number>>(new Set());
  const apply = useMutation({
    mutationFn: (ids: number[]) => api.post<{ applied: number }>('/api/topology/suggestions/apply', { deviceIds: ids }),
    onSuccess: () => {
      setPicked(new Set());
      void qc.invalidateQueries({ queryKey: ['topology'] });
    },
  });

  const option = useMemo(() => {
    if (!q.data) return null;
    const names = new Map(q.data.nodes.map((n) => [n.id, n.name]));
    const external = new Map<string, string>();
    for (const l of q.data.links) if (l.remoteDeviceId == null && l.remoteName) external.set(`x:${l.remoteName}`, l.remoteName);
    return {
      tooltip: {
        formatter: (p: { dataType: string; data: { tip?: string } }) => p.data.tip ?? '',
      },
      series: [
        {
          type: 'graph',
          layout: 'force',
          roam: true,
          draggable: true,
          force: { repulsion: 120, edgeLength: [40, 110], gravity: 0.08 },
          label: { show: q.data.nodes.length <= 80, position: 'right', fontSize: 10, color: cssVar('--text', '#e6ebf1') },
          data: [
            ...q.data.nodes.map((n) => ({
              id: String(n.id),
              name: n.name,
              symbolSize: n.type === 'Switch' || n.type === 'Router' ? 14 : 9,
              itemStyle: { color: nodeColor(n.status) },
              tip: `<b>${n.name}</b><br/>${n.ipAddress} · ${typeLabel(n.type)}<br/>${n.status}${n.worstAlert ? ` · ${n.worstAlert} alert` : ''}<br/><i>Double-click to open</i>`,
              deviceId: n.id,
            })),
            ...[...external].map(([id, name]) => ({ id, name, symbol: 'rect', symbolSize: 8, itemStyle: { color: cssVar('--text-faint', '#7b8794') }, tip: `${name}<br/>(not in the inventory)` })),
          ],
          links: q.data.links.map((l) => ({
            source: String(l.deviceId),
            target: l.remoteDeviceId != null ? String(l.remoteDeviceId) : `x:${l.remoteName}`,
            lineStyle: { color: linkColor(l), width: (l.speedBps ?? 0) >= 10e9 ? 2.5 : 1.2 },
            tip: `${names.get(l.deviceId) ?? '?'} ${l.localPort} ↔ ${l.remoteDeviceId != null ? names.get(l.remoteDeviceId) : l.remoteName} ${l.remotePort ?? ''}<br/>${l.protocol}${l.speedBps ? ` · ${fmtSpeed(l.speedBps)}bps` : ''}${l.utilPct != null ? ` · ${Math.round(l.utilPct)} %` : ''}${l.operStatus != null && l.operStatus !== 1 ? ' · DOWN' : ''}`,
          })),
        },
      ],
    };
  }, [q.data]);

  useEffect(() => {
    if (!ref.current) return;
    chart.current = echarts.init(ref.current, undefined, { renderer: 'canvas' });
    const ro = new ResizeObserver(() => chart.current?.resize());
    ro.observe(ref.current);
    chart.current.on('dblclick', (p) => {
      const id = (p.data as { deviceId?: number } | undefined)?.deviceId;
      if (id) navigate(`/devices/${id}`);
    });
    return () => {
      ro.disconnect();
      chart.current?.dispose();
      chart.current = null;
    };
  }, [navigate]);

  useEffect(() => {
    if (option && chart.current) chart.current.setOption(option, { notMerge: true });
  }, [option]);

  const suggestions = s.data ?? [];
  return (
    <>
      <ErrorBox error={q.error ?? s.error ?? apply.error} />
      <div className="card" style={{ padding: 0 }}>
        <div ref={ref} style={{ height: 'calc(100vh - 330px)', minHeight: 380 }} />
      </div>
      <p className="muted" style={{ marginTop: 8 }}>
        {q.data ? `${q.data.nodes.length} devices, ${q.data.links.length} links from LLDP/CDP. ` : ''}
        Links are green when up, amber at ≥ 80 % utilization and red when down; thick lines are 10G or faster. Grey squares are neighbours not in the inventory
        {q.data && q.data.unmatched.length > 0 ? ` (${q.data.unmatched.length})` : ''}. Neighbours are re-read with the interface table (every 30 min by default).
      </p>
      <div className="card" style={{ marginTop: 12 }}>
        <div className="card-head">
          <strong>Suggested upstream devices</strong>
          <span className="muted">{suggestions.length} suggestion(s) from the discovered links (used for alert dependencies)</span>
          <span className="spacer" />
          {can('Admin') && (
            <>
              <button disabled={suggestions.length === 0} onClick={() => setPicked(picked.size === suggestions.length ? new Set() : new Set(suggestions.map((x) => x.deviceId)))}>
                {picked.size === suggestions.length && suggestions.length > 0 ? 'Select none' : 'Select all'}
              </button>
              <button className="primary" disabled={picked.size === 0 || apply.isPending} onClick={() => apply.mutate([...picked])}>
                Apply {picked.size || ''}
              </button>
            </>
          )}
        </div>
        {suggestions.length === 0 ? (
          <div className="card-body faint">Every device’s upstream device matches the discovered links.</div>
        ) : (
          <div className="table-wrap" style={{ maxHeight: 320 }}>
            <table className="data">
              <thead>
                <tr>
                  {can('Admin') && <th style={{ width: 30 }} />}
                  <th>Device</th>
                  <th>Current upstream</th>
                  <th>Suggested upstream</th>
                </tr>
              </thead>
              <tbody>
                {suggestions.map((x) => (
                  <tr key={x.deviceId}>
                    {can('Admin') && (
                      <td>
                        <input
                          type="checkbox"
                          aria-label={`Apply for ${x.deviceName}`}
                          checked={picked.has(x.deviceId)}
                          onChange={(e) => {
                            const next = new Set(picked);
                            if (e.target.checked) next.add(x.deviceId);
                            else next.delete(x.deviceId);
                            setPicked(next);
                          }}
                        />
                      </td>
                    )}
                    <td>{x.deviceName}</td>
                    <td className="muted">{x.currentParentName ?? '—'}</td>
                    <td>
                      <strong>{x.suggestedParentName}</strong> <span className="faint">({x.via})</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  );
}
