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
  settingsText: string; // key=value per line — fallback editor for types without a field layout below
  settingsFields: Record<string, string>; // structured editor for CONNECTOR_FIELDS types
}

const emptyConnector: ConnectorForm = {
  name: '', type: 'ActiveDirectory', enabled: true, cronSchedule: '0 */4 * * *',
  priority: 100, rateLimitPerMinute: 300, defaultSyncMode: 'Incremental',
  settingsText: '', settingsFields: {},
};

function parseSettings(text: string): Record<string, string> {
  const result: Record<string, string> = {};
  text.split('\n').forEach((line) => {
    const idx = line.indexOf('=');
    if (idx > 0) result[line.slice(0, idx).trim()] = line.slice(idx + 1).trim();
  });
  return result;
}

const RAW_SETTINGS_PLACEHOLDER = 'tenantId=...\\nclientId=...\\nclientSecret=...';

// Mirrors ConnectorsController.IsSecret exactly (SecretHints) so a field renders as a password
// input, and shows the same "***" masked-on-edit convention, whenever the backend would encrypt it.
const SECRET_HINTS = ['secret', 'password', 'token', 'apikey', 'accesskey', 'key'];
function isSecretKey(key: string) {
  const lower = key.toLowerCase();
  return SECRET_HINTS.some((hint) => lower.includes(hint));
}

interface SettingField {
  key: string;
  label: string;
  placeholder?: string;
  type?: 'checkbox';
}

// Shared by every Entra ID (Azure AD) app-registration connector — see AadConnectorBase.AcquireTokenAsync.
const AAD_CREDENTIAL_FIELDS: SettingField[] = [
  { key: 'tenantId', label: 'Tenant ID' },
  { key: 'clientId', label: 'Client ID' },
  { key: 'clientSecret', label: 'Client secret' },
];

// Structured per-field layout for every connector type with a real implementation (see
// DependencyInjection.cs's IConnector registrations — /connectors/types only ever lists these).
// Any type without an entry here falls back to the raw key=value textarea below.
const CONNECTOR_FIELDS: Record<string, SettingField[]> = {
  CortexXdr: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://api-<tenant>.xdr.<region>.paloaltonetworks.com' },
    { key: 'apiKeyId', label: 'API Key ID' },
    { key: 'apiKey', label: 'API Key (secret)' },
  ],
  ActiveDirectory: [
    { key: 'server', label: 'Domain controller (FQDN)', placeholder: 'dc01.esar.local' },
    { key: 'baseDn', label: 'Base DN', placeholder: 'DC=esar,DC=local' },
    { key: 'username', label: 'Bind username (UPN)', placeholder: 'svc_esar_ad@esar.local' },
    { key: 'password', label: 'Password' },
    { key: 'port', label: 'Port', placeholder: '636' },
    { key: 'useSsl', label: 'Use LDAPS (required)', type: 'checkbox' },
    { key: 'authType', label: 'Auth type', placeholder: 'Basic' },
    { key: 'timeoutSeconds', label: 'Timeout (seconds)', placeholder: '30' },
    { key: 'resolveDns', label: 'Resolve DNS (needs private AD DNS)', type: 'checkbox' },
    { key: 'dnsTimeoutSeconds', label: 'DNS timeout (seconds)', placeholder: '5' },
    { key: 'dnsMaxConcurrency', label: 'DNS max concurrency', placeholder: '8' },
    { key: 'macAttributes', label: 'MAC attributes (comma-separated LDAP attribute names)' },
  ],
  Azure: [
    ...AAD_CREDENTIAL_FIELDS,
    { key: 'subscriptionIds', label: 'Subscription IDs (comma-separated, empty = all)' },
  ],
  EntraId: AAD_CREDENTIAL_FIELDS,
  Intune: AAD_CREDENTIAL_FIELDS,
  MicrosoftDefender: AAD_CREDENTIAL_FIELDS,
  VmwareVCenter: [
    { key: 'baseUrl', label: 'vCenter URL', placeholder: 'https://vcenter.example.com' },
    { key: 'username', label: 'Username' },
    { key: 'password', label: 'Password' },
  ],
  CrowdStrike: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://api.crowdstrike.com' },
    { key: 'clientId', label: 'Client ID' },
    { key: 'clientSecret', label: 'Client secret' },
  ],
  SentinelOne: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://<tenant>.sentinelone.net' },
    { key: 'apiToken', label: 'API token' },
  ],
  Tenable: [
    { key: 'accessKey', label: 'Access key' },
    { key: 'secretKey', label: 'Secret key' },
  ],
  Qualys: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://gateway.qg1.apps.qualys.com' },
    { key: 'username', label: 'Username' },
    { key: 'password', label: 'Password' },
  ],
  ServiceNowCmdb: [
    { key: 'instanceUrl', label: 'Instance URL', placeholder: 'https://<instance>.service-now.com' },
    { key: 'username', label: 'Username' },
    { key: 'password', label: 'Password' },
    { key: 'table', label: 'CMDB table (optional)', placeholder: 'cmdb_ci_computer' },
  ],
  GenericRest: [
    { key: 'url', label: 'URL' },
    { key: 'authHeader', label: 'Auth header (optional)', placeholder: 'Authorization: Bearer xyz' },
    { key: 'itemsPath', label: 'Items path (optional, dot path to the array; default = response root)' },
    { key: 'idField', label: 'ID field', placeholder: 'id' },
    { key: 'hostnameField', label: 'Hostname field', placeholder: 'hostname' },
    { key: 'osField', label: 'OS field (optional)' },
    { key: 'ipField', label: 'IP field (optional)' },
    { key: 'macField', label: 'MAC field (optional)' },
    { key: 'serialField', label: 'Serial field (optional)' },
  ],
};

