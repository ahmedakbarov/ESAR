import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge } from '../components/Ui';

interface Group { id: string; name: string; description?: string; memberCount: number; }
interface MemberAsset {
  id: string; hostname: string; assetType: string; environment: string;
  criticality: string; status: string;
}
interface AssetHit { id: string; hostname: string; assetType: string; environment: string; }

export default function Groups() {
  const [groups, setGroups] = useState<Group[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [members, setMembers] = useState<MemberAsset[]>([]);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState('');
  const [search, setSearch] = useState('');
  const [hits, setHits] = useState<AssetHit[]>([]);

  const loadGroups = () => client.get('/asset-groups').then((r) => setGroups(r.data));
  useEffect(() => { loadGroups(); }, []);

  const manage = (id: string) => {
    setSelected(id);
    setHits([]); setSearch('');
    client.get(`/asset-groups/${id}/members`).then((r) => setMembers(r.data));
  };

  const create = async () => {
    setError('');
    if (!name.trim()) return;
    try {
      await client.post('/asset-groups', { name, description });
      setName(''); setDescription('');
      loadGroups();
    } catch (e: any) { setError(e.response?.data?.error ?? 'Create failed'); }
  };

  const remove = async (id: string, gname: string) => {
    if (!window.confirm(`Delete group "${gname}"? Assets are only ungrouped, not deleted.`)) return;
    await client.delete(`/asset-groups/${id}`);
    if (selected === id) { setSelected(null); setMembers([]); }
    loadGroups();
  };

  const runSearch = async () => {
    if (!search.trim()) { setHits([]); return; }
    const { data } = await client.get(`/assets?search=${encodeURIComponent(search)}&pageSize=20`);
    setHits(data.items ?? []);
  };

  const addMember = async (assetId: string) => {
    if (!selected) return;
    await client.post(`/asset-groups/${selected}/members`, { assetIds: [assetId] });
    manage(selected); loadGroups();
  };

  const removeMember = async (assetId: string) => {
    if (!selected) return;
    await client.delete(`/asset-groups/${selected}/members/${assetId}`);
    manage(selected); loadGroups();
  };

  const selectedGroup = groups.find((g) => g.id === selected);
  const memberIds = new Set(members.map((m) => m.id));

  return (
    <>
      <div className="card">
        <h3>Asset groups</h3>
        <p className="muted" style={{ marginBottom: 12 }}>
          A group is a hand-picked set of assets. Apply a security baseline to a group by selecting it
          in a policy's scope (Policies → Groups).
        </p>
        <div className="filters">
          <input placeholder="Group name" value={name} onChange={(e) => setName(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && create()} style={{ width: 220 }} />
          <input placeholder="Description (optional)" value={description}
            onChange={(e) => setDescription(e.target.value)} style={{ width: 300 }} />
          <button onClick={create}>Create group</button>
        </div>
        {error && <div className="error" style={{ marginBottom: 10 }}>{error}</div>}

        <table className="data">
          <thead><tr><th>Name</th><th>Description</th><th>Members</th><th></th></tr></thead>
          <tbody>
            {groups.map((g) => (
              <tr key={g.id} style={{ background: g.id === selected ? 'var(--panel-2)' : undefined }}>
                <td>{g.name}</td>
                <td className="muted">{g.description ?? '—'}</td>
                <td>{g.memberCount}</td>
                <td>
                  <div style={{ display: 'flex', gap: 6 }}>
                    <button className="secondary" onClick={() => manage(g.id)}>Manage</button>
                    <button className="danger" onClick={() => remove(g.id, g.name)}>Delete</button>
                  </div>
                </td>
              </tr>
            ))}
            {groups.length === 0 && (
              <tr><td colSpan={4} className="muted">No groups yet — create one above.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {selectedGroup && (
        <div className="card" style={{ marginTop: 16 }}>
          <h3>Members of “{selectedGroup.name}” ({members.length})</h3>
          <table className="data" style={{ marginBottom: 16 }}>
            <thead><tr><th>Hostname</th><th>Type</th><th>Environment</th><th>Criticality</th>
              <th>Status</th><th></th></tr></thead>
            <tbody>
              {members.map((m) => (
                <tr key={m.id}>
                  <td>{m.hostname}</td>
                  <td className="muted">{m.assetType}</td>
                  <td className="muted">{m.environment}</td>
                  <td><Badge value={m.criticality} /></td>
                  <td><Badge value={m.status} /></td>
                  <td><button className="danger" onClick={() => removeMember(m.id)}>Remove</button></td>
                </tr>
              ))}
              {members.length === 0 && (
                <tr><td colSpan={6} className="muted">No assets in this group yet — add some below.</td></tr>
              )}
            </tbody>
          </table>

          <h3>Add assets</h3>
          <div className="filters">
            <input placeholder="Search assets by hostname…" value={search}
              onChange={(e) => setSearch(e.target.value)}
              onKeyDown={(e) => e.key === 'Enter' && runSearch()} style={{ width: 280 }} />
            <button onClick={runSearch}>Search</button>
          </div>
          {hits.length > 0 && (
            <table className="data">
              <thead><tr><th>Hostname</th><th>Type</th><th>Environment</th><th></th></tr></thead>
              <tbody>
                {hits.map((a) => (
                  <tr key={a.id}>
                    <td>{a.hostname}</td>
                    <td className="muted">{a.assetType}</td>
                    <td className="muted">{a.environment}</td>
                    <td>
                      {memberIds.has(a.id)
                        ? <span className="muted">In group</span>
                        : <button onClick={() => addMember(a.id)}>Add</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </>
  );
}
