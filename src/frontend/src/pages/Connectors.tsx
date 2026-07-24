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
  maxRetries: number;
  rateLimitPerMinute: number;
  defaultSyncMode: string;
  settingsText: string; // key=value per line — fallback editor for types without a field layout below
  settingsFields: Record<string, string>; // structured editor for CONNECTOR_FIELDS types
}

const emptyConnector: ConnectorForm = {
  name: '', type: 'ActiveDirectory', enabled: true, cronSchedule: '0 */4 * * *',
  priority: 100, maxRetries: 3, rateLimitPerMinute: 300, defaultSyncMode: 'Incremental',
  settingsText: '', settingsFields: {},
};

const SCHEDULE_PRESETS = [
  { label: 'Every 15 minutes', value: '*/15 * * * *' },
  { label: 'Every 30 minutes', value: '*/30 * * * *' },
  { label: 'Every hour', value: '0 * * * *' },
  { label: 'Every 4 hours', value: '0 */4 * * *' },
  { label: 'Every 6 hours', value: '0 */6 * * *' },
  { label: 'Daily at 02:00', value: '0 2 * * *' },
  { label: 'Manual only', value: '' },
  { label: 'Custom cron', value: '__custom__' },
];

function schedulePresetValue(cron: string) {
  return SCHEDULE_PRESETS.some((p) => p.value === cron && p.value !== '__custom__') ? cron : '__custom__';
}

function describeSchedule(cron: string) {
  if (!cron.trim()) return 'Runs only when you click Sync now.';
  const preset = SCHEDULE_PRESETS.find((p) => p.value === cron);
  return preset && preset.value !== '__custom__'
    ? `Scheduled sync: ${preset.label.toLowerCase()}.`
    : 'Custom cron expression. Use five-field cron syntax: minute hour day month weekday.';
}

function scheduleLabel(cron: string) {
  if (!cron.trim()) return 'Manual only';
  const preset = SCHEDULE_PRESETS.find((p) => p.value === cron && p.value !== '__custom__');
  return preset?.label ?? 'Custom schedule';
}

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
  /// Validation schema (mirrored by ConnectorSettingsValidator on the backend).
  required?: boolean;
  kind?: 'url' | 'host' | 'port' | 'number' | 'guid';
}

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HOST_RE = /^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$|^(\d{1,3}\.){3}\d{1,3}$/;

/// Field-level validation matching the backend schema; returns an error message or null.
function validateSetting(field: SettingField, raw: string): string | null {
  const value = (raw ?? '').trim();
  if (!value) return field.required ? 'This field is required.' : null;
  if (value === '***') return null; // masked stored secret
  switch (field.kind) {
    case 'url':
      try {
        const u = new URL(value);
        if (u.protocol !== 'http:' && u.protocol !== 'https:') return 'Must be an http(s) URL.';
      } catch { return 'Must be an absolute http(s) URL, e.g. https://host.example.com.'; }
      return null;
    case 'host':
      if (value.includes('://')) return 'Enter a hostname or IP without a scheme, e.g. dc01.corp.local.';
      return HOST_RE.test(value) ? null : 'Not a valid hostname or IPv4 address.';
    case 'port': {
      const port = Number(value);
      return Number.isInteger(port) && port >= 1 && port <= 65535
        ? null : 'Port must be a number between 1 and 65535.';
    }
    case 'number':
      return /^\d+$/.test(value) ? null : 'Must be a non-negative number.';
    case 'guid':
      return GUID_RE.test(value) ? null : 'Must be a GUID, e.g. 00000000-0000-0000-0000-000000000000.';
    default:
      return null;
  }
}

// Shared by every Entra ID (Azure AD) app-registration connector — see AadConnectorBase.AcquireTokenAsync.
const AAD_CREDENTIAL_FIELDS: SettingField[] = [
  { key: 'tenantId', label: 'Tenant ID', required: true, kind: 'guid' },
  { key: 'clientId', label: 'Client ID', required: true, kind: 'guid' },
  { key: 'clientSecret', label: 'Client secret', required: true },
];

