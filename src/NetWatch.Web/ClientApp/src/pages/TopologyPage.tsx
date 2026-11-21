import { useQuery } from '@tanstack/react-query';
import { TreeChart } from 'echarts/charts';
import { TooltipComponent } from 'echarts/components';
import * as echarts from 'echarts/core';
import { CanvasRenderer } from 'echarts/renderers';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import type { DeviceStatus, TopologyNode } from '../api/types';
import { ErrorBox } from '../components/Modal';
import { typeLabel } from '../util/format';
import { TopologyLinksView } from './TopologyLinks';

echarts.use([TreeChart, TooltipComponent, CanvasRenderer]);

interface TreeItem {
  name: string;
  value: number;
  children?: TreeItem[];
  collapsed?: boolean;
  itemStyle?: { color: string; borderColor: string };
  label?: { color?: string; fontWeight?: string };
  node?: TopologyNode;
}

function cssVar(name: string, fallback: string): string {
  const v = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return v || fallback;
}

function statusColor(status: DeviceStatus): string {
  switch (status) {
    case 'Up':
      return cssVar('--ok', '#2fbf71');
    case 'Warning':
      return cssVar('--warn', '#f2b33d');
    case 'Down':
      return cssVar('--crit', '#ff4d5e');
    case 'Maintenance':
      return cssVar('--accent', '#4c9aff');
    default:
      return cssVar('--text-faint', '#6b7889');
  }
}

/** Worst status in a subtree, so a collapsed branch still shows trouble inside it. */
const rank: Record<DeviceStatus, number> = { Down: 4, Warning: 3, Unknown: 1, Maintenance: 0, Up: 2 };

/**
 * Builds the tree from the parent links (Device → upstream device). Devices without a parent hang under a
 * virtual root. Large branches start collapsed; a collapsed node is coloured by the worst status below it.
 */
export function buildTree(nodes: TopologyNode[], expandAll: boolean): { root: TreeItem; counts: Record<DeviceStatus, number> } {
  const children = new Map<number | null, TopologyNode[]>();
  for (const n of nodes) {
    const key = n.parentId ?? null;
    children.set(key, [...(children.get(key) ?? []), n]);
  }

  const counts: Record<DeviceStatus, number> = { Up: 0, Warning: 0, Down: 0, Unknown: 0, Maintenance: 0 };
  const visited = new Set<number>();
  const build = (n: TopologyNode, depth: number): { item: TreeItem; worst: DeviceStatus } => {
    visited.add(n.id);
    counts[n.status]++;
    const kids = (children.get(n.id) ?? []).filter((k) => !visited.has(k.id)).sort((a, b) => a.name.localeCompare(b.name));
    const built = kids.map((k) => build(k, depth + 1));
    let worst = n.status;
    for (const b of built) if (rank[b.worst] > rank[worst]) worst = b.worst;
    const collapsed = !expandAll && kids.length > 0 && (depth >= 2 || kids.length > 12);
    const shown = collapsed ? worst : n.status;
    return {
      item: {
        name: kids.length > 0 && collapsed ? `${n.name} (+${kids.length})` : n.name,
        value: n.id,
        node: n,
        collapsed,
        children: built.map((b) => b.item),
        itemStyle: { color: statusColor(shown), borderColor: statusColor(shown) },
      },
      worst,
    };
  };

  const roots = (children.get(null) ?? []).sort((a, b) => a.name.localeCompare(b.name)).map((r) => build(r, 1).item);
  // Cycles in the parent chain: show the remaining devices at the top level instead of losing them.
  for (const n of nodes) if (!visited.has(n.id)) roots.push(build(n, 1).item);
  return {
    root: { name: 'Network', value: 0, children: roots, itemStyle: { color: cssVar('--accent', '#4c9aff'), borderColor: cssVar('--accent', '#4c9aff') } },
    counts,
  };
}

