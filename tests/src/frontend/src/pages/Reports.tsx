import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

export default function Reports() {
  const [types, setTypes] = useState<string[]>([]);
  const [reports, setReports] = useState<any[]>([]);
  const [type, setType] = useState('AssetInventory');
  const [format, setFormat] = useState('Excel');
  const [busy, setBusy] = useState(false);

  const load = () => client.get('/reports').then((r) => setReports(r.data));
  useEffect(() => {
    client.get('/reports/types').then((r) => setTypes(r.data));
    load();
  }, []);

  const generate = async () => {
    setBusy(true);
    try { await client.post('/reports/generate', { type, format }); } finally { setBusy(false); load(); }
  };

  const download = async (id: string, name: string) => {
    const response = await client.get(`/reports/${id}/download`, { responseType: 'blob' });
    const url = URL.createObjectURL(response.data);
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    URL.revokeObjectURL(url);
  };

  return (
    <>
      <div className="card">
        <div className="filters">
          <select value={type} onChange={(e) => setType(e.target.value)}>
            {types.map((t) => <option key={t}>{t}</option>)}
          </select>
          <select value={format} onChange={(e) => setFormat(e.target.value)}>
            {['Excel', 'Csv', 'Pdf'].map((f) => <option key={f}>{f}</option>)}
          </select>
          <button disabled={busy} onClick={generate}>{busy ? 'Generating…' : 'Generate report'}</button>
        </div>
      </div>

      <h2 className="section-title">Generated Reports</h2>
      <div className="card">
        <table className="data">
          <thead><tr><th>Name</th><th>Type</th><th>Format</th><th>Status</th><th>Generated</th><th>By</th><th></th></tr></thead>
          <tbody>
            {reports.map((r) => (
              <tr key={r.id}>
                <td>{r.name}</td>
                <td className="muted">{r.type}</td>
                <td className="muted">{r.format}</td>
                <td><Badge value={r.status} /></td>
                <td className="muted">{formatDate(r.generatedAt)}</td>
                <td className="muted">{r.generatedBy}</td>
                <td>
                  {r.status === 'Succeeded' && (
                    <button className="secondary" onClick={() => download(r.id, `${r.name}.${r.format.toLowerCase()}`)}>
                      Download
                    </button>
                  )}
                  {r.error && <span className="error">{r.error}</span>}
                </td>
              </tr>
            ))}
            {reports.length === 0 && <tr><td colSpan={7} className="muted">No reports generated yet.</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  );
}
