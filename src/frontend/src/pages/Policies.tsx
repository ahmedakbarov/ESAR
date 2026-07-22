import { useEffect, useState } from 'react';
import client from '../api/client';
import { Badge, ChipListInput } from '../components/Ui';

const ALL_CONTROLS = ['SiemLogSource', 'Edr', 'Antivirus', 'VulnerabilityScanner', 'MonitoringAgent',
  'BackupAgent', 'PatchStatus', 'DiskEncryption', 'AssetClassification'];
const ALL_TYPES = ['WindowsServer', 'LinuxServer', 'VirtualMachine', 'PhysicalServer', 'Workstation',
  'CloudInstance', 'Container', 'KubernetesNode', 'NetworkDevice', 'Firewall', 'LoadBalancer',
  'Switch', 'Router', 'Database', 'Application', 'StorageSystem'];
const ENVIRONMENTS = ['Production', 'Staging', 'Test', 'Development', 'DisasterRecovery'];
const CRITICALITIES = ['Low', 'Medium', 'High', 'Critical'];
const ALL_CONNECTORS = ['Azure', 'EntraId', 'ActiveDirectory', 'Aws', 'GoogleCloud', 'VmwareVCenter', 'HyperV',
  'MicrosoftDefender', 'CortexXdr', 'CrowdStrike', 'SentinelOne', 'Qualys', 'Rapid7', 'Tenable', 'Nessus',
  'MicrosoftSentinel', 'QRadar', 'Splunk', 'Elastic', 'ServiceNowCmdb', 'Jira', 'Dns', 'Dhcp', 'Sccm', 'Intune',
  'GenericRest', 'ManualImport'];

// AD group membership is stored as a plain tag (see ActiveDirectoryConnector) with this key prefix —
// the UI presents it as its own "AD groups" field, but it lives in appliesToTags on the wire.
const AD_GROUP_PREFIX = 'adgroup:';

interface PolicyForm {
  id?: string;
  name: string;
  description: string;
  enabled: boolean;
  priority: number;
  appliesToAssetTypes: string[];
  appliesToEnvironments: string[];
  minCriticality: string; // '' = any
  appliesToConnectors: string[];
  appliesToTags: string[];
  appliesToHostnamePatterns: string[];
  appliesToIpRanges: string[];
  appliesToSubscriptions: string[];
  requiredControls: string[];
  mandatoryControls: string[];
}

const emptyForm: PolicyForm = {
  name: '', description: '', enabled: true, priority: 100,
  appliesToAssetTypes: [], appliesToEnvironments: [], minCriticality: '',
  appliesToConnectors: [], appliesToTags: [], appliesToHostnamePatterns: [],
  appliesToIpRanges: [], appliesToSubscriptions: [],
  requiredControls: [], mandatoryControls: [],
};

interface ScopeLike {
  appliesToAssetTypes?: string[]; appliesToEnvironments?: string[]; minCriticality?: string | null;
  appliesToConnectors?: string[]; appliesToTags?: string[]; appliesToHostnamePatterns?: string[];
  appliesToIpRanges?: string[]; appliesToSubscriptions?: string[];
}

function plural(n: number, word: string) { return `${n} ${word}${n === 1 ? '' : 's'}`; }

/// One-line, at-a-glance read of how constrained a policy's scope is — used in the edit form and the list table.
function summarizeScope(p: ScopeLike): string {
  const parts: string[] = [];
  if (p.appliesToAssetTypes?.length) parts.push(plural(p.appliesToAssetTypes.length, 'asset type'));
  if (p.appliesToEnvironments?.length) parts.push(p.appliesToEnvironments.join('/'));
  if (p.minCriticality) parts.push(`${p.minCriticality}+`);
  if (p.appliesToConnectors?.length) parts.push(plural(p.appliesToConnectors.length, 'connector'));
  const tags = p.appliesToTags ?? [];
  const adGroupCount = tags.filter((t) => t.toLowerCase().startsWith(AD_GROUP_PREFIX)).length;
  const tagCount = tags.length - adGroupCount;
  if (tagCount > 0) parts.push(plural(tagCount, 'tag'));
  if (adGroupCount > 0) parts.push(plural(adGroupCount, 'AD group'));
  if (p.appliesToHostnamePatterns?.length) parts.push(plural(p.appliesToHostnamePatterns.length, 'hostname pattern'));
  if (p.appliesToIpRanges?.length) parts.push(plural(p.appliesToIpRanges.length, 'IP range'));
  if (p.appliesToSubscriptions?.length) parts.push(plural(p.appliesToSubscriptions.length, 'subscription'));
  return parts.length === 0 ? 'all assets' : parts.join(' · ');
}

