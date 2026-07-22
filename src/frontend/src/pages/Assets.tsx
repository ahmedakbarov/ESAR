import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import client, { AssetDto, PagedResult } from '../api/client';
import { Badge, formatDate } from '../components/Ui';

const ASSET_TYPES = ['', 'WindowsServer', 'LinuxServer', 'VirtualMachine', 'PhysicalServer', 'Workstation',
  'CloudInstance', 'Container', 'KubernetesNode', 'NetworkDevice', 'Firewall', 'LoadBalancer',
  'Switch', 'Router', 'Database', 'Application', 'StorageSystem'];
const ENVIRONMENTS = ['', 'Production', 'Staging', 'Test', 'Development', 'DisasterRecovery'];
const COMPLIANCE = ['', 'Compliant', 'NonCompliant', 'Pending', 'Unknown'];
const CRITICALITIES = ['', 'Low', 'Medium', 'High', 'Critical'];
const STATUSES = ['', 'Active', 'Inactive', 'Offline', 'Quarantined', 'Decommissioned'];
const CONNECTORS = ['', 'Azure', 'EntraId', 'ActiveDirectory', 'Aws', 'GoogleCloud', 'VmwareVCenter', 'HyperV',
  'MicrosoftDefender', 'CortexXdr', 'CrowdStrike', 'SentinelOne', 'Qualys', 'Rapid7', 'Tenable', 'Nessus',
  'MicrosoftSentinel', 'QRadar', 'Splunk', 'Elastic', 'ServiceNowCmdb', 'Jira', 'Dns', 'Dhcp', 'Sccm', 'Intune',
  'GenericRest', 'ManualImport'];

