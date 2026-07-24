import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge, formatDate, Modal } from '../components/Ui';

interface Template {
  key: string; name: string; type: string; defaultFormat: string;
  description: string; supportsFilters: boolean;
}
interface ReportRow {
  id: string; name: string; type: string; format: string; status: string;
  generatedAt?: string; generatedBy?: string; error?: string;
}

const FORMATS = ['Excel', 'Csv', 'Pdf'];
const ENVIRONMENTS = ['', 'Production', 'Staging', 'Test', 'Development', 'DisasterRecovery'];
const CRITICALITIES = ['', 'Low', 'Medium', 'High', 'Critical'];

/// Dialog to generate a report from a template: pick a format and (when supported) filters.
function GenerateDialog({ template, allTypes, onClose, onDone }: {
  template: Template | null; allTypes: string[]; onClose: () => void; onDone: () => void;
}) {
  const [name, setName] = useState('');
  const [type, setType] = useState(template?.type ?? 'AssetInventory');
  const [format, setFormat] = useState(template?.defaultFormat ?? 'Excel');
  const [environment, setEnvironment] = useState('');
  const [criticality, setCriticality] = useState('');
  const [seenSinceDays, setSeenSinceDays] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  // A template fixes the type and offers filters; the "custom" path lets any report type be picked.
  const isTemplate = template !== null;
  const supportsFilters = template?.supportsFilters ?? true;

  const submit = async () => {
    setBusy(true);
    setError('');
    const parameters: Record<string, string> = {};
    if (supportsFilters) {
      if (environment) parameters.environment = environment;
      if (criticality) parameters.criticality = criticality;
      if (seenSinceDays.trim()) parameters.seenSinceDays = seenSinceDays.trim();
    }
    try {
      await client.post('/reports/generate', {
        type, format, name: name.trim() || null,
        parameters: Object.keys(parameters).length > 0 ? parameters : null,
      });
      onDone();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Failed to generate report');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={isTemplate ? `Generate — ${template!.name}` : 'Generate custom report'} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        {isTemplate && <p className="muted" style={{ margin: 0 }}>{template!.description}</p>}
        <label className="muted" style={{ fontSize: 12 }}>Report name (optional)
          <input placeholder="Auto-named if left blank" value={name}
            onChange={(e) => setName(e.target.value)} style={{ width: '100%', marginTop: 3 }} />
        </label>
        {!isTemplate && (
          <label className="muted" style={{ fontSize: 12 }}>Report type
            <select value={type} onChange={(e) => setType(e.target.value)} style={{ width: '100%', marginTop: 3 }}>
              {allTypes.map((t) => <option key={t}>{t}</option>)}
            </select>
          </label>
        )}
        <label className="muted" style={{ fontSize: 12 }}>Format
          <select value={format} onChange={(e) => setFormat(e.target.value)} style={{ width: '100%', marginTop: 3 }}>
            {FORMATS.map((f) => <option key={f}>{f}</option>)}
          </select>
        </label>
        {supportsFilters && (
          <>
            <div className="muted" style={{ fontSize: 12, marginTop: 4 }}>Filters (optional)</div>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <select value={environment} onChange={(e) => setEnvironment(e.target.value)}>
                {ENVIRONMENTS.map((t) => <option key={t} value={t}>{t || 'Any environment'}</option>)}
              </select>
              <select value={criticality} onChange={(e) => setCriticality(e.target.value)}>
                {CRITICALITIES.map((t) => <option key={t} value={t}>{t || 'Any criticality'}</option>)}
              </select>
              <input type="number" min={1} placeholder="Seen in last N days" value={seenSinceDays}
                onChange={(e) => setSeenSinceDays(e.target.value)} style={{ width: 160 }} />
            </div>
          </>
        )}
        {error && <div className="error">{error}</div>}
        <div style={{ display: 'flex', gap: 8 }}>
          <button disabled={busy} onClick={submit}>{busy ? 'Generating…' : 'Generate'}</button>
          <button className="secondary" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </Modal>
  );
}

export default function Reports() {
  const [templates, setTemplates] = useState<Template[]>([]);
  const [types, setTypes] = useState<string[]>([]);
  const [reports, setReports] = useState<ReportRow[]>([]);
  const [dialog, setDialog] = useState<Template | 'custom' | null>(null);

  const load = () => client.get('/reports').then((r) => setReports(r.data));
  useEffect(() => {
    client.get('/reports/templates').then((r) => setTemplates(r.data)).catch(() => undefined);
    client.get('/reports/types').then((r) => setTypes(r.data)).catch(() => undefined);
    load();
  }, []);

  const download = async (id: string, filename: string) => {
    const response = await client.get(`/reports/${id}/download`, { responseType: 'blob' });
    const url = URL.createObjectURL(response.data);
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.click();
    URL.revokeObjectURL(url);
  };

  const rename = async (r: ReportRow) => {
    const next = window.prompt('New report name', r.name);
    if (next === null || !next.trim() || next.trim() === r.name) return;
    await client.put(`/reports/${r.id}`, { name: next.trim() });
    load();
  };

  const remove = async (r: ReportRow) => {
    if (!window.confirm(`Delete report "${r.name}"? The generated file is removed too.`)) return;
    await client.delete(`/reports/${r.id}`);
    load();
  };

  return (
    <>
      <div className="card">
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
          <h3>Report templates</h3>
          <button className="secondary" onClick={() => setDialog('custom')}>Custom report…</button>
        </div>
        <div className="grid cols-4">
          {templates.map((t) => (
            <div key={t.key} className="card" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              <h3 style={{ color: 'var(--text)', margin: 0 }}>{t.name}</h3>
              <p className="muted" style={{ fontSize: 12, margin: 0, flex: 1 }}>{t.description}</p>
              <button onClick={() => setDialog(t)}>Generate</button>
            </div>
          ))}
          {templates.length === 0 && <p className="muted">No templates available.</p>}
        </div>
      </div>

      <h2 className="section-title">Generated reports</h2>
      <div className="card">
        <table className="data">
          <thead><tr><th>Name</th><th>Type</th><th>Format</th><th>Status</th>
            <th>Generated</th><th>By</th><th></th></tr></thead>
          <tbody>
            {reports.map((r) => (
              <tr key={r.id}>
                <td>{r.name}</td>
                <td className="muted">{r.type}</td>
                <td className="muted">{r.format}</td>
                <td>
                  <Badge value={r.status} />
                  {r.error && <div className="error" style={{ fontSize: 11 }}>{r.error}</div>}
                </td>
                <td className="muted">{formatDate(r.generatedAt)}</td>
                <td className="muted">{r.generatedBy}</td>
                <td>
                  <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                    {r.status === 'Succeeded' && (
                      <button className="secondary"
                        onClick={() => download(r.id, `${r.name}.${r.format.toLowerCase()}`)}>
                        Download
                      </button>
                    )}
                    <button className="secondary" onClick={() => rename(r)}>Rename</button>
                    <button className="danger" onClick={() => remove(r)}>Delete</button>
                  </div>
                </td>
              </tr>
            ))}
            {reports.length === 0 && <tr><td colSpan={7} className="muted">No reports generated yet.</td></tr>}
          </tbody>
        </table>
      </div>

      {dialog && (
        <GenerateDialog
          template={dialog === 'custom' ? null : dialog}
          allTypes={types}
          onClose={() => setDialog(null)}
          onDone={() => { setDialog(null); load(); }}
        />
      )}
    </>
  );
}