const CONNECTOR_HELP: Record<string, string> = {
  ActiveDirectory: 'Use the DC FQDN, LDAPS port 636, and a read-only UPN or bind DN. Resolve DNS needs ' +
    'private AD DNS reachability and adds IP-only evidence.',
  CortexXdr: 'API Key ID and API Key come from Palo Alto Cortex XDR → Settings → API Keys (Standard auth).',
  Azure: 'App registration needs Reader on the subscriptions being synced. Leave Subscription IDs empty ' +
    'to discover every subscription the app registration can see.',
  EntraId: 'App registration needs Directory.Read.All (application permission, admin-consented).',
  Intune: 'App registration needs DeviceManagementManagedDevices.Read.All (application permission, admin-consented).',
  MicrosoftDefender: 'App registration needs Machine.Read.All on the Microsoft Defender for Endpoint API ' +
    '(application permission, admin-consented).',
  VmwareVCenter: 'Read-only vCenter account is enough — this only lists VMs, it never changes anything.',
  CrowdStrike: 'API client needs the Hosts: Read scope. Base URL depends on your CrowdStrike cloud region.',
  SentinelOne: 'API token is generated per-user in Settings → Users, or as a dedicated service user.',
  Tenable: 'Access key and secret key come from Tenable.io → Settings → My Account → API Keys.',
  Qualys: 'Use a Qualys user with the Reader role and API access enabled.',
  ServiceNowCmdb: 'Account needs read access to the CMDB table. Table defaults to cmdb_ci_computer if left blank.',
  GenericRest: 'For any REST API returning a JSON array of assets. Field names map JSON properties in each ' +
    'item to ESAR asset fields — leave optional ones blank if the source does not provide them.',
};

function SettingFieldInput({ field, value, onChange }: {
  field: SettingField; value: string; onChange: (value: string) => void;
}) {
  if (field.type === 'checkbox') {
    return (
      <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
        <input type="checkbox" checked={value === 'true'} onChange={(e) => onChange(String(e.target.checked))} />
        {field.label}
      </label>
    );
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
      <label className="muted" style={{ fontSize: 12 }}>{field.label}</label>
      <input
        type={isSecretKey(field.key) ? 'password' : 'text'}
        placeholder={field.placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        style={{ width: 260 }}
      />
    </div>
  );
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

  const setField = (key: string, value: string) => {
    if (!form) return;
    setForm({ ...form, settingsFields: { ...form.settingsFields, [key]: value } });
  };

  const save = async () => {
    if (!form) return;
    setError('');
    const settings = form.type in CONNECTOR_FIELDS ? form.settingsFields : parseSettings(form.settingsText);
    const payload = {
      name: form.name,
      type: form.type,
      enabled: form.enabled,
      cronSchedule: form.cronSchedule,
      priority: form.priority,
      rateLimitPerMinute: form.rateLimitPerMinute,
      defaultSyncMode: form.defaultSyncMode,
      settings,
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

  const remove = async (id: string, name: string) => {
    if (!window.confirm(`Delete connector "${name}"? Its configuration and run history are removed. This cannot be undone.`)) return;
    setError('');
    setBusy(id);
    try {
      await client.delete(`/connectors/${id}`);
      if (expanded === id) setExpanded(null);
      if (form?.id === id) setForm(null);
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Delete failed');
    } finally {
      setBusy(null);
      load();
    }
  };

  const toggleJobs = async (id: string) => {
    if (expanded === id) { setExpanded(null); return; }
    const { data } = await client.get(`/connectors/${id}/jobs?limit=10`);
    setJobs((prev) => ({ ...prev, [id]: data }));
    setExpanded(id);
  };

  const fields = form ? CONNECTOR_FIELDS[form.type] : undefined;

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
              onChange={(e) => setForm({ ...form, type: e.target.value, settingsFields: {}, settingsText: '' })}>
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

          {CONNECTOR_HELP[form.type] && (
            <p className="muted" style={{ fontSize: 13, marginTop: 10 }}>{CONNECTOR_HELP[form.type]}</p>
          )}

          {fields ? (
            <>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 14, marginTop: 8 }}>
                {fields.map((f) => (
                  <SettingFieldInput key={f.key} field={f}
                    value={form.settingsFields[f.key] ?? ''}
                    onChange={(v) => setField(f.key, v)} />
                ))}
              </div>
              {form.id && (
                <p className="muted" style={{ fontSize: 12, marginTop: 8 }}>
                  Secret fields show as *** when editing — leave as-is to keep the stored value, or
                  type a new one to replace it.
                </p>
              )}
            </>
          ) : (
            <>
              <h3>Settings (key=value per line; secrets are encrypted at rest, keep *** to preserve)</h3>
              <textarea rows={5} style={{ width: '100%', fontFamily: 'monospace' }}
                placeholder={RAW_SETTINGS_PLACEHOLDER}
                value={form.settingsText}
                onChange={(e) => setForm({ ...form, settingsText: e.target.value })} />
            </>
          )}

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
                    settingsFields: { ...(c.settings ?? {}) },
                  })} style={{ marginRight: 6 }}>
                    Edit
                  </button>
                  <button className="danger" disabled={busy === c.id} onClick={() => remove(c.id, c.name)}>
                    Delete
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