/** Topology map: devices arranged by their upstream (parent) device, coloured by status. */
export function TopologyPage() {
  const navigate = useNavigate();
  const ref = useRef<HTMLDivElement>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const [expandAll, setExpandAll] = useState(false);
  const [orient, setOrient] = useState<'LR' | 'TB'>('LR');
  const [view, setView] = useState<'tree' | 'links'>('tree');
  const q = useQuery({ queryKey: ['topology'], queryFn: () => api.get<TopologyNode[]>('/api/topology'), refetchInterval: 30000 });
  const tree = useMemo(() => (q.data ? buildTree(q.data, expandAll) : null), [q.data, expandAll]);
  const withoutParent = (q.data ?? []).filter((n) => !n.parentId).length;

  useEffect(() => {
    if (!ref.current) return;
    chart.current = echarts.init(ref.current, undefined, { renderer: 'canvas' });
    const ro = new ResizeObserver(() => chart.current?.resize());
    ro.observe(ref.current);
    chart.current.on('dblclick', (p) => {
      const node = (p.data as TreeItem | undefined)?.node;
      if (node) navigate(`/devices/${node.id}`);
    });
    return () => {
      ro.disconnect();
      chart.current?.dispose();
      chart.current = null;
    };
  }, [navigate]);

  useEffect(() => {
    if (!tree || !chart.current) return;
    const text = cssVar('--text', '#e6ebf1');
    chart.current.setOption(
      {
        tooltip: {
          trigger: 'item',
          formatter: (p: { data: TreeItem }) => {
            const n = p.data.node;
            if (!n) return 'Network';
            const via = n.unreachableVia ? `<br/>Unreachable via ${n.unreachableVia}` : '';
            return `<b>${n.name}</b><br/>${n.ipAddress} · ${typeLabel(n.type)}<br/>${n.status}${n.worstAlert ? ` · ${n.worstAlert} alert` : ''}${via}${n.groupPath ? `<br/>${n.groupPath}` : ''}<br/><i>Double-click to open</i>`;
          },
        },
        series: [
          {
            type: 'tree',
            data: [tree.root],
            orient,
            layout: 'orthogonal',
            top: 20,
            bottom: 20,
            left: orient === 'LR' ? 90 : 20,
            right: orient === 'LR' ? 160 : 20,
            roam: true,
            symbol: 'circle',
            symbolSize: 11,
            initialTreeDepth: -1,
            expandAndCollapse: true,
            animationDuration: 250,
            label: {
              position: orient === 'LR' ? 'left' : 'top',
              verticalAlign: 'middle',
              align: orient === 'LR' ? 'right' : 'center',
              fontSize: 11,
              color: text,
            },
            leaves: { label: { position: orient === 'LR' ? 'right' : 'bottom', align: orient === 'LR' ? 'left' : 'center' } },
            lineStyle: { color: cssVar('--border-strong', '#3a4656'), width: 1.2 },
          },
        ],
      },
      { notMerge: true },
    );
  }, [tree, orient]);

  return (
    <>
      <div className="page-head">
        <h1>Topology</h1>
        <div className="tabs" role="tablist" style={{ marginLeft: 16 }}>
          <button role="tab" className={view === 'tree' ? 'active' : ''} aria-selected={view === 'tree'} onClick={() => setView('tree')}>
            Upstream tree
          </button>
          <button role="tab" className={view === 'links' ? 'active' : ''} aria-selected={view === 'links'} onClick={() => setView('links')}>
            Discovered links (LLDP/CDP)
          </button>
        </div>
      </div>
      {view === 'links' && <TopologyLinksView />}
      <div style={{ display: view === 'tree' ? undefined : 'none' }}>
      <ErrorBox error={q.error} />
      <div className="toolbar" style={{ marginBottom: 10, flexWrap: 'wrap' }}>
        {tree && (
          <span className="muted">
            {q.data?.length} devices · <span style={{ color: 'var(--ok)' }}>{tree.counts.Up} up</span> · <span style={{ color: 'var(--warn)' }}>{tree.counts.Warning} warning</span> ·{' '}
            <span style={{ color: 'var(--crit)' }}>{tree.counts.Down} down</span>
            {tree.counts.Maintenance > 0 && ` · ${tree.counts.Maintenance} in maintenance`}
          </span>
        )}
        <span style={{ flex: 1 }} />
        <label className="check">
          <input type="checkbox" checked={expandAll} onChange={(e) => setExpandAll(e.target.checked)} /> Expand all
        </label>
        <select value={orient} onChange={(e) => setOrient(e.target.value as 'LR' | 'TB')} aria-label="Layout">
          <option value="LR">Left to right</option>
          <option value="TB">Top to bottom</option>
        </select>
      </div>
      <div className="card" style={{ padding: 0 }}>
        <div ref={ref} style={{ height: 'calc(100vh - 230px)', minHeight: 420 }} />
      </div>
      <p className="muted" style={{ marginTop: 8 }}>
        Lines follow each device’s <em>upstream device</em> (set on the device page; also used for alert suppression). Click a node to expand or collapse it, double-click to open the
        device, scroll to zoom and drag to pan. A collapsed branch takes the colour of the worst device inside it.
        {withoutParent > 1 && ` ${withoutParent} devices have no upstream device and are shown at the top level.`}
      </p>
      </div>
    </>
  );
}
