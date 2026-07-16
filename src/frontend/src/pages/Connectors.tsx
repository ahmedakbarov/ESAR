import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

export default function Connectors() {
  const [connectors, setConnectors] = useState<any[]>([]);
  const [jobs, setJobs] = useState<Record<string, any[]>>({});
  const [expanded, setExpanded] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = () => client.get('/connectors').then((r) => setConnectors(r.data));
  useEffect(() => { load(); }, []);

  const run = async (id: string) => {
    setBusy(id);
    try { await client.post(`/connectors/${id}/run`); } finally { setBusy(null); load(); }
  };

  const healthCheck = async (id: string) => {
    setBusy(id);
    try { await client.post(`/connectors/${id}/health-check`); } finally { setBusy(null); load(); }
  };

  const toggleJobs = async (id: string) => {
    if (expanded === id) { setExpanded(null); return; }
    const { data } = await client.get(`/connectors/${id}/jobs?limit=10`);
    setJobs((prev) => ({ ...prev, [id]: data }));
    setExpanded(id);
  };

  return (
    <div className="card">
      <table className="data">
        <thead>
          <tr>
            <th>Name</th><th>Type</th><th>Enabled</th><th>Schedule</th><th>Health</th>
            <th>Last Run</th><th>Status</th><th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {connectors.map((c) => (
            <>
              <tr key={c.id}>
                <td><a href="#" onClick={(e) => { e.preventDefault(); toggleJobs(c.id); }}>{c.name}</a></td>
                <td><Badge value={c.type} /></td>
                <td>{c.enabled ? <Badge value="Active" /> : <Badge value="Inactive" />}</td>
                <td className="muted">{c.cronSchedule}</td>
                <td>
                  {c.isHealthy ? <Badge value="Compliant" /> : <Badge value="Failed" />}
                  {!c.isHealthy && <div className="muted" style={{ fontSize: 11 }}>{c.lastHealthMessage}</div>}
                </td>
                <td className="muted">{formatDate(c.lastRunAt)}</td>
                <td><Badge value={c.lastRunStatus} /></td>
                <td>
                  <button disabled={busy === c.id} onClick={() => run(c.id)} style={{ marginRight: 6 }}>
                    {busy === c.id ? 'Running…' : 'Sync now'}
                  </button>
                  <button className="secondary" disabled={busy === c.id} onClick={() => healthCheck(c.id)}>
                    Health
                  </button>
                </td>
              </tr>
              {expanded === c.id && (
                <tr key={`${c.id}-jobs`}>
                  <td colSpan={8}>
                    <table className="data">
                      <thead>
                        <tr><th>Status</th><th>Mode</th><th>Started</th><th>Completed</th>
                          <th>Discovered</th><th>Created</th><th>Updated</th><th>Failed</th><th>By</th></tr>
                      </thead>
                      <tbody>
                        {(jobs[c.id] ?? []).map((j) => (
                          <tr key={j.id}>
                            <td><Badge value={j.status} /></td>
                            <td className="muted">{j.syncMode}</td>
                            <td className="muted">{formatDate(j.startedAt)}</td>
                            <td className="muted">{formatDate(j.completedAt)}</td>
                            <td>{j.assetsDiscovered}</td>
                            <td>{j.assetsCreated}</td>
                            <td>{j.assetsUpdated}</td>
                            <td style={{ color: j.assetsFailed > 0 ? 'var(--red)' : undefined }}>{j.assetsFailed}</td>
                            <td className="muted">{j.triggeredBy}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </td>
                </tr>
              )}
            </>
          ))}
          {connectors.length === 0 && (
            <tr><td colSpan={8} className="muted">
              No connectors configured. Create one via POST /api/v1/connectors — see the Administration Guide.
            </td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
