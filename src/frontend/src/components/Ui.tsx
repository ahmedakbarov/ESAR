import { ReactNode } from 'react';

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
