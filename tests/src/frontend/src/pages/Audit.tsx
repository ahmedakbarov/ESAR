import { useEffect, useState } from 'react';
import client from '../api/client';
import { formatDate } from '../components/Ui';

export default function Audit() {
  const [data, setData] = useState<any>(null);
  const [user, setUser] = useState('');
  const [page, setPage] = useState(1);

  const load = () => {
    const params = new URLSearchParams({ page: String(page), pageSize: '50' });
    if (user) params.set('user', user);
    client.get(`/audit?${params}`).then((r) => setData(r.data));
  };
  useEffect(load, [page]);

  return (
    <div className="card">
      <div className="filters">
        <input placeholder="Filter by user…" value={user} onChange={(e) => setUser(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (setPage(1), load())} />
        <button onClick={() => { setPage(1); load(); }}>Filter</button>
      </div>
      <table className="data">
        <thead><tr><th>Time</th><th>User</th><th>Action</th><th>Entity</th><th>Details</th><th>IP</th></tr></thead>
        <tbody>
          {data?.items.map((l: any) => (
            <tr key={l.id}>
              <td className="muted">{formatDate(l.timestamp)}</td>
              <td>{l.userName}</td>
              <td>{l.action}</td>
              <td className="muted">{l.entityType} {l.entityId?.substring(0, 12)}</td>
              <td className="muted" style={{ maxWidth: 360, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {l.details}
              </td>
              <td className="muted">{l.ipAddress ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {data && (
        <div className="pagination">
          <button className="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Prev</button>
          <span>{data.totalCount} entries</span>
          <button className="secondary" disabled={page * data.pageSize >= data.totalCount}
            onClick={() => setPage(page + 1)}>Next</button>
        </div>
      )}
    </div>
  );
}