// Structured per-field layout for every connector type with a real implementation (see
// DependencyInjection.cs's IConnector registrations — /connectors/types only ever lists these).
// Any type without an entry here falls back to the raw key=value textarea below.
const CONNECTOR_FIELDS: Record<string, SettingField[]> = {
  CortexXdr: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://api-<tenant>.xdr.<region>.paloaltonetworks.com',
      required: true, kind: 'url' },
    { key: 'apiKeyId', label: 'API Key ID', required: true },
    { key: 'apiKey', label: 'API Key (secret)', required: true },
  ],
  ActiveDirectory: [
    { key: 'server', label: 'Domain controller (FQDN)', placeholder: 'dc01.esar.local',
      required: true, kind: 'host' },
    { key: 'baseDn', label: 'Base DN', placeholder: 'DC=esar,DC=local', required: true },
    { key: 'username', label: 'Bind username (UPN)', placeholder: 'svc_esar_ad@esar.local', required: true },
    { key: 'password', label: 'Password', required: true },
    { key: 'port', label: 'Port', placeholder: '636', kind: 'port' },
    { key: 'useSsl', label: 'Use LDAPS (required)', type: 'checkbox' },
    { key: 'authType', label: 'Auth type', placeholder: 'Basic' },
    { key: 'timeoutSeconds', label: 'Timeout (seconds)', placeholder: '30', kind: 'number' },
    { key: 'resolveDns', label: 'Resolve DNS (needs private AD DNS)', type: 'checkbox' },
    { key: 'dnsTimeoutSeconds', label: 'DNS timeout (seconds)', placeholder: '5', kind: 'number' },
    { key: 'dnsMaxConcurrency', label: 'DNS max concurrency', placeholder: '8', kind: 'number' },
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
    { key: 'baseUrl', label: 'vCenter URL', placeholder: 'https://vcenter.example.com',
      required: true, kind: 'url' },
    { key: 'username', label: 'Username', required: true },
    { key: 'password', label: 'Password', required: true },
    { key: 'allowSelfSignedCert', label: 'Accept self-signed certificate (appliance/lab)', type: 'checkbox' },
  ],
  CrowdStrike: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://api.crowdstrike.com',
      required: true, kind: 'url' },
    { key: 'clientId', label: 'Client ID', required: true },
    { key: 'clientSecret', label: 'Client secret', required: true },
  ],
  SentinelOne: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://<tenant>.sentinelone.net',
      required: true, kind: 'url' },
    { key: 'apiToken', label: 'API token', required: true },
  ],
  Tenable: [
    { key: 'accessKey', label: 'Access key', required: true },
    { key: 'secretKey', label: 'Secret key', required: true },
  ],
  Qualys: [
    { key: 'baseUrl', label: 'Base URL', placeholder: 'https://gateway.qg1.apps.qualys.com',
      required: true, kind: 'url' },
    { key: 'username', label: 'Username', required: true },
    { key: 'password', label: 'Password', required: true },
  ],
  ServiceNowCmdb: [
    { key: 'instanceUrl', label: 'Instance URL', placeholder: 'https://<instance>.service-now.com',
      required: true, kind: 'url' },
    { key: 'username', label: 'Username', required: true },
    { key: 'password', label: 'Password', required: true },
    { key: 'table', label: 'CMDB table (optional)', placeholder: 'cmdb_ci_computer' },
  ],
  GenericRest: [
    { key: 'url', label: 'URL', required: true, kind: 'url' },
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
  VmwareVCenter: 'Read-only vCenter account is enough — this only lists VMs, it never changes anything. ' +
    'It collects guest hostname/OS, IP + MAC per NIC, CPU/memory and VMware Tools state (guest IP/MAC ' +
    'need VMware Tools running). Tick "Accept self-signed certificate" for appliances that use one.',
  CrowdStrike: 'API client needs the Hosts: Read scope. Base URL depends on your CrowdStrike cloud region.',
  SentinelOne: 'API token is generated per-user in Settings → Users, or as a dedicated service user.',
  Tenable: 'Access key and secret key come from Tenable.io → Settings → My Account → API Keys.',
  Qualys: 'Use a Qualys user with the Reader role and API access enabled.',
  ServiceNowCmdb: 'Account needs read access to the CMDB table. Table defaults to cmdb_ci_computer if left blank.',
  GenericRest: 'For any REST API returning a JSON array of assets. Field names map JSON properties in each ' +
    'item to ESAR asset fields — leave optional ones blank if the source does not provide them.',
};

