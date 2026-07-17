import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useEffect, useState } from 'react';
import client from '../api/client';
import { useAuth } from '../auth/AuthContext';

export default function Layout() {
  const { displayName, logout } = useAuth();
  const navigate = useNavigate();
  const [counts, setCounts] = useState({ matching: 0, approvals: 0 });
  const [changePassword, setChangePassword] = useState(false);
  const [passwords, setPasswords] = useState({ currentPassword: '', newPassword: '', confirm: '' });
  const [passwordMessage, setPasswordMessage] = useState('');

  useEffect(() => {
    const load = () => Promise.all([client.get('/matching/review-queue/count'), client.get('/approvals/pending/count')])
      .then(([matching, approvals]) => setCounts({ matching: matching.data.count, approvals: approvals.data.count }))
      .catch(() => undefined);
    load(); const timer = window.setInterval(load, 60000);
    window.addEventListener('esar:counts-changed', load);
    return () => { window.clearInterval(timer); window.removeEventListener('esar:counts-changed', load); };
  }, []);

  const handleLogout = () => {
    logout();
    navigate('/login');
  };

  const submitPassword = async () => {
    setPasswordMessage('');
    if (passwords.newPassword.length < 12) return setPasswordMessage('New password must be at least 12 characters.');
    if (passwords.newPassword !== passwords.confirm) return setPasswordMessage('New passwords do not match.');
    try { await client.post('/auth/change-password', passwords); setPasswordMessage('Password changed successfully.'); setPasswords({ currentPassword: '', newPassword: '', confirm: '' }); }
    catch (err: any) { setPasswordMessage(err.response?.data?.error ?? 'Password change failed.'); }
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
          <NavLink to="/matching">Matching {counts.matching > 0 && <span className="badge">{counts.matching}</span>}</NavLink>
          <NavLink to="/approvals">Approvals {counts.approvals > 0 && <span className="badge">{counts.approvals}</span>}</NavLink>
          <NavLink to="/connectors">Connectors</NavLink>
          <NavLink to="/incidents">Incidents</NavLink>
          <NavLink to="/reports">Reports</NavLink>
          <div className="section">Governance</div>
          <NavLink to="/policies">Policies</NavLink>
          <div className="section">Administration</div>
          <NavLink to="/audit">Audit Logs</NavLink>
          <NavLink to="/users">Users &amp; Roles</NavLink>
          <NavLink to="/settings">Settings</NavLink>
        </nav>
      </aside>
      <main className="main">
        <div className="topbar">
          <h1>Enterprise Security Asset Registry</h1>
          <div className="user">
            <span>{displayName}</span>
            <button className="secondary" onClick={() => { setChangePassword(true); setPasswordMessage(''); }}>Change password</button>
            <button className="secondary" onClick={handleLogout}>Sign out</button>
          </div>
        </div>
        <Outlet />
        {changePassword && <div className="card" style={{ position: 'fixed', top: 80, right: 24, width: 360, zIndex: 10 }}>
          <h3>Change password</h3>
          <input type="password" placeholder="Current password" value={passwords.currentPassword} onChange={(e) => setPasswords({ ...passwords, currentPassword: e.target.value })} />
          <input type="password" placeholder="New password (12+ characters)" value={passwords.newPassword} onChange={(e) => setPasswords({ ...passwords, newPassword: e.target.value })} style={{ marginTop: 8 }} />
          <input type="password" placeholder="Confirm new password" value={passwords.confirm} onChange={(e) => setPasswords({ ...passwords, confirm: e.target.value })} style={{ marginTop: 8 }} />
          {passwordMessage && <div className="error" style={{ marginTop: 8 }}>{passwordMessage}</div>}
          <div style={{ marginTop: 10 }}><button onClick={submitPassword}>Save</button><button className="secondary" onClick={() => setChangePassword(false)} style={{ marginLeft: 6 }}>Cancel</button></div>
        </div>}
      </main>
    </div>
  );
}
