import { useQuery, useQueryClient } from '@tanstack/react-query';
import { ChevronRight, Folder, Pencil, Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { api } from '../../api/client';
import type { Group, GroupKind } from '../../api/types';
import { GROUP_KINDS } from '../../api/types';
import { Confirm, ErrorBox, Modal } from '../../components/Modal';

/** Flattens the group tree into indented select options ("Site › Building › Floor"). */
export function groupOptions(groups: Group[]): { id: number; label: string }[] {
  const children = new Map<number | null, Group[]>();
  for (const g of groups) {
    const key = g.parentId ?? null;
    children.set(key, [...(children.get(key) ?? []), g]);
  }
  const out: { id: number; label: string }[] = [];
  const walk = (parent: number | null, prefix: string, depth: number) => {
    if (depth > 20) return;
    for (const g of (children.get(parent) ?? []).sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))) {
      const label = prefix ? `${prefix} › ${g.name}` : g.name;
      out.push({ id: g.id, label });
      walk(g.id, label, depth + 1);
    }
  };
  walk(null, '', 0);
  return out;
}

function GroupDialog({ group, groups, parentId, onClose }: { group?: Group; groups: Group[]; parentId?: number | null; onClose: () => void }) {
  const qc = useQueryClient();
  const [name, setName] = useState(group?.name ?? '');
  const [kind, setKind] = useState<GroupKind>(group?.kind ?? 'Site');
  const [parent, setParent] = useState<number | null>(group ? group.parentId ?? null : parentId ?? null);
  const [sortOrder, setSortOrder] = useState(group?.sortOrder ?? 0);
  const [error, setError] = useState<unknown>(null);

  const save = async () => {
    try {
      const body = { name, kind, parentId: parent, sortOrder };
      if (group) await api.put(`/api/groups/${group.id}`, body);
      else await api.post('/api/groups', body);
      await qc.invalidateQueries({ queryKey: ['groups'] });
      await qc.invalidateQueries({ queryKey: ['devices'] });
      onClose();
    } catch (e) {
      setError(e);
    }
  };

  return (
    <Modal
      title={group ? 'Edit group' : 'Add group'}
      onClose={onClose}
      footer={
        <>
          <button onClick={onClose}>Cancel</button>
          <button className="primary" onClick={() => void save()}>
            Save
          </button>
        </>
      }
    >
      <ErrorBox error={error} />
      <div className="form-grid">
        <label className="field">
          <span>Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} autoFocus maxLength={128} />
        </label>
        <label className="field">
          <span>Kind</span>
          <select value={kind} onChange={(e) => setKind(e.target.value as GroupKind)}>
            {GROUP_KINDS.map((k) => (
              <option key={k}>{k}</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Parent</span>
          <select value={parent ?? ''} onChange={(e) => setParent(e.target.value ? Number(e.target.value) : null)}>
            <option value="">— top level —</option>
            {groupOptions(groups)
              .filter((g) => g.id !== group?.id)
              .map((g) => (
                <option key={g.id} value={g.id}>
                  {g.label}
                </option>
              ))}
          </select>
        </label>
        <label className="field">
          <span>Sort order</span>
          <input type="number" value={sortOrder} onChange={(e) => setSortOrder(Number(e.target.value))} />
        </label>
      </div>
    </Modal>
  );
}

export function GroupsTab() {
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ['groups'], queryFn: () => api.get<Group[]>('/api/groups') });
  const [edit, setEdit] = useState<{ group?: Group; parentId?: number | null } | null>(null);
  const [del, setDel] = useState<Group | null>(null);
  const [error, setError] = useState<unknown>(null);
  const groups = q.data ?? [];
  const byId = new Map(groups.map((g) => [g.id, g]));
  const depth = (g: Group) => {
    let d = 0;
    let p = g.parentId;
    while (p && d < 20) {
      d++;
      p = byId.get(p)?.parentId;
    }
    return d;
  };

  return (
    <>
      <div className="toolbar" style={{ marginBottom: 12 }}>
        <span className="muted">Sites tree, e.g. Site › Building › Floor › Rack. Used for filtering, health tiles and alert rule scopes.</span>
        <span style={{ flex: 1 }} />
        <button className="primary" onClick={() => setEdit({})}>
          <Plus size={16} /> Add group
        </button>
      </div>
      <ErrorBox error={error ?? q.error} />
      <div className="card card-body">
        {groupOptions(groups).map((o) => {
          const g = byId.get(o.id)!;
          return (
            <div key={g.id} className="tree-item" style={{ paddingLeft: 8 + depth(g) * 22 }}>
              {depth(g) > 0 && <ChevronRight size={14} className="faint" />}
              <Folder size={15} className="muted" />
              <strong>{g.name}</strong>
              <span className="tag">{g.kind}</span>
              <span className="muted">{g.deviceCount} device(s)</span>
              <span style={{ flex: 1 }} />
              <button className="ghost small" onClick={() => setEdit({ parentId: g.id })} title="Add sub-group">
                <Plus size={14} />
              </button>
              <button className="ghost small" onClick={() => setEdit({ group: g })} title="Edit">
                <Pencil size={14} />
              </button>
              <button className="ghost small" onClick={() => setDel(g)} title="Delete">
                <Trash2 size={14} />
              </button>
            </div>
          );
        })}
        {groups.length === 0 && <div className="empty">No groups yet.</div>}
      </div>
      {edit && <GroupDialog group={edit.group} parentId={edit.parentId} groups={groups} onClose={() => setEdit(null)} />}
      {del && (
        <Confirm
          title={`Delete group ${del.name}?`}
          danger
          confirmLabel="Delete"
          message="Devices in this group become ungrouped. Groups with sub-groups cannot be deleted."
          onClose={() => setDel(null)}
          onConfirm={() =>
            void (async () => {
              try {
                await api.del(`/api/groups/${del.id}`);
                await qc.invalidateQueries({ queryKey: ['groups'] });
              } catch (e) {
                setError(e);
              }
              setDel(null);
            })()
          }
        />
      )}
    </>
  );
}
