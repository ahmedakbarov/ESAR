import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

export default function Incidents() {
  const [data, setData] = useState<any>(null);
  const [status, setStatus] = useState('Open');
  const [page, setPage] = useState(1);

  const load = () => {
    const params = new URLSearchParams({ page: String(page), pageSize: '25' });
    if (status) params.set('status', status);
    client.get(`/incidents?${params}`).then((r) => setData(r.data));
  };
  useEffect(load, [status, page]);

  const updateStatus = async (id: string, newStatus: string) => {
    await client.patch(`/incidents/${id}`, { status: newStatus });
    load();
  };

  return (
    <div className="card">
      <div className="filters">
        <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
          <option value="">All statuses</option>
          {['Open', 'InProgress', 'Resolved', 'Closed', 'Suppressed'].map((s) => <option key={s}>{s}</option>)}
        </select>
      </div>
      <table className="data">
        <thead>
          <tr><th>Severity</th><th>Type</th><th>Title</th><th>Asset</th><th>Status</th>
            <th>Ticket</th><th>Created</th><th>Actions</th></tr>
        </thead>
        <tbody>
          {data?.items.map((i: any) => (
            <tr key={i.id}>
              <td><Badge value={i.severity} /></td>
              <td className="muted">{i.type}</td>
              <td>{i.title}</td>
              <td>{i.assetId ? <Link to={`/assets/${i.assetId}`}>view</Link> : '—'}</td>
              <td><Badge value={i.status} /></td>
              <td className="muted">{i.externalTicketId ? `${i.externalSystem}: ${i.externalTicketId}` : '—'}</td>
              <td className="muted">{formatDate(i.createdAt)}</td>
              <td>
                {i.status === 'Open' && (
                  <button className="secondary" onClick={() => updateStatus(i.id, 'Resolved')}>Resolve</button>
                )}
              </td>
            </tr>
          ))}
          {data?.items.length === 0 && <tr><td colSpan={8} className="muted">No incidents.</td></tr>}
        </tbody>
      </table>
      {data && (
        <div className="pagination">
          <button className="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Prev</button>
          <span>{data.totalCount} incidents</span>
          <button className="secondary" disabled={page * data.pageSize >= data.totalCount}
            onClick={() => setPage(page + 1)}>Next</button>
        </div>
      )}
    </div>
  );
}
