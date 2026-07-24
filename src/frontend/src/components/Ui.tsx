import { ReactNode, useState } from 'react';

export function StatCard({ label, value, hint, tone }: {
  label: string; value: string | number; hint?: string; tone?: 'good' | 'warn' | 'bad';
}) {
  return (
    <div className={`card stat ${tone ?? ''}`}>
      <h3>{label}</h3>
      <div className="value">{value}</div>
      {hint && <div className="hint">{hint}</div>}
    </div>
  );
}

const toneMap: Record<string, string> = {
  Compliant: 'green', NonCompliant: 'red', Pending: 'amber', Unknown: 'muted',
  Active: 'green', Inactive: 'muted', Offline: 'amber', Decommissioned: 'muted', Quarantined: 'red',
  Critical: 'red', High: 'amber', Medium: 'blue', Low: 'muted',
  Open: 'red', InProgress: 'amber', Resolved: 'green', Closed: 'muted',
  Succeeded: 'green', Failed: 'red', Running: 'blue',
  Production: 'blue', Test: 'muted', Development: 'muted', Staging: 'amber',
  'Policy Exempt': 'amber',
};

export function Badge({ value }: { value?: string }) {
  if (!value) return <span className="badge muted">—</span>;
  return <span className={`badge ${toneMap[value] ?? ''}`}>{value}</span>;
}

export function Panel({ title, children, actions }: {
  title: string; children: ReactNode; actions?: ReactNode;
}) {
  return (
    <div className="card">
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h3>{title}</h3>
        {actions}
      </div>
      {children}
    </div>
  );
}

export function formatDate(value?: string) {
  if (!value) return '—';
  return new Date(value).toLocaleString();
}

/// Free-form list editor: type a value, press Enter/comma or blur to add it as a removable chip.
export function ChipListInput({ values, onChange, placeholder }: {
  values: string[]; onChange: (values: string[]) => void; placeholder?: string;
}) {
  const [draft, setDraft] = useState('');

  const commit = () => {
    const trimmed = draft.trim();
    if (trimmed && !values.includes(trimmed)) onChange([...values, trimmed]);
    setDraft('');
  };

  return (
    <div>
      {values.length > 0 && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 6 }}>
          {values.map((v) => (
            <span key={v} className="badge" style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              {v}
              <button type="button" onClick={() => onChange(values.filter((x) => x !== v))}
                aria-label={`Remove ${v}`}
                style={{ all: 'unset', cursor: 'pointer', lineHeight: 1, fontWeight: 700 }}>×</button>
            </span>
          ))}
        </div>
      )}
      <input
        value={draft}
        placeholder={placeholder}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); commit(); }
        }}
        onBlur={commit}
        style={{ width: 280 }}
      />
    </div>
  );
}

export function Modal({ title, onClose, children }: {
  title: string; onClose: () => void; children: ReactNode;
}) {
  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.55)', zIndex: 200,
        display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 16,
      }}
    >
      <div onClick={(e) => e.stopPropagation()} className="card"
        style={{ minWidth: 340, maxWidth: 460, width: '100%' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
          <h3>{title}</h3>
          <button className="secondary" onClick={onClose} aria-label="Close">✕</button>
        </div>
        {children}
      </div>
    </div>
  );
}

export interface RowMenuItem {
  label: string;
  onClick: () => void;
  danger?: boolean;
  disabled?: boolean;
  title?: string;
  /// Draws a divider above this item — use to separate destructive actions.
  separatorBefore?: boolean;
}

/// Kebab (three-dot) row-action menu. Renders nothing when there are no items, so callers can
/// build the item list conditionally (permissions, protected accounts, …) and let it collapse.
export function RowMenu({ items, disabled }: { items: RowMenuItem[]; disabled?: boolean }) {
  const [open, setOpen] = useState(false);
  if (items.length === 0) return null;
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
                  disabled={item.disabled} title={item.title}
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
