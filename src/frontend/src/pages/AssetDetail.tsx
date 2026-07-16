import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import client from '../api/client';
import { Badge, Panel, formatDate } from '../components/Ui';

export default function AssetDetail() {
  const { id } = useParams();
  const [asset, setAsset] = useState<any>(null);
  const [history, setHistory] = useState<any[]>([]);

  useEffect(() => {
    client.get(`/assets/${id}`).then((r) => setAsset(r.data));
    client.get(`/assets/${id}/history`).then((r) => setHistory(r.data));
  }, [id]);

  if (!asset) return <div className="muted">Loading…</div>;

  return (
    <>
      <div className="topbar">
        <h1>{asset.hostname} <Badge value={asset.status} /></h1>
        <button
          className="secondary"
          onClick={() => client.post(`/compliance/assets/${id}/evaluate`).then(() =>
            client.get(`/assets/${id}`).then((r) => setAsset(r.data)))}
        >
          Re-evaluate compliance
        </button>
      </div>

      <div className="grid cols-2">
        <Panel title="General">
          <dl className="kv">
            <dt>FQDN</dt><dd>{asset.fqdn ?? '—'}</dd>
            <dt>Operating System</dt><dd>{asset.operatingSystem ?? '—'} {asset.osVersion ?? ''}</dd>
            <dt>Type</dt><dd>{asset.assetType}</dd>
            <dt>Environment</dt><dd><Badge value={asset.environment} /></dd>
            <dt>Criticality</dt><dd><Badge value={asset.criticality} /></dd>
            <dt>Lifecycle</dt><dd>{asset.lifecycleStatus}</dd>
            <dt>Owner</dt><dd>{asset.ownerName ?? '—'} {asset.ownerEmail ? `(${asset.ownerEmail})` : ''}</dd>
            <dt>Business Unit</dt><dd>{asset.businessUnit ?? '—'}</dd>
            <dt>Department</dt><dd>{asset.department ?? '—'}</dd>
            <dt>Location</dt><dd>{asset.location ?? '—'}</dd>
            <dt>Classification</dt><dd>{asset.classification ?? '—'}</dd>
            <dt>First / Last Seen</dt><dd>{formatDate(asset.firstSeen)} / {formatDate(asset.lastSeen)}</dd>
          </dl>
        </Panel>
        <Panel title="Hardware & Cloud">
          <dl className="kv">
            <dt>Serial Number</dt><dd>{asset.serialNumber ?? '—'}</dd>
            <dt>BIOS UUID</dt><dd>{asset.biosUuid ?? '—'}</dd>
            <dt>Manufacturer</dt><dd>{asset.manufacturer ?? '—'}</dd>
            <dt>Model</dt><dd>{asset.model ?? '—'}</dd>
            <dt>Cloud Provider</dt><dd>{asset.cloudProvider ?? '—'}</dd>
            <dt>Cloud Region</dt><dd>{asset.cloudRegion ?? '—'}</dd>
            <dt>Subscription</dt><dd>{asset.cloudSubscriptionId ?? '—'}</dd>
            <dt>Resource ID</dt><dd style={{ wordBreak: 'break-all' }}>{asset.cloudResourceId ?? '—'}</dd>
          </dl>
        </Panel>
      </div>

      <h2 className="section-title">Compliance — {asset.complianceScore}% <Badge value={asset.complianceStatus} /></h2>
      <div className="card">
        <table className="data">
          <thead><tr><th>Control</th><th>Status</th><th>Evidence</th><th>Details</th><th>Checked</th></tr></thead>
          <tbody>
            {asset.compliance?.map((c: any, i: number) => (
              <tr key={i}>
                <td>{c.control}</td>
                <td><Badge value={c.status} /></td>
                <td>{c.evidenceSource ?? '—'}</td>
                <td className="muted">{c.details}</td>
                <td className="muted">{formatDate(c.checkedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="grid cols-2" style={{ marginTop: 16 }}>
        <Panel title="Network Interfaces">
          <table className="data">
            <thead><tr><th>IP</th><th>MAC</th><th>Source</th><th>Last Seen</th></tr></thead>
            <tbody>
              {asset.ipAddresses?.map((ip: any, i: number) => (
                <tr key={i}>
                  <td>{ip.ipAddress}{ip.isPrimary ? ' ★' : ''}</td>
                  <td>{ip.macAddress ?? '—'}</td>
                  <td className="muted">{ip.source}</td>
                  <td className="muted">{formatDate(ip.lastSeen)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>
        <Panel title="Data Sources">
          <table className="data">
            <thead><tr><th>Connector</th><th>External ID</th><th>First</th><th>Last</th></tr></thead>
            <tbody>
              {asset.sourceDetails?.map((s: any, i: number) => (
                <tr key={i}>
                  <td><Badge value={s.connector} /></td>
                  <td className="muted" style={{ wordBreak: 'break-all' }}>{s.externalId}</td>
                  <td className="muted">{formatDate(s.firstSeen)}</td>
                  <td className="muted">{formatDate(s.lastSeen)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>
      </div>

      {asset.tags?.length > 0 && (
        <>
          <h2 className="section-title">Tags</h2>
          <div className="card">
            {asset.tags.map((t: any, i: number) => (
              <span key={i} className="badge" style={{ marginRight: 8, marginBottom: 6 }}>
                {t.key}={t.value} <span className="muted">({t.source})</span>
              </span>
            ))}
          </div>
        </>
      )}

      <h2 className="section-title">Change History</h2>
      <div className="card">
        <table className="data">
          <thead><tr><th>Field</th><th>Old</th><th>New</th><th>By</th><th>Source</th><th>When</th></tr></thead>
          <tbody>
            {history.slice(0, 50).map((h, i) => (
              <tr key={i}>
                <td>{h.fieldName}</td>
                <td className="muted">{h.oldValue ?? '—'}</td>
                <td>{h.newValue ?? '—'}</td>
                <td className="muted">{h.changedBy}</td>
                <td className="muted">{h.source ?? '—'}</td>
                <td className="muted">{formatDate(h.changedAt)}</td>
              </tr>
            ))}
            {history.length === 0 && <tr><td colSpan={6} className="muted">No changes recorded.</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  );
}