export default function Assets() {
  const [result, setResult] = useState<PagedResult<AssetDto> | null>(null);
  const [search, setSearch] = useState('');
  const [assetType, setAssetType] = useState('');
  const [environment, setEnvironment] = useState('');
  const [compliance, setCompliance] = useState('');
  const [criticality, setCriticality] = useState('');
  const [status, setStatus] = useState('');
  const [source, setSource] = useState('');
  const [policyExempt, setPolicyExempt] = useState('');
  const [tagKey, setTagKey] = useState('');
  const [tagValue, setTagValue] = useState('');
  const [showMore, setShowMore] = useState(false);
  const [page, setPage] = useState(1);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState('');

  const load = () => {
    const params = new URLSearchParams({ page: String(page), pageSize: '25' });
    if (search) params.set('search', search);
    if (assetType) params.set('assetType', assetType);
    if (environment) params.set('environment', environment);
    if (compliance) params.set('complianceStatus', compliance);
    if (criticality) params.set('criticality', criticality);
    if (status) params.set('status', status);
    if (source) params.set('source', source);
    if (policyExempt) params.set('policyExempt', policyExempt);
    if (tagKey) params.set('tagKey', tagKey);
    if (tagKey && tagValue) params.set('tagValue', tagValue);
    client.get(`/assets?${params}`).then((r) => setResult(r.data));
  };

  useEffect(load, [page, assetType, environment, compliance, criticality, status, source, policyExempt]);

  const remove = async (id: string, hostname: string) => {
    if (!window.confirm(`Delete asset "${hostname}"? It will be marked decommissioned and hidden from all views. This cannot be undone from the UI.`)) return;
    setError('');
    setBusy(id);
    try {
      await client.delete(`/assets/${id}`);
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Delete failed');
    } finally {
      setBusy(null);
      load();
    }
  };

  const enablePolicy = async (id: string) => {
    setError('');
    setBusy(id);
    try {
      await client.put(`/assets/${id}`, { policyExempt: false });
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Failed to re-enable policy evaluation');
    } finally {
      setBusy(null);
      load();
    }
  };

  return (
    <div className="card">
      <div className="filters">
        <input
          placeholder="Search hostname, IP, owner, serial…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (setPage(1), load())}
          style={{ width: 280 }}
        />
        <select value={assetType} onChange={(e) => { setAssetType(e.target.value); setPage(1); }}>
          {ASSET_TYPES.map((t) => <option key={t} value={t}>{t || 'All types'}</option>)}
        </select>
        <select value={environment} onChange={(e) => { setEnvironment(e.target.value); setPage(1); }}>
          {ENVIRONMENTS.map((t) => <option key={t} value={t}>{t || 'All environments'}</option>)}
        </select>
        <select value={compliance} onChange={(e) => { setCompliance(e.target.value); setPage(1); }}>
          {COMPLIANCE.map((t) => <option key={t} value={t}>{t || 'All compliance'}</option>)}
        </select>
        <button onClick={() => { setPage(1); load(); }}>Search</button>
        <button className="secondary" onClick={() => setShowMore(!showMore)}>
          {showMore ? 'Fewer filters' : 'More filters'}
        </button>
      </div>

      {showMore && (
        <div className="filters">
          <select value={criticality} onChange={(e) => { setCriticality(e.target.value); setPage(1); }}>
            {CRITICALITIES.map((t) => <option key={t} value={t}>{t || 'All criticality'}</option>)}
          </select>
          <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            {STATUSES.map((t) => <option key={t} value={t}>{t || 'All statuses'}</option>)}
          </select>
          <select value={source} onChange={(e) => { setSource(e.target.value); setPage(1); }}>
            {CONNECTORS.map((t) => <option key={t} value={t}>{t || 'All connectors'}</option>)}
          </select>
          <select value={policyExempt} onChange={(e) => { setPolicyExempt(e.target.value); setPage(1); }}>
            <option value="">Exempt: any</option>
            <option value="true">Exempt only</option>
            <option value="false">Not exempt</option>
          </select>
          <input placeholder="Tag key" value={tagKey}
            onChange={(e) => setTagKey(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && (setPage(1), load())}
            style={{ width: 120 }} />
          <input placeholder="Tag value (optional)" value={tagValue}
            onChange={(e) => setTagValue(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && (setPage(1), load())}
            style={{ width: 150 }} />
          <button onClick={() => { setPage(1); load(); }}>Apply</button>
        </div>
      )}

      {error && <div className="error" style={{ marginBottom: 10 }}>{error}</div>}
      <table className="data">
        <thead>
          <tr>
            <th>Hostname</th><th>Type</th><th>OS</th><th>IP</th><th>Environment</th>
            <th>Criticality</th><th>Compliance</th><th>Sources</th><th>Last Seen</th><th></th>
          </tr>
        </thead>
        <tbody>
          {result?.items.map((a) => (
            <tr key={a.id}>
              <td>
                <Link to={`/assets/${a.id}`}>{a.hostname}</Link>
                {a.policyExempt && <> <Badge value="Policy Exempt" /></>}
              </td>
              <td>{a.assetType}</td>
              <td>{a.operatingSystem ?? '—'}</td>
              <td>{a.primaryIp ?? '—'}</td>
              <td><Badge value={a.environment} /></td>
              <td><Badge value={a.criticality} /></td>
              <td><Badge value={a.complianceStatus} /> <span className="muted">{a.complianceScore}%</span></td>
              <td className="muted">{a.sources.join(', ')}</td>
              <td className="muted">{formatDate(a.lastSeen)}</td>
              <td>
                <div style={{ display: 'flex', gap: 6 }}>
                  {a.policyExempt && (
                    <button className="secondary" disabled={busy === a.id} onClick={() => enablePolicy(a.id)}>
                      Enable
                    </button>
                  )}
                  <button className="danger" disabled={busy === a.id} onClick={() => remove(a.id, a.hostname)}>
                    Delete
                  </button>
                </div>
              </td>
            </tr>
          ))}
          {result && result.items.length === 0 && (
            <tr><td colSpan={10} className="muted">No assets found.</td></tr>
          )}
        </tbody>
      </table>

      {result && (
        <div className="pagination">
          <button className="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Prev</button>
          <span>Page {result.page} of {result.totalPages || 1} — {result.totalCount} assets</span>
          <button className="secondary" disabled={page >= result.totalPages} onClick={() => setPage(page + 1)}>Next</button>
        </div>
      )}
    </div>
  );
}
