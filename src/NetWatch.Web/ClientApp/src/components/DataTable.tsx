import { useVirtualizer } from '@tanstack/react-virtual';
import { useMemo, useRef, useState, type ReactNode } from 'react';

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  /** Value used for sorting; omit to make the column unsortable. */
  sort?: (row: T) => string | number | null | undefined;
  width?: number | string;
  className?: string;
}

interface Props<T> {
  rows: T[];
  columns: Column<T>[];
  rowKey: (row: T) => string | number;
  onRowClick?: (row: T) => void;
  initialSort?: { key: string; desc?: boolean };
  /** Max height of the scroll area; rows are virtualised so thousands of rows stay fast. */
  maxHeight?: number | string;
  selectable?: boolean;
  selected?: Set<string | number>;
  onSelectedChange?: (s: Set<string | number>) => void;
  empty?: ReactNode;
  rowClassName?: (row: T) => string | undefined;
}

const ROW_HEIGHT = 37;

/** Sortable, virtualised table with optional row selection. */
export function DataTable<T>({ rows, columns, rowKey, onRowClick, initialSort, maxHeight = '70vh', selectable, selected, onSelectedChange, empty, rowClassName }: Props<T>) {
  const [sort, setSort] = useState(initialSort);
  const parent = useRef<HTMLDivElement>(null);

  const sorted = useMemo(() => {
    if (!sort) return rows;
    const col = columns.find((c) => c.key === sort.key);
    if (!col?.sort) return rows;
    const getter = col.sort;
    return [...rows].sort((a, b) => {
      const va = getter(a);
      const vb = getter(b);
      if (va === vb) return 0;
      if (va === null || va === undefined) return 1;
      if (vb === null || vb === undefined) return -1;
      const r = typeof va === 'number' && typeof vb === 'number' ? va - vb : String(va).localeCompare(String(vb), undefined, { numeric: true });
      return sort.desc ? -r : r;
    });
  }, [rows, columns, sort]);

  const virtualizer = useVirtualizer({
    count: sorted.length,
    getScrollElement: () => parent.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 12,
  });

  const items = virtualizer.getVirtualItems();
  const paddingTop = items.length > 0 ? items[0].start : 0;
  const paddingBottom = items.length > 0 ? virtualizer.getTotalSize() - items[items.length - 1].end : 0;
  const allSelected = selectable && sorted.length > 0 && sorted.every((r) => selected?.has(rowKey(r)));
  const colSpan = columns.length + (selectable ? 1 : 0);

  const toggle = (key: string | number) => {
    const next = new Set(selected);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    onSelectedChange?.(next);
  };

  return (
    <div ref={parent} className="vtable" style={{ maxHeight }}>
      <table className="data">
        <thead>
          <tr>
            {selectable && (
              <th style={{ width: 34 }}>
                <input
                  type="checkbox"
                  checked={!!allSelected}
                  onChange={() => onSelectedChange?.(allSelected ? new Set() : new Set(sorted.map(rowKey)))}
                  aria-label="Select all"
                />
              </th>
            )}
            {columns.map((c) => (
              <th
                key={c.key}
                style={{ width: c.width }}
                className={`${c.sort ? 'sortable' : ''} ${c.className ?? ''}`}
                onClick={() => c.sort && setSort((s) => (s?.key === c.key ? { key: c.key, desc: !s.desc } : { key: c.key, desc: false }))}
              >
                {c.header}
                {sort?.key === c.key ? (sort.desc ? ' ▼' : ' ▲') : ''}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {sorted.length === 0 && (
            <tr>
              <td colSpan={colSpan} className="empty">
                {empty ?? 'Nothing to show.'}
              </td>
            </tr>
          )}
          {paddingTop > 0 && (
            <tr>
              <td colSpan={colSpan} style={{ height: paddingTop, padding: 0, border: 0 }} />
            </tr>
          )}
          {items.map((vi) => {
            const row = sorted[vi.index];
            const key = rowKey(row);
            return (
              <tr
                key={key}
                className={`${onRowClick ? 'clickable' : ''} ${selected?.has(key) ? 'selected' : ''} ${rowClassName?.(row) ?? ''}`}
                onClick={() => onRowClick?.(row)}
                style={{ height: ROW_HEIGHT }}
              >
                {selectable && (
                  <td onClick={(e) => e.stopPropagation()}>
                    <input type="checkbox" checked={!!selected?.has(key)} onChange={() => toggle(key)} aria-label="Select row" />
                  </td>
                )}
                {columns.map((c) => (
                  <td key={c.key} className={c.className}>
                    {c.render(row)}
                  </td>
                ))}
              </tr>
            );
          })}
          {paddingBottom > 0 && (
            <tr>
              <td colSpan={colSpan} style={{ height: paddingBottom, padding: 0, border: 0 }} />
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
