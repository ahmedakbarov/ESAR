import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import client from '../api/client';
import { Modal } from './Ui';

function ChangePasswordModal({ onClose }: { onClose: () => void }) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState('');
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setError('');
    if (next !== confirm) { setError('New passwords do not match'); return; }
    if (next.length < 12) { setError('Password must be at least 12 characters'); return; }
    setBusy(true);
    try {
      await client.post('/auth/change-password', { currentPassword: current, newPassword: next });
      setDone(true);
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Failed to change password');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title="Change password" onClose={onClose}>
      {done ? (
        <>
          <p>Your password has been changed.</p>
          <div style={{ marginTop: 12 }}><button onClick={onClose}>Close</button></div>
        </>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <input type="password" placeholder="Current password" value={current}
            autoComplete="current-password" onChange={(e) => setCurrent(e.target.value)} />
          <input type="password" placeholder="New password (12+ chars)" value={next}
            autoComplete="new-password" minLength={12} onChange={(e) => setNext(e.target.value)} />
          <input type="password" placeholder="Confirm new password" value={confirm}
            autoComplete="new-password" onChange={(e) => setConfirm(e.target.value)} />
          {error && <div className="error">{error}</div>}
          <div>
            <button disabled={busy || !current || !next} onClick={submit}>
              {busy ? 'Saving…' : 'Update password'}
            </button>
          </div>
        </div>
      )}
    </Modal>
  );
}

export default function Layout() {
  const { displayName, logout } = useAuth();
  const navigate = useNavigate();
  const [showChangePassword, setShowChangePassword] = useState(false);

  const handleLogout = () => {
    logout();
    navigate('/login');
  };

  return (
    <div className="layout">
      <aside className="sidebar">
        <div className="brand">E<span>SAR</span></div>
        <nav>
          <div className="section">Overview</div>
          <NavLink to="/" end>Dashboard</NavLink>
          <NavLink to="/assets">Assets</NavLink>
          <NavLink to="/compliance">Compliance</NavLink>
          <div className="section">Operations</div>
          <NavLink to="/matching">Matching</NavLink>
          <NavLink to="/approvals">Approvals</NavLink>
          <NavLink to="/settings/integrations">Connectors</NavLink>
          <NavLink to="/incidents">Incidents</NavLink>
          <NavLink to="/reports">Reports</NavLink>
          <div className="section">Governance</div>
          <NavLink to="/policies">Policies</NavLink>
          <div className="section">Administration</div>
          <NavLink to="/settings">Settings</NavLink>
          <div className="sidebar-subnav">
            <NavLink to="/settings/security">Security</NavLink>
            <NavLink to="/settings/system">System</NavLink>
            <NavLink to="/settings/users-roles">Users &amp; Roles</NavLink>
            <NavLink to="/settings/authentication">Authentication</NavLink>
            <NavLink to="/settings/integrations">Integrations</NavLink>
            <NavLink to="/settings/sources">Sources</NavLink>
            <NavLink to="/settings/priorities">Priorities</NavLink>
            <NavLink to="/settings/audit">Audit Logs</NavLink>
          </div>
        </nav>
      </aside>
      <main className="main">
        <div className="topbar">
          <h1>Enterprise Security Asset Registry</h1>
          <div className="user">
            <span>{displayName}</span>
            <button className="secondary" onClick={() => setShowChangePassword(true)}>Change password</button>
            <button className="secondary" onClick={handleLogout}>Sign out</button>
          </div>
        </div>
        <Outlet />
      </main>
      {showChangePassword && <ChangePasswordModal onClose={() => setShowChangePassword(false)} />}
    </div>
  );
}
