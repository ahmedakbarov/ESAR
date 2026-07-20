import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

interface ConnectorForm {
  id?: string;
  name: string;
  type: string;
  enabled: boolean;
  cronSchedule: string;
  priority: number;
  rateLimitPerMinute: number;
  defaultSyncMode: string;
  settingsText: string; // key=value per line
}

const emptyConnector: ConnectorForm = {
  name: '', type: 'ActiveDirectory', enabled: true, cronSchedule: '0 */4 * * *',
  priority: 100, rateLimitPerMinute: 300, defaultSyncMode: 'Incremental', settingsText: '',
};

function parseSettings(text: string): Record<string, string> {
  const result: Record<string, string> = {};
  text.split('\n').forEach((line) => {
    const idx = line.indexOf('=');
    if (idx > 0) result[line.slice(0, idx).trim()] = line.slice(idx + 1).trim();
  });
  return result;
}

function settingsPlaceholder(type: string): string {
  if (type === 'ActiveDirectory') {
    return 'server=dc01.esar.local\\nport=636\\nbaseDn=DC=esar,DC=local\\nusername=svc_esar_ad@esar.local\\npassword=...\\nuseSsl=true\\nauthType=Basic\\ntimeoutSeconds=30\\nresolveDns=true\\ndnsTimeoutSeconds=5\\ndnsMaxConcurrency=8';
  }
  if (type === 'Azure') {
    return 'tenantId=...\\nclientId=...\\nclientSecret=...\\nsubscriptionIds=00000000-0000-0000-0000-000000000000';
  }
  return 'tenantId=...\\nclientId=...\\nclientSecret=...';
}

export default function Connectors() {
  const [connectors, setConnectors] = useState<any[]>([]);
  const [types, setTypes] = useState<string[]>([]);
  const [jobs, setJobs] = useState<Record<string, any[]>>({});
  const [expanded, setExpanded] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [form, setForm] = useState<ConnectorForm | null>(null);
  const [error, setError] = useState('');

  const load = () => {
    client.get('/connectors').then((r) => setConnectors(r.data));
    client.get('/connectors/types').then((r) => setTypes(r.data));
  };
  useEffect(() => { load(); }, []);

  const save = async () => {
    if (!form) return;
    setError('');
    const payload = {
      name: form.name,
      type: form.type,
      enabled: form.enabled,
      cronSchedule: form.cronSchedule,
      priority: form.priority,
      rateLimitPerMinute: form.rateLimitPerMinute,
      defaultSyncMode: form.defaultSyncMode,
      settings: parseSettings(form.settingsText),
    };
    try {
      if (form.id) await client.put(`/connectors/${form.id}`, payload);
      else await client.post('/connectors', payload);
      setForm(null);
      load();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Save failed');
    }
  };

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
      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
        <h3>Connectors</h3>
        <button onClick={() => { setError(''); setForm(form ? null : { ...emptyConnector }); }}>
          {form ? 'Cancel' : 'New connector'}
        </button>
      </div>

      {form && (
        <div className="card" style={{ marginBottom: 16 }}>
          <div className="filters">
            <input placeholder="Name" value={form.name}
              onChange={(e) => setForm({ ...form, name: e.target.value })} style={{ width: 200 }} />
            <select value={form.type} disabled={!!form.id}
              onChange={(e) => setForm({ ...form, type: e.target.value })}>
              {types.map((t) => <option key={t}>{t}</option>)}
            </select>
            <input placeholder="Cron schedule" value={form.cronSchedule} title="Cron schedule"
              onChange={(e) => setForm({ ...form, cronSchedule: e.target.value })} style={{ width: 130 }} />
            <input type="number" title="Priority" value={form.priority}
              onChange={(e) => setForm({ ...form, priority: Number(e.target.value) })} style={{ width: 80 }} />
            <input type="number" title="Rate limit / minute" value={form.rateLimitPerMinute}
              onChange={(e) => setForm({ ...form, rateLimitPerMinute: Number(e.target.value) })} style={{ width: 90 }} />
            <select value={form.defaultSyncMode}
              onChange={(e) => setForm({ ...form, defaultSyncMode: e.target.value })}>
              <option>Incremental</option>
              <option>Full</option>
            </select>
            <label style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <input type="checkbox" checked={form.enabled}
                onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Enabled
            </label>
          </div>
          <h3>
            {form.type === 'ActiveDirectory'
              ? 'Active Directory: use the DC FQDN, LDAPS port 636, and a read-only UPN or bind DN. resolveDns needs private AD DNS and adds IP-only evidence; keep *** to preserve an existing encrypted password.'
              : 'Settings (key=value per line; secrets are encrypted at rest, keep *** to preserve)'}
          </h3>
          <textarea rows={form.type === 'ActiveDirectory' ? 12 : 5} style={{ width: '100%', fontFamily: 'monospace' }}
            placeholder={settingsPlaceholder(form.type)}
            value={form.settingsText}
            onChange={(e) => setForm({ ...form, settingsText: e.target.value })} />
          {error && <div className="error" style={{ margin: '8px 0' }}>{error}</div>}
          <div style={{ marginTop: 8 }}>
            <button onClick={save}>Save connector</button>
          </div>
        </div>
      )}

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
                  <button className="secondary" disabled={busy === c.id} onClick={() => healthCheck(c.id)}
                    style={{ marginRight: 6 }}>
                    Health
                  </button>
                  <button className="secondary" onClick={() => setForm({
                    id: c.id, name: c.name, type: c.type, enabled: c.enabled,
                    cronSchedule: c.cronSchedule, priority: c.priority,
                    rateLimitPerMinute: c.rateLimitPerMinute, defaultSyncMode: c.defaultSyncMode,
                    settingsText: Object.entries(c.settings ?? {})
                      .map(([k, v]) => `${k}=${v}`).join('\n'),
                  })}>
                    Edit
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
              No connectors configured yet — click "New connector" to add the first one.
            </td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
