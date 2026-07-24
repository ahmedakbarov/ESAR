import { Fragment, useEffect, useState } from 'react';
import client from '../api/client';
import { formatDate } from '../components/Ui';

// Runtime/system logs (Serilog) live in Seq; user-action audit lives here. Override the Seq
// location at build time with VITE_SEQ_URL when it is not on localhost.
const SEQ_URL = (import.meta as any).env?.VITE_SEQ_URL ?? 'http://localhost:5341';

type Tone = 'green' | 'red' | 'amber' | 'blue' | 'muted';

// Friendly label + severity tone for every AuditAction the API emits.
const ACTION_META: Record<string, { label: string; tone: Tone }> = {
  Login: { label: 'Sign in', tone: 'blue' },
  Logout: { label: 'Sign out', tone: 'muted' },
  ApiCall: { label: 'API call', tone: 'muted' },
  AssetCreated: { label: 'Asset created', tone: 'green' },
  AssetUpdated: { label: 'Asset updated', tone: 'blue' },
  AssetDeleted: { label: 'Asset deleted', tone: 'red' },
  AssetReactivated: { label: 'Asset reactivated', tone: 'green' },
  AssetMerged: { label: 'Assets merged', tone: 'amber' },
  ConfigurationChanged: { label: 'Configuration changed', tone: 'amber' },
  MatchingDecision: { label: 'Matching decision', tone: 'blue' },
  ComplianceDecision: { label: 'Compliance decision', tone: 'blue' },
  ConnectorExecuted: { label: 'Connector run', tone: 'muted' },
  UserCreated: { label: 'User created', tone: 'green' },
  UserUpdated: { label: 'User updated', tone: 'blue' },
  UserDeleted: { label: 'User deleted', tone: 'red' },
  RoleChanged: { label: 'Role changed', tone: 'amber' },
  ReportGenerated: { label: 'Report generated', tone: 'muted' },
};

const ACTIONS = Object.keys(ACTION_META).filter((a) => a !== 'ApiCall');

interface AuditItem {
  id: string;
  userName: string;
  action: string;
  entityType?: string;
  entityId?: string;
  details?: string;
  ipAddress?: string;
  timestamp: string;
}

function ActionBadge({ action }: { action: string }) {
  const meta = ACTION_META[action] ?? { label: action, tone: 'muted' as Tone };
  return <span className={`badge ${meta.tone}`}>{meta.label}</span>;
}

// Audit "details" is a JSON blob or plain string. Render objects as a readable key/value grid.
function DetailsView({ details }: { details?: string }) {
  if (!details) return <span className="muted">No additional details.</span>;
  let parsed: unknown = null;
  try { parsed = JSON.parse(details); } catch { /* plain text */ }
  if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
    const entries = Object.entries(parsed as Record<string, unknown>);
    return (
      <div style={{ display: 'grid', gridTemplateColumns: '200px 1fr', gap: '4px 14px' }}>
        {entries.map(([k, v]) => (
          <div key={k} style={{ display: 'contents' }}>
            <span className="muted" style={{ fontSize: 12 }}>{k}</span>
            <span style={{ wordBreak: 'break-word' }}>
              {v === null || v === undefined ? '—' : typeof v === 'object' ? JSON.stringify(v) : String(v)}
            </span>
          </div>
        ))}
      </div>
    );
  }
  return <pre className="json">{details}</pre>;
}

export default function Audit() {
  const [data, setData] = useState<{ items: AuditItem[]; totalCount: number; pageSize: number } | null>(null);
  const [loading, setLoading] = useState(true);
  const [user, setUser] = useState('');
  const [action, setAction] = useState('');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [hideApiCalls, setHideApiCalls] = useState(true);
  const [page, setPage] = useState(1);
  const [reload, setReload] = useState(0);
  const [expanded, setExpanded] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    const params = new URLSearchParams({ page: String(page), pageSize: '50' });
    if (user.trim()) params.set('user', user.trim());
    if (action) params.set('action', action);
    if (from) params.set('from', from);
    if (to) params.set('to', to);
    if (!action && hideApiCalls) params.set('excludeApiCalls', 'true');
    client.get(`/audit?${params}`).then((r) => setData(r.data)).finally(() => setLoading(false));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page, reload]);

  const apply = () => { setExpanded(null); if (page !== 1) setPage(1); else setReload((r) => r + 1); };

  return (
    <>
      <div className="settings-toolbar">
        <div>
          <h3 style={{ marginBottom: 2 }}>Activity audit</h3>
          <p className="muted" style={{ margin: 0, fontSize: 13 }}>
            Who did what in the portal. For runtime/system logs (services, jobs, errors) use Seq.
          </p>
        </div>
        <a className="btn" href={SEQ_URL} target="_blank" rel="noopener noreferrer"
          style={{ whiteSpace: 'nowrap' }}>
          Open system logs (Seq) ↗
        </a>
      </div>

      <div className="card">
        <div className="filters">
          <select value={action} onChange={(e) => setAction(e.target.value)} aria-label="Action">
            <option value="">All actions</option>
            {ACTIONS.map((a) => <option key={a} value={a}>{ACTION_META[a].label}</option>)}
          </select>
          <input placeholder="User…" value={user} onChange={(e) => setUser(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && apply()} />
          <label className="muted" style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 12 }}>
            From <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
          </label>
          <label className="muted" style={{ display: 'flex', alignItems: 'center', gap: 4, fontSize: 12 }}>
            To <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
          </label>
          <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13,
            opacity: action ? 0.5 : 1 }} title="API-call rows mirror the underlying action">
            <input type="checkbox" checked={hideApiCalls} disabled={!!action}
              onChange={(e) => setHideApiCalls(e.target.checked)} /> Hide API calls
          </label>
          <button onClick={apply}>Apply</button>
        </div>

        <table className="data">
          <thead>
            <tr><th style={{ width: 20 }}></th><th>Time</th><th>User</th><th>Action</th>
              <th>Entity</th><th>IP</th></tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={6} className="muted">Loading…</td></tr>}
            {!loading && data?.items.length === 0 && (
              <tr><td colSpan={6} className="muted">No audit entries match these filters.</td></tr>
            )}
            {!loading && data?.items.map((l) => {
              const open = expanded === l.id;
              return (
                <Fragment key={l.id}>
                  <tr onClick={() => setExpanded(open ? null : l.id)}
                    style={{ cursor: 'pointer' }}>
                    <td className="muted" style={{ textAlign: 'center' }}>{open ? '▾' : '▸'}</td>
                    <td className="muted" style={{ whiteSpace: 'nowrap' }}>{formatDate(l.timestamp)}</td>
                    <td>{l.userName}</td>
                    <td><ActionBadge action={l.action} /></td>
                    <td className="muted">
                      {l.entityType}{l.entityId ? ` · ${l.entityId.substring(0, 12)}` : ''}
                    </td>
                    <td className="muted">{l.ipAddress ?? '—'}</td>
                  </tr>
                  {open && (
                    <tr>
                      <td></td>
                      <td colSpan={5} style={{ background: 'var(--panel-2)' }}>
                        <DetailsView details={l.details} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>

        {data && (
          <div className="pagination">
            <button className="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Prev</button>
            <span>{data.totalCount} entries · page {page}</span>
            <button className="secondary" disabled={page * data.pageSize >= data.totalCount}
              onClick={() => setPage(page + 1)}>Next</button>
          </div>
        )}
      </div>
    </>
  );
}
