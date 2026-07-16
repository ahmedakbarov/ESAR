import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

export default function Matching() {
  const [queue, setQueue] = useState<any[]>([]);
  const [stats, setStats] = useState<any>(null);
  const [rules, setRules] = useState<any[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);

  const load = () => {
    client.get('/matching/review-queue').then((r) => setQueue(r.data));
    client.get('/matching/stats').then((r) => setStats(r.data));
    client.get('/matching/rules').then((r) => setRules(r.data));
  };
  useEffect(load, []);

  const decide = async (id: string, action: 'approve' | 'reject') => {
    await client.post(`/matching/review-queue/${id}/${action}`, {});
    load();
  };

  return (
    <>
      {stats && (
        <div className="grid cols-4">
          <div className="card stat"><h3>Total Decisions</h3><div className="value">{stats.total}</div></div>
          <div className="card stat"><h3>Pending Review</h3><div className="value">{stats.pendingReview}</div></div>
          <div className="card stat"><h3>Avg Confidence (30d)</h3><div className="value">{stats.avgConfidenceLast30Days}</div></div>
          <div className="card stat">
            <h3>Auto-merged</h3>
            <div className="value">{stats.byDecision?.AutoMerged ?? 0}</div>
          </div>
        </div>
      )}

      <h2 className="section-title">Manual Review Queue</h2>
      <div className="card">
        <table className="data">
          <thead>
            <tr><th>Candidate</th><th>Source</th><th>Matched Asset</th><th>Score</th><th>Queued</th><th>Actions</th></tr>
          </thead>
          <tbody>
            {queue.map((m) => (
              <>
                <tr key={m.id}>
                  <td>
                    <a href="#" onClick={(e) => { e.preventDefault(); setExpanded(expanded === m.id ? null : m.id); }}>
                      {m.candidateHostname ?? m.externalId}
                    </a>
                  </td>
                  <td><Badge value={m.source} /></td>
                  <td>{m.matchedAssetId
                    ? <Link to={`/assets/${m.matchedAssetId}`}>{m.matchedAssetId.substring(0, 8)}…</Link>
                    : '—'}</td>
                  <td>{(m.score * 100).toFixed(0)}%</td>
                  <td className="muted">{formatDate(m.queuedAt)}</td>
                  <td>
                    <button onClick={() => decide(m.id, 'approve')} style={{ marginRight: 6 }}>Merge</button>
                    <button className="secondary" onClick={() => decide(m.id, 'reject')}>New asset</button>
                  </td>
                </tr>
                {expanded === m.id && (
                  <tr key={`${m.id}-detail`}>
                    <td colSpan={6}>
                      <pre className="json">{JSON.stringify(m.explanation, null, 2)}</pre>
                    </td>
                  </tr>
                )}
              </>
            ))}
            {queue.length === 0 && <tr><td colSpan={6} className="muted">Review queue is empty.</td></tr>}
          </tbody>
        </table>
      </div>

      <h2 className="section-title">Matching Rules</h2>
      <p className="muted" style={{ marginBottom: 10 }}>
        Weights, order and activation are fully configurable — changes apply within 5 minutes (rule cache TTL).
      </p>
      <div className="card">
        <table className="data">
          <thead><tr><th>Rule</th><th>Attribute</th><th>Type</th><th>Weight</th><th>Order</th>
            <th>Enabled</th><th>Version</th><th></th></tr></thead>
          <tbody>
            {rules.map((r) => (
              <tr key={r.id}>
                <td>{r.name}</td>
                <td className="muted">{r.attribute}</td>
                <td><Badge value={r.matchType} /></td>
                <td>
                  <input type="number" step="0.05" min="0" max="1" defaultValue={r.weight}
                    style={{ width: 80 }} onChange={(e) => (r._weight = e.target.value)} />
                </td>
                <td>
                  <input type="number" defaultValue={r.order} style={{ width: 70 }}
                    onChange={(e) => (r._order = e.target.value)} />
                </td>
                <td>
                  <input type="checkbox" defaultChecked={r.enabled}
                    onChange={(e) => (r._enabled = e.target.checked)} />
                </td>
                <td className="muted">v{r.version ?? 1}</td>
                <td>
                  <button className="secondary" onClick={async () => {
                    await client.put(`/matching/rules/${r.id}`, {
                      weight: Number(r._weight ?? r.weight),
                      order: Number(r._order ?? r.order),
                      enabled: r._enabled ?? r.enabled,
                    });
                    load();
                  }}>Save</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
