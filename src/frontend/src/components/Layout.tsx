import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';

export default function Layout() {
  const { displayName, logout } = useAuth();
  const navigate = useNavigate();

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
          <NavLink to="/connectors">Connectors</NavLink>
          <NavLink to="/incidents">Incidents</NavLink>
          <NavLink to="/reports">Reports</NavLink>
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
            <button className="secondary" onClick={handleLogout}>Sign out</button>
          </div>
        </div>
        <Outlet />
      </main>
    </div>
  );
}
