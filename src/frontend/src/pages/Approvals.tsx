import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

export default function Approvals() {
  const [pending, setPending] = useState<any[]>([]);
  const [busy, setBusy] = useState<string | null>(null);

  const load = () => client.get('/approvals/pending').then((r) => setPending(r.data));
  useEffect(() => { load(); }, []);

  const decide = async (id: string, action: 'approve' | 'reject') => {
    setBusy(id);
    try {
      await client.post(`/approvals/${id}/${action}`, {});
      window.dispatchEvent(new Event('esar:counts-changed'));
    } finally {
      setBusy(null);
      load();
    }
  };

  return (
    <div className="card">
      <h3>Pending Approvals</h3>
      <p className="muted" style={{ marginBottom: 12 }}>
        Newly discovered assets (when activation approval is enabled) and requested merges wait here
        for an owner decision. Approving a new asset moves it to the Active lifecycle.
      </p>
      <table className="data">
        <thead>
          <tr><th>Type</th><th>Asset</th><th>Requested By</th><th>Justification</th>
            <th>Requested</th><th>Actions</th></tr>
        </thead>
        <tbody>
          {pending.map((p) => (
            <tr key={p.id}>
              <td><Badge value={p.type} /></td>
              <td>{p.assetId
                ? <Link to={`/assets/${p.assetId}`}>{p.hostname ?? p.assetId.substring(0, 8)}</Link>
                : '—'}</td>
              <td className="muted">{p.requestedBy}</td>
              <td className="muted">{p.justification ?? '—'}</td>
              <td className="muted">{formatDate(p.requestedAt)}</td>
              <td>
                <button disabled={busy === p.id} onClick={() => decide(p.id, 'approve')}
                  style={{ marginRight: 6 }}>Approve</button>
                <button className="danger" disabled={busy === p.id}
                  onClick={() => decide(p.id, 'reject')}>Reject</button>
              </td>
            </tr>
          ))}
          {pending.length === 0 && <tr><td colSpan={6} className="muted">Nothing waiting for approval.</td></tr>}
        </tbody>
      </table>
    </div>
  );
}
