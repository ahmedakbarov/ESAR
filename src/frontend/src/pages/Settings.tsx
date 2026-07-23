import { ReactNode, useEffect, useMemo, useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

interface SettingDto {
  id: string;
  key: string;
  value: string;
  description?: string;
  isEncrypted?: boolean;
  updatedBy?: string;
  updatedAt?: string;
}

interface PriorityDto {
  id: string;
  connector: string;
  attribute?: string | null;
  priority: number;
}

type RowStatus = { kind: 'saving' | 'saved' | 'error'; message?: string };
type ValueKind = 'secret' | 'boolean' | 'number' | 'text';
type SettingsBucket = 'system' | 'security' | 'authentication';

const CATEGORY_LABELS: Record<string, string> = {
  matching: 'Matching',
  lifecycle: 'Asset Lifecycle',
  compliance: 'Compliance',
  approval: 'Approvals',
  auth: 'Authentication',
  notification: 'Notifications',
  reports: 'Reports',
  dataquality: 'Data Quality',
  security: 'Security',
  session: 'Sessions',
  password: 'Password Policy',
  audit: 'Audit',
};

const CATEGORY_ORDER = [
  'security',
  'password',
  'session',
  'auth',
  'matching',
  'lifecycle',
  'compliance',
  'approval',
  'notification',
  'reports',
  'dataquality',
  'audit',
];

const SETTINGS_NAV = [
  { to: '/settings/security', label: 'Security', hint: 'Password, sessions, audit guardrails' },
  { to: '/settings/system', label: 'System Settings', hint: 'Operational defaults and retention' },
  { to: '/settings/users-roles', label: 'Users & Roles', hint: 'Accounts, roles, permissions' },
  { to: '/settings/authentication', label: 'Authentication', hint: 'Local, Entra ID, AD login' },
  { to: '/settings/integrations', label: 'Integrations', hint: 'Azure, AD, security tools' },
  { to: '/settings/sources', label: 'Sources', hint: 'Source trust and field ownership' },
  { to: '/settings/priorities', label: 'Priorities', hint: 'Authoritative source order' },
  { to: '/settings/audit', label: 'Audit', hint: 'Admin and security events' },
];

function humanize(text: string): string {
  return text
    .replace(/[._-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim()
    .replace(/^./, (c) => c.toUpperCase());
}

function categoryOf(key: string): string {
  return key.split('.')[0] || 'other';
}

function labelOf(key: string): string {
  const dot = key.indexOf('.');
  return humanize(dot === -1 ? key : key.slice(dot + 1));
}

function bucketOf(s: SettingDto): SettingsBucket {
  const key = s.key.toLowerCase();
  const category = categoryOf(key);
  if (category === 'auth') return 'authentication';
  if (
    ['security', 'password', 'session', 'audit'].includes(category) ||
    key.includes('lockout') ||
    key.includes('token') ||
    key.includes('jwt') ||
    key.includes('secret') ||
    key.includes('https')
  ) return 'security';
  return 'system';
}

function kindOf(s: SettingDto): ValueKind {
  if (s.isEncrypted || s.value === '***') return 'secret';
  const v = s.value.trim().toLowerCase();
  if (v === 'true' || v === 'false') return 'boolean';
  if (v !== '' && /^-?\d+(\.\d+)?$/.test(v)) return 'number';
  return 'text';
}

function useSettingsData() {
  const [settings, setSettings] = useState<SettingDto[]>([]);
  const [priorities, setPriorities] = useState<PriorityDto[]>([]);
  const [loading, setLoading] = useState(true);

  const load = () => {
    setLoading(true);
    Promise.all([
      client.get('/settings').then((r) => setSettings(r.data)),
      client.get('/settings/source-priorities').then((r) => setPriorities(r.data)),
    ]).finally(() => setLoading(false));
  };

  useEffect(load, []);

  return { settings, priorities, loading, load };
}

function SettingsEditor({ bucket, title, description, empty }: {
  bucket: SettingsBucket;
  title: string;
  description: string;
  empty: ReactNode;
}) {
  const { settings, loading, load } = useSettingsData();
  const [query, setQuery] = useState('');
  const [edited, setEdited] = useState<Record<string, string>>({});
  const [status, setStatus] = useState<Record<string, RowStatus>>({});

  const setRow = (key: string, s: RowStatus | null) =>
    setStatus((prev) => {
      const next = { ...prev };
      if (s) next[key] = s; else delete next[key];
      return next;
    });

  const edit = (key: string, value: string) => setEdited((prev) => ({ ...prev, [key]: value }));
  const reset = (key: string) => {
    setEdited((prev) => { const next = { ...prev }; delete next[key]; return next; });
    setRow(key, null);
  };

  const save = async (s: SettingDto) => {
    const value = edited[s.key];
    if (value === undefined) return;
    setRow(s.key, { kind: 'saving' });
    try {
      await client.put(`/settings/${encodeURIComponent(s.key)}`, { value });
      setEdited((prev) => { const next = { ...prev }; delete next[s.key]; return next; });
      setRow(s.key, { kind: 'saved' });
      setTimeout(() => setRow(s.key, null), 2500);
      load();
    } catch (err: any) {
      setRow(s.key, { kind: 'error', message: err.response?.data?.error ?? 'Save failed' });
    }
  };

  const groups = useMemo(() => {
    const q = query.trim().toLowerCase();
    const matches = settings
      .filter((s) => bucketOf(s) === bucket)
      .filter((s) =>
        !q || s.key.toLowerCase().includes(q) ||
        (s.description ?? '').toLowerCase().includes(q) ||
        labelOf(s.key).toLowerCase().includes(q));

    const byCat = new Map<string, SettingDto[]>();
    for (const s of matches) {
      const cat = categoryOf(s.key);
      if (!byCat.has(cat)) byCat.set(cat, []);
      byCat.get(cat)!.push(s);
    }
    const cats = [...byCat.keys()].sort((a, b) => {
      const ia = CATEGORY_ORDER.indexOf(a), ib = CATEGORY_ORDER.indexOf(b);
      if (ia !== -1 || ib !== -1) return (ia === -1 ? 99 : ia) - (ib === -1 ? 99 : ib);
      return a.localeCompare(b);
    });
    return cats.map((cat) => ({
      cat,
      title: CATEGORY_LABELS[cat] ?? humanize(cat),
      rows: byCat.get(cat)!.sort((a, b) => a.key.localeCompare(b.key)),
    }));
  }, [settings, query, bucket]);

  const renderControl = (s: SettingDto) => {
    const kind = kindOf(s);
    const id = `set-${s.key}`;
    const base = kind === 'secret' ? '' : s.value;
    const draft = edited[s.key];
    const current = draft ?? base;

    if (kind === 'boolean') {
      return (
        <select id={id} value={current.toLowerCase()} onChange={(e) => edit(s.key, e.target.value)}>
          <option value="true">Enabled (true)</option>
          <option value="false">Disabled (false)</option>
        </select>
      );
    }
    if (kind === 'number') {
      const step = (draft ?? s.value).includes('.') ? '0.01' : '1';
      return (
        <input id={id} type="number" step={step} value={current} style={{ width: 130 }}
          onChange={(e) => edit(s.key, e.target.value)} />
      );
    }
    if (kind === 'secret') {
      return (
        <input id={id} type="password" autoComplete="new-password" value={current} style={{ width: 240 }}
          placeholder="stored secret - type to replace"
          onChange={(e) => edit(s.key, e.target.value)} />
      );
    }
    return (
      <input id={id} type="text" value={current} style={{ width: 260 }}
        onChange={(e) => edit(s.key, e.target.value)} />
    );
  };

  const renderStatus = (st?: RowStatus) => {
    if (!st) return <span className="status" />;
    if (st.kind === 'saving') return <span className="status muted">Saving...</span>;
    if (st.kind === 'saved') return <span className="status ok">Saved</span>;
    return <span className="status err" title={st.message}>Failed</span>;
  };

  return (
    <>
      <div className="settings-toolbar">
        <div>
          <h2>{title}</h2>
          <p className="muted">{description}</p>
        </div>
        <input type="search" placeholder="Search settings..." value={query}
          onChange={(e) => setQuery(e.target.value)} aria-label={`Search ${title}`} />
      </div>

      {loading && <div className="card muted">Loading settings...</div>}

      {!loading && groups.length === 0 && (
        <div className="settings-empty">
          {query ? `No settings match "${query}".` : empty}
        </div>
      )}

      {!loading && groups.map((g) => (
        <div key={g.cat} className="card settings-group">
          <h3>{g.title}</h3>
          {g.rows.map((s) => {
            const kind = kindOf(s);
            const base = kind === 'secret' ? '' : s.value;
            const draft = edited[s.key];
            const dirty = draft !== undefined && draft !== base;
            const st = status[s.key];
            return (
              <div key={s.key} className="setting-row">
                <div className="meta">
                  <label htmlFor={`set-${s.key}`}>{labelOf(s.key)}</label>
                  <div className="key muted">{s.key}</div>
                  {s.description && <div className="desc muted">{s.description}</div>}
                  {s.updatedBy && (
                    <div className="stamp muted">Updated by {s.updatedBy} · {formatDate(s.updatedAt)}</div>
                  )}
                </div>
                <div className="control">
                  {renderControl(s)}
                  {dirty && (
                    <>
                      <button onClick={() => save(s)} disabled={st?.kind === 'saving'}>Save</button>
                      <button className="secondary" onClick={() => reset(s.key)}>Reset</button>
                    </>
                  )}
                  {renderStatus(st)}
                </div>
              </div>
            );
          })}
        </div>
      ))}
    </>
  );
}

function SourceOverview() {
  const { priorities, loading } = useSettingsData();
  const connectors = [...new Set(priorities.map((p) => p.connector))].sort();

  return (
    <>
      <div className="settings-toolbar">
        <div>
          <h2>Sources</h2>
          <p className="muted">
            Define which data sources are trusted for asset identity, ownership, and network attributes.
          </p>
        </div>
      </div>
      <div className="settings-cards">
        <div className="card">
          <h3>Identity sources</h3>
          <p className="muted">
            Azure, Active Directory, manual entries, imports, and scanners can all describe the same endpoint.
            Source priority decides which value wins when fields conflict.
          </p>
        </div>
        <div className="card">
          <h3>Field ownership</h3>
          <p className="muted">
            Use attribute-level priorities for sensitive fields like IP address, MAC address, owner,
            environment, and criticality.
          </p>
        </div>
        <div className="card">
          <h3>Configured sources</h3>
          {loading ? (
            <p className="muted">Loading sources...</p>
          ) : connectors.length > 0 ? (
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              {connectors.map((c) => <Badge key={c} value={c} />)}
            </div>
          ) : (
            <p className="muted">No source priorities configured yet.</p>
          )}
        </div>
      </div>
    </>
  );
}

function Priorities() {
  const { priorities, loading, load } = useSettingsData();
  const [prioDraft, setPrioDraft] = useState<Record<string, string>>({});
  const [prioStatus, setPrioStatus] = useState<Record<string, RowStatus>>({});

  const savePriority = async (p: PriorityDto) => {
    const raw = prioDraft[p.id];
    const value = Number(raw);
    if (raw === undefined || Number.isNaN(value)) return;
    setPrioStatus((prev) => ({ ...prev, [p.id]: { kind: 'saving' } }));
    try {
      await client.put(`/settings/source-priorities/${p.id}`, { priority: value });
      setPrioDraft((prev) => { const next = { ...prev }; delete next[p.id]; return next; });
      setPrioStatus((prev) => ({ ...prev, [p.id]: { kind: 'saved' } }));
      setTimeout(() => setPrioStatus((prev) => { const n = { ...prev }; delete n[p.id]; return n; }), 2500);
      load();
    } catch (err: any) {
      setPrioStatus((prev) => ({
        ...prev, [p.id]: { kind: 'error', message: err.response?.data?.error ?? 'Save failed' },
      }));
    }
  };

  const dirty = (p: PriorityDto) => {
    const d = prioDraft[p.id];
    return d !== undefined && d !== '' && Number(d) !== p.priority && !Number.isNaN(Number(d));
  };

  return (
    <>
      <div className="settings-toolbar">
        <div>
          <h2>Priorities</h2>
          <p className="muted">
            Lower value means more authoritative. Attribute-level entries override the connector default.
          </p>
        </div>
      </div>
      <div className="card">
        <table className="data">
          <thead><tr><th>Connector</th><th>Attribute</th><th style={{ width: 260 }}>Priority</th></tr></thead>
          <tbody>
            {priorities.map((p) => {
              const st = prioStatus[p.id];
              const value = prioDraft[p.id] ?? String(p.priority);
              return (
                <tr key={p.id}>
                  <td><Badge value={p.connector} /></td>
                  <td className="muted">{p.attribute ?? '(all attributes)'}</td>
                  <td>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                      <input type="number" value={value} style={{ width: 90 }}
                        aria-label={`Priority for ${p.connector} ${p.attribute ?? 'all attributes'}`}
                        onChange={(e) => setPrioDraft((prev) => ({ ...prev, [p.id]: e.target.value }))} />
                      {dirty(p) && (
                        <button onClick={() => savePriority(p)} disabled={st?.kind === 'saving'}>Save</button>
                      )}
                      {st?.kind === 'saving' && <span className="muted">Saving...</span>}
                      {st?.kind === 'saved' && <span className="muted">Saved</span>}
                      {st?.kind === 'error' && <span className="error" title={st.message}>Failed</span>}
                    </div>
                  </td>
                </tr>
              );
            })}
            {!loading && priorities.length === 0 && (
              <tr><td colSpan={3} className="muted">No source priorities configured.</td></tr>
            )}
            {loading && <tr><td colSpan={3} className="muted">Loading priorities...</td></tr>}
          </tbody>
        </table>
      </div>
    </>
  );
}

export function SettingsSecurity() {
  return (
    <SettingsEditor
      bucket="security"
      title="Security"
      description="Control password, session, audit, and transport-security guardrails."
      empty={
        <>
          No dedicated security settings are configured yet. Password change, admin reset, protected
          bootstrap admin, audit logging, and HTTPS deployment are active in the current build.
        </>
      }
    />
  );
}

export function SettingsSystem() {
  return (
    <SettingsEditor
      bucket="system"
      title="System Settings"
      description="Manage operational defaults used by matching, lifecycle, compliance, approvals, and jobs."
      empty="No system settings are configured yet."
    />
  );
}

export function SettingsAuthentication() {
  return (
    <SettingsEditor
      bucket="authentication"
      title="Authentication"
      description="Configure local sign-in, Entra ID SSO, and Active Directory login options."
      empty={
        <>
          No authentication settings are stored yet. Entra ID and AD login settings can be added through the
          backend settings API and will appear here automatically.
        </>
      }
    />
  );
}

export function SettingsSources() {
  return <SourceOverview />;
}

export function SettingsPriorities() {
  return <Priorities />;
}

export default function Settings() {
  return (
    <div className="settings-shell">
      <aside className="settings-nav" aria-label="Settings sections">
        <div>
          <h2>Settings</h2>
          <p className="muted">Administration center</p>
        </div>
        {SETTINGS_NAV.map((item) => (
          <NavLink key={item.to} to={item.to}>
            <span>{item.label}</span>
            <small>{item.hint}</small>
          </NavLink>
        ))}
      </aside>
      <section className="settings-content">
        <Outlet />
      </section>
    </div>
  );
}
