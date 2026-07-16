import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import client from '../api/client';
import { Badge, Panel, StatCard, formatDate } from '../components/Ui';

export default function AssetDetail() {
  const { id } = useParams();
  const [asset, setAsset] = useState<any>(null);
  const [history, setHistory] = useState<any[]>([]);
  const [relationships, setRelationships] = useState<any[]>([]);
  const [impact, setImpact] = useState<any>(null);

  useEffect(() => {
    client.get(`/assets/${id}`).then((r) => setAsset(r.data));
    client.get(`/assets/${id}/history`).then((r) => setHistory(r.data));
    client.get(`/relationships/asset/${id}`).then((r) => setRelationships(r.data));
    setImpact(null);
  }, [id]);

  const analyzeImpact = () =>
    client.get(`/relationships/asset/${id}/impact?depth=3`).then((r) => setImpact(r.data));

  if (!asset) return <div className="muted">Loading…</div>;

  const scoreTone = (v: number) => (v >= 80 ? 'good' : v >= 50 ? 'warn' : 'bad');

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

      <div className="grid cols-4" style={{ marginBottom: 16 }}>
        <StatCard label="Compliance" value={`${asset.complianceScore}%`}
          tone={scoreTone(asset.complianceScore)} hint={asset.complianceStatus} />
        <StatCard label="Health" value={asset.healthScore ?? '—'}
          tone={scoreTone(asset.healthScore ?? 0)} />
        <StatCard label="Data Quality" value={`${asset.dataQualityScore ?? '—'}`}
          tone={scoreTone(asset.dataQualityScore ?? 0)} />
        <StatCard label="Risk" value={asset.risk?.riskScore ?? '—'}
          tone={asset.risk && asset.risk.riskScore >= 60 ? 'bad' : asset.risk && asset.risk.riskScore >= 35 ? 'warn' : 'good'}
          hint={asset.risk ? `${asset.risk.critical} crit / ${asset.risk.high} high vulns` : 'not calculated yet'} />
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

      <h2 className="section-title">Relationships &amp; Impact</h2>
      <div className="grid cols-2">
        <Panel title="Relationships">
          <table className="data">
            <thead><tr><th>Source</th><th>Type</th><th>Target</th><th>Origin</th></tr></thead>
            <tbody>
              {relationships.map((r) => (
                <tr key={r.id}>
                  <td>{r.sourceAssetId === id ? asset.hostname
                    : <Link to={`/assets/${r.sourceAssetId}`}>{r.sourceHostname ?? 'asset'}</Link>}</td>
                  <td><Badge value={r.type} /></td>
                  <td>{r.targetAssetId === id ? asset.hostname
                    : <Link to={`/assets/${r.targetAssetId}`}>{r.targetHostname ?? 'asset'}</Link>}</td>
                  <td className="muted">{r.source}</td>
                </tr>
              ))}
              {relationships.length === 0 &&
                <tr><td colSpan={4} className="muted">No relationships recorded.</td></tr>}
            </tbody>
          </table>
        </Panel>
        <Panel title="Impact Analysis"
          actions={<button className="secondary" onClick={analyzeImpact}>Analyze</button>}>
          {!impact && <div className="muted">Run the analysis to see the blast radius and dependencies.</div>}
          {impact && (
            <>
              <h3 style={{ marginTop: 8 }}>Impacted if this asset fails ({impact.impactedAssets.length})</h3>
              {impact.impactedAssets.map((n: any) => (
                <div key={n.assetId} style={{ marginBottom: 4 }}>
                  <Link to={`/assets/${n.assetId}`}>{n.hostname}</Link>{' '}
                  <Badge value={n.criticality} />{' '}
                  <span className="muted" style={{ fontSize: 11 }}>{n.relationshipPath}</span>
                </div>
              ))}
              {impact.impactedAssets.length === 0 && <div className="muted">None.</div>}
              <h3 style={{ marginTop: 12 }}>Depends on ({impact.dependencies.length})</h3>
              {impact.dependencies.map((n: any) => (
                <div key={n.assetId} style={{ marginBottom: 4 }}>
                  <Link to={`/assets/${n.assetId}`}>{n.hostname}</Link>{' '}
                  <span className="muted" style={{ fontSize: 11 }}>{n.relationshipPath}</span>
                </div>
              ))}
              {impact.dependencies.length === 0 && <div className="muted">None.</div>}
            </>
          )}
        </Panel>
      </div>

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
