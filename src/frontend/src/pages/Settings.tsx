import { useEffect, useMemo, useState } from 'react';
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

// Settings keys follow a `category.subKey` convention (matching.autoMergeThreshold, auth.entra.tenantId, …).
// The first segment groups the row; the rest becomes its human label.
const CATEGORY_LABELS: Record<string, string> = {
  matching: 'Matching',
  lifecycle: 'Asset Lifecycle',
  compliance: 'Compliance',
  approval: 'Approvals',
  auth: 'Authentication',
  notification: 'Notifications',
};
const CATEGORY_ORDER = ['matching', 'lifecycle', 'compliance', 'approval', 'auth', 'notification'];

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

// Label from everything after the category prefix, so `auth.entra.tenantId` reads "Entra tenant id".
function labelOf(key: string): string {
  const dot = key.indexOf('.');
  return humanize(dot === -1 ? key : key.slice(dot + 1));
}

// The control type is inferred from the value: encrypted secrets get a masked field, true/false a
// toggle, plain numbers a numeric field, everything else free text.
function kindOf(s: SettingDto): ValueKind {
  if (s.isEncrypted || s.value === '***') return 'secret';
  const v = s.value.trim().toLowerCase();
  if (v === 'true' || v === 'false') return 'boolean';
  if (v !== '' && /^-?\d+(\.\d+)?$/.test(v)) return 'number';
  return 'text';
}

export default function Settings() {
  const [settings, setSettings] = useState<SettingDto[]>([]);
  const [priorities, setPriorities] = useState<PriorityDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState('');

  const [edited, setEdited] = useState<Record<string, string>>({});
  const [status, setStatus] = useState<Record<string, RowStatus>>({});
  const [prioDraft, setPrioDraft] = useState<Record<string, string>>({});
  const [prioStatus, setPrioStatus] = useState<Record<string, RowStatus>>({});

  const load = () => {
    setLoading(true);
    Promise.all([
      client.get('/settings').then((r) => setSettings(r.data)),
      client.get('/settings/source-priorities').then((r) => setPriorities(r.data)),
    ]).finally(() => setLoading(false));
  };
  useEffect(load, []);

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

  // Filter, then bucket by category in a stable known-first order.
  const groups = useMemo(() => {
    const q = query.trim().toLowerCase();
    const matches = settings.filter((s) =>
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
  }, [settings, query]);

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
        <input id={id} type="password" autoComplete="new-password" value={current} style={{ width: 220 }}
          placeholder="•••••• stored — type to replace"
          onChange={(e) => edit(s.key, e.target.value)} />
      );
    }
    return (
      <input id={id} type="text" value={current} style={{ width: 240 }}
        onChange={(e) => edit(s.key, e.target.value)} />
    );
  };

  const renderStatus = (st?: RowStatus) => {
    if (!st) return <span className="status" />;
    if (st.kind === 'saving') return <span className="status muted">Saving…</span>;
    if (st.kind === 'saved') return <span className="status ok">Saved ✓</span>;
    return <span className="status err" title={st.message}>Failed</span>;
  };

  const prioDirty = (p: PriorityDto) => {
    const d = prioDraft[p.id];
    return d !== undefined && d !== '' && Number(d) !== p.priority && !Number.isNaN(Number(d));
  };

  return (
    <>
      <div className="settings-toolbar">
        <p className="muted" style={{ margin: 0, maxWidth: 560 }}>
          System-wide configuration. Changes take effect on the next run of the affected job and are
          recorded in the audit log.
        </p>
        <input type="search" placeholder="Search settings…" value={query}
          onChange={(e) => setQuery(e.target.value)} aria-label="Search settings" />
      </div>

      {loading && <div className="card muted">Loading settings…</div>}

      {!loading && groups.length === 0 && (
        <div className="card muted">
          {query ? `No settings match "${query}".` : 'No settings configured.'}
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

      <h2 className="section-title">Source Priorities</h2>
      <p className="muted" style={{ marginBottom: 10 }}>
        Lower value = more authoritative. Attribute-level entries override the connector's global priority.
      </p>
      <div className="card">
        <table className="data">
          <thead><tr><th>Connector</th><th>Attribute</th><th style={{ width: 220 }}>Priority</th></tr></thead>
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
                      {prioDirty(p) && (
                        <button onClick={() => savePriority(p)} disabled={st?.kind === 'saving'}>Save</button>
                      )}
                      {renderStatus(st)}
                    </div>
                  </td>
                </tr>
              );
            })}
            {priorities.length === 0 && (
              <tr><td colSpan={3} className="muted">No source priorities configured.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </>
  );
}
