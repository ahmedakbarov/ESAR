import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import client from '../api/client';
import { Badge, Panel } from '../components/Ui';

const CONTROLS = ['SiemLogSource', 'Edr', 'Antivirus', 'VulnerabilityScanner', 'MonitoringAgent',
  'BackupAgent', 'PatchStatus', 'DiskEncryption', 'AssetClassification'];

export default function Compliance() {
  const [summary, setSummary] = useState<any>(null);
  const [control, setControl] = useState('SiemLogSource');
  const [failing, setFailing] = useState<any[]>([]);

  useEffect(() => { client.get('/compliance/summary').then((r) => setSummary(r.data)); }, []);
  useEffect(() => {
    client.get(`/compliance/failing/${control}`).then((r) => setFailing(r.data));
  }, [control]);

  return (
    <>
      {summary && (
        <div className="grid cols-4">
          {Object.entries(summary.byStatus ?? {}).map(([status, count]) => (
            <Panel key={status} title={status}>
              <div className="stat"><div className="value">{count as number}</div></div>
            </Panel>
          ))}
        </div>
      )}

      {summary && (
        <>
          <h2 className="section-title">Control Coverage</h2>
          <div className="card">
            <table className="data">
              <thead>
                <tr><th>Control</th><th>Compliant</th><th>Non-Compliant</th><th>Pending</th><th>Unknown</th></tr>
              </thead>
              <tbody>
                {Object.entries(summary.byControl ?? {}).map(([name, stats]: [string, any]) => (
                  <tr key={name}>
                    <td>{name}</td>
                    <td style={{ color: 'var(--green)' }}>{stats.compliant}</td>
                    <td style={{ color: 'var(--red)' }}>{stats.nonCompliant}</td>
                    <td style={{ color: 'var(--amber)' }}>{stats.pending}</td>
                    <td className="muted">{stats.unknown}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      <h2 className="section-title">Assets Failing Control</h2>
      <div className="card">
        <div className="filters">
          <select value={control} onChange={(e) => setControl(e.target.value)}>
            {CONTROLS.map((c) => <option key={c}>{c}</option>)}
          </select>
        </div>
        <table className="data">
          <thead>
            <tr><th>Hostname</th><th>Type</th><th>Environment</th><th>Criticality</th><th>Owner</th><th>Score</th></tr>
          </thead>
          <tbody>
            {failing.map((a) => (
              <tr key={a.id}>
                <td><Link to={`/assets/${a.id}`}>{a.hostname}</Link></td>
                <td>{a.assetType}</td>
                <td><Badge value={a.environment} /></td>
                <td><Badge value={a.criticality} /></td>
                <td className="muted">{a.ownerName ?? '—'}</td>
                <td>{a.complianceScore}%</td>
              </tr>
            ))}
            {failing.length === 0 && <tr><td colSpan={6} className="muted">No assets failing this control.</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  );
}
