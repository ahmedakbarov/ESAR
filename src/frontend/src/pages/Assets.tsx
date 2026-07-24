import { useEffect, useRef, useState, type MouseEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import client, { AssetDto, PagedResult } from '../api/client';
import { Badge, formatDate } from '../components/Ui';

interface FilterValue { value: string; count: number; }

// Column filter definitions: URL key ↔ facet field ↔ API multi-value query key.
const FILTERS: Record<string, { facet: string; apiKey: string }> = {
  assetType: { facet: 'assetType', apiKey: 'assetTypes' },
  os: { facet: 'os', apiKey: 'osNames' },
  environment: { facet: 'environment', apiKey: 'environments' },
  criticality: { facet: 'criticality', apiKey: 'criticalities' },
  complianceStatus: { facet: 'complianceStatus', apiKey: 'complianceStatuses' },
  source: { facet: 'source', apiKey: 'sources' },
};

function dqTone(score: number): string {
  if (score >= 80) return 'var(--green)';
  if (score >= 50) return 'var(--amber)';
  return 'var(--red)';
}

/// Excel-style dropdown: distinct values (with counts) as checkboxes plus a free search box.
function ColumnFilter({ filterKey, selected, onChange }: {
  filterKey: string; selected: string[]; onChange: (values: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  const [values, setValues] = useState<FilterValue[] | null>(null);
  const [query, setQuery] = useState('');
  const loadedFor = useRef<string | null>(null);

  const toggleOpen = (e: MouseEvent) => {
    e.stopPropagation(); // header click also sorts — keep the two gestures separate
    if (!open && loadedFor.current !== filterKey) {
      client.get(`/assets/filter-values?field=${FILTERS[filterKey].facet}`)
        .then((r) => { setValues(r.data); loadedFor.current = filterKey; })
        .catch(() => setValues([]));
    }
    setOpen(!open);
  };

  const toggleValue = (value: string) =>
    onChange(selected.includes(value) ? selected.filter((v) => v !== value) : [...selected, value]);

  const shown = (values ?? []).filter((v) => !query || v.value.toLowerCase().includes(query.toLowerCase()));

  return (
    <span className="col-filter" onClick={(e) => e.stopPropagation()}>
      <button type="button" className={`funnel ${selected.length > 0 ? 'active' : ''}`}
        onClick={toggleOpen} title={selected.length > 0 ? `Filtered: ${selected.join(', ')}` : 'Filter'}
        aria-label={`Filter ${filterKey}`}>
        ▼{selected.length > 0 && ` ${selected.length}`}
      </button>
      {open && (
        <>
          <div className="col-filter-overlay" onClick={() => setOpen(false)} />
          <div className="dropdown">
            <input type="text" placeholder="Search values…" value={query} autoFocus
              onChange={(e) => setQuery(e.target.value)} />
            <div className="options">
              {values === null && <div className="muted" style={{ fontSize: 12 }}>Loading…</div>}
              {values !== null && shown.length === 0 && (
                <div className="muted" style={{ fontSize: 12 }}>No values.</div>
              )}
              {shown.map((v) => (
                <label key={v.value} className="opt">
                  <input type="checkbox" checked={selected.includes(v.value)}
                    onChange={() => toggleValue(v.value)} />
                  {v.value}
                  <span className="count">{v.count}</span>
                </label>
              ))}
            </div>
            <div className="actions">
              <button type="button" className="secondary" onClick={() => onChange([])}>Clear</button>
              <button type="button" onClick={() => setOpen(false)}>Done</button>
            </div>
          </div>
        </>
      )}
    </span>
  );
}

export default function Assets() {
  const [searchParams, setSearchParams] = useSearchParams();
  const [result, setResult] = useState<PagedResult<AssetDto> | null>(null);
  const [searchDraft, setSearchDraft] = useState(searchParams.get('q') ?? '');
  const [showMore, setShowMore] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [reloadTick, setReloadTick] = useState(0);

  // The URL is the single source of truth for filters, sort and paging, so views
  // are shareable/bookmarkable and survive refresh.
  const page = Math.max(1, Number(searchParams.get('page') ?? '1') || 1);
  const sort = searchParams.get('sort') ?? '';
  const dir = searchParams.get('dir') === 'desc' ? 'desc' : 'asc';
  const getList = (key: string) => searchParams.get(`f_${key}`)?.split(',').filter(Boolean) ?? [];

  const patch = (mutate: (p: URLSearchParams) => void, resetPage = true) => {
    const next = new URLSearchParams(searchParams);
    mutate(next);
    if (resetPage) next.delete('page');
    setSearchParams(next, { replace: true });
  };

  const setFilter = (key: string, values: string[]) =>
    patch((p) => { values.length > 0 ? p.set(`f_${key}`, values.join(',')) : p.delete(`f_${key}`); });

  const cycleSort = (field: string) => patch((p) => {
    if (sort !== field) { p.set('sort', field); p.set('dir', 'asc'); }
    else if (dir === 'asc') p.set('dir', 'desc');
    else { p.delete('sort'); p.delete('dir'); }
  }, false);

  const sortMark = (field: string) => sort === field ? (dir === 'asc' ? ' ▲' : ' ▼') : '';

  useEffect(() => {
    const params = new URLSearchParams({ page: String(page), pageSize: '25' });
    const q = searchParams.get('q');
    if (q) params.set('search', q);
    if (sort) { params.set('sortBy', sort); params.set('sortDescending', String(dir === 'desc')); }
    for (const [key, def] of Object.entries(FILTERS))
      for (const value of getList(key)) params.append(def.apiKey, value);
    const status = searchParams.get('status');
    if (status) params.set('status', status);
    const policyExempt = searchParams.get('policyExempt');
    if (policyExempt) params.set('policyExempt', policyExempt);
    const tagKey = searchParams.get('tagKey');
    if (tagKey) {
      params.set('tagKey', tagKey);
      const tagValue = searchParams.get('tagValue');
      if (tagValue) params.set('tagValue', tagValue);
    }
    client.get(`/assets?${params}`).then((r) => setResult(r.data));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams.toString(), reloadTick]);

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
      setReloadTick((t) => t + 1);
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
      setReloadTick((t) => t + 1);
    }
  };

  const th = (label: string, sortField: string, filterKey?: string) => (
    <th className="sortable" onClick={() => cycleSort(sortField)}
      title="Click to sort">
      {label}{sortMark(sortField)}
      {filterKey && (
        <ColumnFilter filterKey={filterKey} selected={getList(filterKey)}
          onChange={(v) => setFilter(filterKey, v)} />
      )}
    </th>
  );

  const activeFilterCount = Object.keys(FILTERS).filter((k) => getList(k).length > 0).length;

  return (
    <div className="card">
      <div className="filters">
        <input
          placeholder="Search hostname, IP, owner, serial…"
          value={searchDraft}
          onChange={(e) => setSearchDraft(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' &&
            patch((p) => { searchDraft ? p.set('q', searchDraft) : p.delete('q'); })}
          style={{ width: 280 }}
        />
        <button onClick={() => patch((p) => { searchDraft ? p.set('q', searchDraft) : p.delete('q'); })}>
          Search
        </button>
        {activeFilterCount > 0 && (
          <button className="secondary"
            onClick={() => patch((p) => { Object.keys(FILTERS).forEach((k) => p.delete(`f_${k}`)); })}>
            Clear column filters ({activeFilterCount})
          </button>
        )}
        <button className="secondary" onClick={() => setShowMore(!showMore)}>
          {showMore ? 'Fewer filters' : 'More filters'}
        </button>
        <span className="muted" style={{ alignSelf: 'center', fontSize: 12 }}>
          Filter columns with the ▼ icon in each header.
        </span>
      </div>

      {showMore && (
        <div className="filters">
          <select value={searchParams.get('status') ?? ''}
            onChange={(e) => patch((p) => { e.target.value ? p.set('status', e.target.value) : p.delete('status'); })}>
            {['', 'Active', 'Inactive', 'Offline', 'Quarantined', 'Decommissioned']
              .map((t) => <option key={t} value={t}>{t || 'All statuses'}</option>)}
          </select>
          <select value={searchParams.get('policyExempt') ?? ''}
            onChange={(e) => patch((p) => { e.target.value ? p.set('policyExempt', e.target.value) : p.delete('policyExempt'); })}>
            <option value="">Exempt: any</option>
            <option value="true">Exempt only</option>
            <option value="false">Not exempt</option>
          </select>
          <input placeholder="Tag key" defaultValue={searchParams.get('tagKey') ?? ''}
            onKeyDown={(e) => e.key === 'Enter' &&
              patch((p) => { const v = (e.target as HTMLInputElement).value; v ? p.set('tagKey', v) : p.delete('tagKey'); })}
            style={{ width: 120 }} />
          <input placeholder="Tag value (optional)" defaultValue={searchParams.get('tagValue') ?? ''}
            onKeyDown={(e) => e.key === 'Enter' &&
              patch((p) => { const v = (e.target as HTMLInputElement).value; v ? p.set('tagValue', v) : p.delete('tagValue'); })}
            style={{ width: 150 }} />
        </div>
      )}

      {error && <div className="error" style={{ marginBottom: 10 }}>{error}</div>}
      <table className="data">
        <thead>
          <tr>
            {th('Hostname', 'hostname')}
            {th('Type', 'assetType', 'assetType')}
            {th('OS', 'os', 'os')}
            <th>IP</th>
            {th('Environment', 'environment', 'environment')}
            {th('Criticality', 'criticality', 'criticality')}
            {th('Compliance', 'complianceScore', 'complianceStatus')}
            {th('DQ', 'dataQualityScore')}
            <th>Sources<ColumnFilter filterKey="source" selected={getList('source')}
              onChange={(v) => setFilter('source', v)} /></th>
            {th('Last Seen', 'lastSeen')}
            <th></th>
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
              <td><span style={{ color: dqTone(a.dataQualityScore), fontWeight: 600 }}>
                {a.dataQualityScore}</span></td>
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
            <tr><td colSpan={11} className="muted">No assets match the current filters.</td></tr>
          )}
        </tbody>
      </table>

      {result && (
        <div className="pagination">
          <button className="secondary" disabled={page <= 1}
            onClick={() => patch((p) => p.set('page', String(page - 1)), false)}>Prev</button>
          <span>Page {result.page} of {result.totalPages || 1} — {result.totalCount} assets</span>
          <button className="secondary" disabled={page >= result.totalPages}
            onClick={() => patch((p) => p.set('page', String(page + 1)), false)}>Next</button>
        </div>
      )}
    </div>
  );
}