export default function Policies() {
  const [policies, setPolicies] = useState<any[]>([]);
  const [form, setForm] = useState<PolicyForm | null>(null);
  const [error, setError] = useState('');

  const load = () => client.get('/policies').then((r) => setPolicies(r.data));
  useEffect(() => { load(); }, []);

  const toggle = (list: string[], value: string) =>
    list.includes(value) ? list.filter((v) => v !== value) : [...list, value];

  // appliesToTags mixes plain "key"/"key=value" filters with adgroup:-prefixed entries; the form
  // presents them as two separate chip lists but they save into the same backend field.
  const plainTags = (form?.appliesToTags ?? []).filter((t) => !t.toLowerCase().startsWith(AD_GROUP_PREFIX));
  const adGroups = (form?.appliesToTags ?? [])
    .filter((t) => t.toLowerCase().startsWith(AD_GROUP_PREFIX))
    .map((t) => t.slice(AD_GROUP_PREFIX.length));

  const setPlainTags = (vals: string[]) => {
    if (!form) return;
    setForm({ ...form, appliesToTags: [...vals, ...adGroups.map((g) => AD_GROUP_PREFIX + g)] });
  };
  const setAdGroups = (vals: string[]) => {
    if (!form) return;
    setForm({ ...form, appliesToTags: [...plainTags, ...vals.map((g) => AD_GROUP_PREFIX + g.toLowerCase())] });
  };

  const save = async () => {
    if (!form) return;
    setError('');
    const payload = {
      name: form.name,
      description: form.description,
      enabled: form.enabled,
      priority: form.priority,
      appliesToAssetTypes: form.appliesToAssetTypes,
      appliesToEnvironments: form.appliesToEnvironments,
      minCriticality: form.minCriticality || null,
      appliesToConnectors: form.appliesToConnectors,
      appliesToTags: form.appliesToTags,
      appliesToHostnamePatterns: form.appliesToHostnamePatterns,
      appliesToIpRanges: form.appliesToIpRanges,
      appliesToSubscriptions: form.appliesToSubscriptions,
      requiredControls: form.requiredControls,
      mandatoryControls: form.mandatoryControls,
    };
    try {
      if (form.id) await client.put(`/policies/${form.id}`, payload);
      else await client.post('/policies', payload);
      setForm(null);
      load();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Save failed');
    }
  };

  const remove = async (id: string, name: string) => {
    if (!window.confirm(`Delete policy "${name}"? This cannot be undone.`)) return;
    setError('');
    try {
      await client.delete(`/policies/${id}`);
      if (form?.id === id) setForm(null);
      load();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Delete failed');
    }
  };

  const edit = (p: any) => setForm({
    id: p.id, name: p.name, description: p.description ?? '', enabled: p.enabled, priority: p.priority,
    appliesToAssetTypes: p.appliesToAssetTypes ?? [],
    appliesToEnvironments: p.appliesToEnvironments ?? [],
    minCriticality: p.minCriticality ?? '',
    appliesToConnectors: p.appliesToConnectors ?? [],
    appliesToTags: p.appliesToTags ?? [],
    appliesToHostnamePatterns: p.appliesToHostnamePatterns ?? [],
    appliesToIpRanges: p.appliesToIpRanges ?? [],
    appliesToSubscriptions: p.appliesToSubscriptions ?? [],
    requiredControls: p.requiredControls ?? [], mandatoryControls: p.mandatoryControls ?? [],
  });

  return (
    <>
      <div className="card">
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
          <h3>Security Baseline Policies</h3>
          <button onClick={() => setForm(form ? null : { ...emptyForm })}>
            {form ? 'Cancel' : 'New policy'}
          </button>
        </div>
        <p className="muted" style={{ marginBottom: 12 }}>
          The first enabled policy (by priority) matching an asset defines which controls it must satisfy.
          Assets not matched by any policy use the full default baseline.
        </p>
        {error && <div className="error" style={{ marginBottom: 12 }}>{error}</div>}

        {form && (
          <div className="card" style={{ marginBottom: 16 }}>
            <div className="filters">
              <input placeholder="Policy name" value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })} style={{ width: 200 }} />
              <input placeholder="Description" value={form.description}
                onChange={(e) => setForm({ ...form, description: e.target.value })} style={{ width: 280 }} />
              <input type="number" title="Priority (lower wins)" value={form.priority}
                onChange={(e) => setForm({ ...form, priority: Number(e.target.value) })} style={{ width: 90 }} />
              <label style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <input type="checkbox" checked={form.enabled}
                  onChange={(e) => setForm({ ...form, enabled: e.target.checked })} /> Enabled
              </label>
            </div>

            <p className="muted" style={{ margin: '4px 0 14px', fontSize: 13 }}>Scope: {summarizeScope(form)}</p>

            <details open={form.appliesToAssetTypes.length > 0} style={{ marginBottom: 10 }}>
              <summary>Asset types (empty = all)</summary>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, margin: '8px 0' }}>
                {ALL_TYPES.map((t) => (
                  <button key={t} type="button"
                    className={form.appliesToAssetTypes.includes(t) ? '' : 'secondary'}
                    onClick={() => setForm({ ...form, appliesToAssetTypes: toggle(form.appliesToAssetTypes, t) })}>
                    {t}
                  </button>
                ))}
              </div>
            </details>

            <details open={form.appliesToEnvironments.length > 0} style={{ marginBottom: 10 }}>
              <summary>Environments (empty = all)</summary>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, margin: '8px 0' }}>
                {ENVIRONMENTS.map((t) => (
                  <button key={t} type="button"
                    className={form.appliesToEnvironments.includes(t) ? '' : 'secondary'}
                    onClick={() => setForm({ ...form, appliesToEnvironments: toggle(form.appliesToEnvironments, t) })}>
                    {t}
                  </button>
                ))}
              </div>
            </details>

            <details open={!!form.minCriticality} style={{ marginBottom: 10 }}>
              <summary>Minimum criticality (empty = any)</summary>
              <div style={{ margin: '8px 0' }}>
                <select value={form.minCriticality}
                  onChange={(e) => setForm({ ...form, minCriticality: e.target.value })}>
                  <option value="">(any)</option>
                  {CRITICALITIES.map((c) => <option key={c} value={c}>{c}+</option>)}
                </select>
              </div>
            </details>

            <details open={form.appliesToConnectors.length > 0} style={{ marginBottom: 10 }}>
              <summary>Connectors (empty = any source)</summary>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, margin: '8px 0' }}>
                {ALL_CONNECTORS.map((t) => (
                  <button key={t} type="button"
                    className={form.appliesToConnectors.includes(t) ? '' : 'secondary'}
                    onClick={() => setForm({ ...form, appliesToConnectors: toggle(form.appliesToConnectors, t) })}>
                    {t}
                  </button>
                ))}
              </div>
            </details>

            <details open={plainTags.length > 0} style={{ marginBottom: 10 }}>
              <summary>Tags (empty = no tag constraint)</summary>
              <div style={{ margin: '8px 0' }}>
                <ChipListInput values={plainTags} onChange={setPlainTags}
                  placeholder="key or key=value, e.g. env=prod — Enter to add" />
              </div>
            </details>

            <details open={adGroups.length > 0} style={{ marginBottom: 10 }}>
              <summary>AD groups (empty = any)</summary>
              <div style={{ margin: '8px 0' }}>
                <ChipListInput values={adGroups} onChange={setAdGroups}
                  placeholder="AD group name, e.g. Domain Admins — Enter to add" />
              </div>
            </details>

            <details open={form.appliesToHostnamePatterns.length > 0} style={{ marginBottom: 10 }}>
              <summary>Hostname patterns (empty = any)</summary>
              <div style={{ margin: '8px 0' }}>
                <ChipListInput values={form.appliesToHostnamePatterns}
                  onChange={(v) => setForm({ ...form, appliesToHostnamePatterns: v })}
                  placeholder="glob pattern, e.g. prod-db-* — Enter to add" />
              </div>
            </details>

            <details open={form.appliesToIpRanges.length > 0} style={{ marginBottom: 10 }}>
              <summary>IP ranges (empty = any)</summary>
              <div style={{ margin: '8px 0' }}>
                <ChipListInput values={form.appliesToIpRanges}
                  onChange={(v) => setForm({ ...form, appliesToIpRanges: v })}
                  placeholder="CIDR, e.g. 10.0.0.0/8 — Enter to add" />
              </div>
            </details>

            <details open={form.appliesToSubscriptions.length > 0} style={{ marginBottom: 10 }}>
              <summary>Cloud subscriptions (empty = any)</summary>
              <div style={{ margin: '8px 0' }}>
                <ChipListInput values={form.appliesToSubscriptions}
                  onChange={(v) => setForm({ ...form, appliesToSubscriptions: v })}
                  placeholder="subscription id — Enter to add" />
              </div>
            </details>

            <h3 style={{ marginTop: 10 }}>Required controls</h3>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 10 }}>
              {ALL_CONTROLS.map((c) => (
                <button key={c} type="button"
                  className={form.requiredControls.includes(c) ? '' : 'secondary'}
                  onClick={() => setForm({ ...form, requiredControls: toggle(form.requiredControls, c) })}>
                  {c}
                </button>
              ))}
            </div>
            <h3>Mandatory controls (failure ⇒ NonCompliant)</h3>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 10 }}>
              {form.requiredControls.map((c) => (
                <button key={c} type="button"
                  className={form.mandatoryControls.includes(c) ? 'danger' : 'secondary'}
                  onClick={() => setForm({ ...form, mandatoryControls: toggle(form.mandatoryControls, c) })}>
                  {c}
                </button>
              ))}
            </div>
            <button onClick={save}>Save policy</button>
          </div>
        )}

        <table className="data">
          <thead>
            <tr><th>Priority</th><th>Name</th><th>Scope</th><th>Required</th>
              <th>Mandatory</th><th>Version</th><th>Enabled</th><th></th></tr>
          </thead>
          <tbody>
            {policies.map((p) => {
              const scope = summarizeScope(p);
              return (
                <tr key={p.id}>
                  <td>{p.priority}</td>
                  <td>{p.name}<div className="muted" style={{ fontSize: 11 }}>{p.description}</div></td>
                  <td className="muted" title={scope}
                    style={{ maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                    {scope}
                  </td>
                  <td className="muted">{(p.requiredControls ?? []).join(', ')}</td>
                  <td style={{ color: 'var(--red)' }}>{(p.mandatoryControls ?? []).join(', ')}</td>
                  <td className="muted">v{p.version}</td>
                  <td>{p.enabled ? <Badge value="Active" /> : <Badge value="Inactive" />}</td>
                  <td>
                    <div style={{ display: 'flex', gap: 6 }}>
                      <button className="secondary" onClick={() => edit(p)}>Edit</button>
                      <button className="danger" onClick={() => remove(p.id, p.name)}>Delete</button>
                    </div>
                  </td>
                </tr>
              );
            })}
            {policies.length === 0 && (
              <tr><td colSpan={8} className="muted">
                No policies configured yet — assets use the full default control baseline.
              </td></tr>
            )}
          </tbody>
        </table>
      </div>
    </>
  );
}