function SettingFieldInput({ field, value, error, onChange }: {
  field: SettingField; value: string; error?: string | null; onChange: (value: string) => void;
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
      <label className="muted" style={{ fontSize: 12 }}>
        {field.label}{field.required && <span style={{ color: 'var(--red)' }}> *</span>}
      </label>
      <input
        type={isSecretKey(field.key) ? 'password' : 'text'}
        className={error ? 'invalid' : undefined}
        placeholder={field.placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        style={{ width: 260 }}
      />
      {error && <div className="field-error">{error}</div>}
    </div>
  );
}

/// Kebab (three-dot) menu holding every row action.
function RowMenu({ disabled, items }: {
  disabled?: boolean;
  items: { label: string; onClick: () => void; danger?: boolean; separatorBefore?: boolean }[];
}) {
  const [open, setOpen] = useState(false);
  return (
    <span className="row-menu">
      <button type="button" className="kebab" disabled={disabled} aria-label="Actions"
        onClick={() => setOpen(!open)}>⋯</button>
      {open && (
        <>
          <div className="row-menu-overlay" onClick={() => setOpen(false)} />
          <div className="menu">
            {items.map((item) => (
              <span key={item.label} style={{ display: 'contents' }}>
                {item.separatorBefore && <div className="separator" />}
                <button type="button" className={item.danger ? 'danger-item' : undefined}
                  onClick={() => { setOpen(false); item.onClick(); }}>
                  {item.label}
                </button>
              </span>
            ))}
          </div>
        </>
      )}
    </span>
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
  const [serverErrors, setServerErrors] = useState<Record<string, string>>({});
  const [testResult, setTestResult] = useState<{ healthy: boolean; message: string } | null>(null);
  const [testing, setTesting] = useState(false);

  const load = () => {
    client.get('/connectors').then((r) => setConnectors(r.data));
    client.get('/connectors/types').then((r) => setTypes(r.data));
  };
  useEffect(() => { load(); }, []);

  const setField = (key: string, value: string) => {
    if (!form) return;
    setForm({ ...form, settingsFields: { ...form.settingsFields, [key]: value } });
    // A change invalidates both the server's last verdict and any stale test result.
    setServerErrors((prev) => {
      if (!(key in prev)) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
    setTestResult(null);
  };

  // Real-time validation: recomputed on every keystroke, straight from the form state.
  const fields = form ? CONNECTOR_FIELDS[form.type] : undefined;
  const fieldErrors: Record<string, string> = {};
  if (form && fields) {
    for (const f of fields) {
      const validation = validateSetting(f, form.settingsFields[f.key] ?? '');
      if (validation) fieldErrors[f.key] = validation;
    }
  }
  const nameError = form !== null && !form.name.trim() ? 'Connector name is required.' : null;
  const cronError = form !== null && form.cronSchedule.trim() !== '' &&
    form.cronSchedule.trim().split(/\s+/).length !== 5
    ? 'Cron expression needs five fields: minute hour day month weekday.' : null;
  const formInvalid = form !== null &&
    (nameError !== null || cronError !== null || Object.keys(fieldErrors).length > 0);

  const testConnection = async () => {
    if (!form || formInvalid) return;
    setTesting(true);
    setTestResult(null);
    setError('');
    try {
      const settings = form.type in CONNECTOR_FIELDS ? form.settingsFields : parseSettings(form.settingsText);
      const { data } = await client.post('/connectors/test-connection',
        { id: form.id ?? null, type: form.type, settings });
      setTestResult(data);
    } catch (err: any) {
      const data = err.response?.data;
      if (data?.errors) setServerErrors(data.errors);
      setTestResult({ healthy: false, message: data?.error ?? 'Connection test failed' });
    } finally {
      setTesting(false);
    }
  };

  const save = async () => {
    if (!form || formInvalid) return;
    setError('');
    const settings = form.type in CONNECTOR_FIELDS ? form.settingsFields : parseSettings(form.settingsText);
    const payload = {
      name: form.name,
      type: form.type,
      enabled: form.enabled,
      cronSchedule: form.cronSchedule,
      priority: form.priority,
      maxRetries: form.maxRetries,
      rateLimitPerMinute: form.rateLimitPerMinute,
      defaultSyncMode: form.defaultSyncMode,
      settings,
    };
    try {
      if (form.id) await client.put(`/connectors/${form.id}`, payload);
      else await client.post('/connectors', payload);
      setForm(null);
      setServerErrors({});
      setTestResult(null);
      load();
    } catch (err: any) {
      const data = err.response?.data;
      if (data?.errors) setServerErrors(data.errors);
      setError(data?.error ?? 'Save failed');
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

  return (
    <div className="card">
      <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
        <h3>Connectors</h3>
        <button onClick={() => {
          setError('');
          setServerErrors({});
          setTestResult(null);
          setForm(form ? null : { ...emptyConnector });
        }}>
          {form ? 'Cancel' : 'New connector'}
        </button>
      </div>

      {form && (
        <div className="card" style={{ marginBottom: 16 }}>
          <div className="grid cols-3">
            <div className="card">
              <h3>Basic</h3>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                <div>
                  <label className="muted" style={{ fontSize: 12 }}>Connector name</label>
                  <input placeholder="Example: AD production" value={form.name}
                    className={nameError ? 'invalid' : undefined}
                    onChange={(e) => setForm({ ...form, name: e.target.value })}
                    style={{ width: '100%', marginTop: 3 }} />
                  {nameError && <div className="field-error">{nameError}</div>}
                </div>
                <div>
                  <label className="muted" style={{ fontSize: 12 }}>Connector type</label>
                  <select value={form.type} disabled={!!form.id}
                    onChange={(e) => setForm({ ...form, type: e.target.value, settingsFields: {}, settingsText: '' })}
                    style={{ width: '100%', marginTop: 3 }}>
                    {types.map((t) => <option key={t}>{t}</option>)}
                  </select>
                </div>
                <label style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <input type="checkbox" checked={form.enabled}
                    onChange={(e) => setForm({ ...form, enabled: e.target.checked })} />
                  Enabled for scheduled sync
                </label>
              </div>
            </div>

            <div className="card">
              <h3>Schedule</h3>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                <div>
                  <label className="muted" style={{ fontSize: 12 }}>How often should it run?</label>
                  <select value={schedulePresetValue(form.cronSchedule)}
                    onChange={(e) => {
                      const value = e.target.value;
                      if (value === '__custom__') setForm({ ...form, cronSchedule: form.cronSchedule || '0 */4 * * *' });
                      else setForm({ ...form, cronSchedule: value });
                    }}
                    style={{ width: '100%', marginTop: 3 }}>
                    {SCHEDULE_PRESETS.map((p) => <option key={p.label} value={p.value}>{p.label}</option>)}
                  </select>
                </div>
                {schedulePresetValue(form.cronSchedule) === '__custom__' && (
                  <div>
                    <label className="muted" style={{ fontSize: 12 }}>Cron expression</label>
                    <input placeholder="0 */4 * * *" value={form.cronSchedule}
                      className={cronError ? 'invalid' : undefined}
                      onChange={(e) => setForm({ ...form, cronSchedule: e.target.value })}
                      style={{ width: '100%', marginTop: 3 }} />
                    {cronError && <div className="field-error">{cronError}</div>}
                  </div>
                )}
                <p className="muted" style={{ fontSize: 12, lineHeight: 1.4, margin: 0 }}>
                  {describeSchedule(form.cronSchedule)}
                </p>
              </div>
            </div>

            <div className="card">
              <h3>Execution</h3>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                <div>
                  <label className="muted" style={{ fontSize: 12 }}>Default sync mode</label>
                  <select value={form.defaultSyncMode}
                    onChange={(e) => setForm({ ...form, defaultSyncMode: e.target.value })}
                    style={{ width: '100%', marginTop: 3 }}>
                    <option value="Incremental">Incremental - only recent changes</option>
                    <option value="Full">Full - rescan everything</option>
                  </select>
                </div>
                <div>
                  <label className="muted" style={{ fontSize: 12 }}>Source priority</label>
                  <input type="number" value={form.priority}
                    onChange={(e) => setForm({ ...form, priority: Number(e.target.value) })}
                    style={{ width: '100%', marginTop: 3 }} />
                  <p className="muted" style={{ fontSize: 11, marginTop: 4 }}>
                    Lower number wins when sources disagree. Typical: Azure 10, AD 60, scanners 100+.
                  </p>
                </div>
                <div>
                  <label className="muted" style={{ fontSize: 12 }}>Retry attempts</label>
                  <input type="number" min={0} value={form.maxRetries}
                    onChange={(e) => setForm({ ...form, maxRetries: Number(e.target.value) })}
                    style={{ width: '100%', marginTop: 3 }} />
                  <p className="muted" style={{ fontSize: 11, marginTop: 4 }}>
                    How many times ESAR retries temporary connector/API failures.
                  </p>
                </div>
                <div>
                  <label className="muted" style={{ fontSize: 12 }}>Rate limit per minute</label>
                  <input type="number" value={form.rateLimitPerMinute}
                    onChange={(e) => setForm({ ...form, rateLimitPerMinute: Number(e.target.value) })}
                    style={{ width: '100%', marginTop: 3 }} />
                  <p className="muted" style={{ fontSize: 11, marginTop: 4 }}>
                    Maximum requests per minute sent to the source API.
                  </p>
                </div>
              </div>
            </div>
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
                    error={fieldErrors[f.key] ?? serverErrors[f.key]}
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
          <div style={{ marginTop: 8, display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <button onClick={save} disabled={formInvalid}
              title={formInvalid ? 'Fix the highlighted fields first' : undefined}>
              Save connector
            </button>
            <button className="secondary" onClick={testConnection} disabled={formInvalid || testing}>
              {testing ? 'Testing…' : 'Test connection'}
            </button>
            {testResult && (
              <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
                <Badge value={testResult.healthy ? 'Succeeded' : 'Failed'} />
                <span className="muted" style={{ fontSize: 12, maxWidth: 420 }}>{testResult.message}</span>
              </span>
            )}
            {formInvalid && (
              <span className="muted" style={{ fontSize: 12 }}>
                Fix the highlighted fields to enable saving.
              </span>
            )}
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
                <td>
                  <div>{scheduleLabel(c.cronSchedule ?? '')}</div>
                  {c.cronSchedule && <div className="muted" style={{ fontSize: 11 }}>{c.cronSchedule}</div>}
                </td>
                <td>
                  {c.isHealthy ? <Badge value="Compliant" /> : <Badge value="Failed" />}
                  {!c.isHealthy && <div className="muted" style={{ fontSize: 11 }}>{c.lastHealthMessage}</div>}
                </td>
                <td className="muted">{formatDate(c.lastRunAt)}</td>
                <td><Badge value={c.lastRunStatus} /></td>
                <td style={{ textAlign: 'right' }}>
                  {busy === c.id
                    ? <span className="muted">Working…</span>
                    : <RowMenu items={[
                        { label: 'Sync now', onClick: () => run(c.id) },
                        { label: 'Health check', onClick: () => healthCheck(c.id) },
                        { label: expanded === c.id ? 'Hide jobs' : 'View jobs', onClick: () => toggleJobs(c.id) },
                        {
                          label: 'Edit',
                          onClick: () => {
                            setError('');
                            setServerErrors({});
                            setTestResult(null);
                            setForm({
                              id: c.id, name: c.name, type: c.type, enabled: c.enabled,
                              cronSchedule: c.cronSchedule, priority: c.priority,
                              maxRetries: c.maxRetries ?? 3, rateLimitPerMinute: c.rateLimitPerMinute,
                              defaultSyncMode: c.defaultSyncMode,
                              settingsText: Object.entries(c.settings ?? {})
                                .map(([k, v]) => `${k}=${v}`).join('\n'),
                              settingsFields: { ...(c.settings ?? {}) },
                            });
                          },
                        },
                        { label: 'Delete', onClick: () => remove(c.id, c.name), danger: true, separatorBefore: true },
                      ]} />}
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
