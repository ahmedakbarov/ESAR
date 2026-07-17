import { FormEvent, useEffect, useState } from 'react';
import client from '../api/client';
import { Badge, formatDate } from '../components/Ui';

export default function Users() {
  const [users, setUsers] = useState<any[]>([]);
  const [roles, setRoles] = useState<any[]>([]);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState({ username: '', email: '', displayName: '', password: '', role: 'Viewer' });
  const [error, setError] = useState('');
  const [resetUser, setResetUser] = useState<any>(null);
  const [newPassword, setNewPassword] = useState('');

  const load = () => {
    client.get('/users').then((r) => setUsers(r.data));
    client.get('/roles').then((r) => setRoles(r.data));
  };

  const resetPassword = async () => {
    if (newPassword.length < 12) { setError('Password must be at least 12 characters.'); return; }
    try { await client.post(`/users/${resetUser.id}/reset-password`, { newPassword }); setResetUser(null); setNewPassword(''); setError(''); }
    catch (err: any) { setError(err.response?.data?.error ?? 'Password reset failed'); }
  };
  useEffect(load, []);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      await client.post('/users', { ...form, roles: [form.role] });
      setShowForm(false);
      setForm({ username: '', email: '', displayName: '', password: '', role: 'Viewer' });
      load();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Failed to create user');
    }
  };

  return (
    <>
      <div className="card">
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
          <h3>Users</h3>
          <button onClick={() => setShowForm(!showForm)}>{showForm ? 'Cancel' : 'New user'}</button>
        </div>
        {showForm && (
          <form onSubmit={create} className="filters" style={{ marginBottom: 16 }}>
            <input placeholder="Username" value={form.username}
              onChange={(e) => setForm({ ...form, username: e.target.value })} required />
            <input placeholder="Email" type="email" value={form.email}
              onChange={(e) => setForm({ ...form, email: e.target.value })} required />
            <input placeholder="Display name" value={form.displayName}
              onChange={(e) => setForm({ ...form, displayName: e.target.value })} required />
            <input placeholder="Password (12+ chars)" type="password" value={form.password}
              onChange={(e) => setForm({ ...form, password: e.target.value })} required minLength={12} />
            <select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value })}>
              {roles.map((r) => <option key={r.id}>{r.name}</option>)}
            </select>
            <button type="submit">Create</button>
            {error && <span className="error">{error}</span>}
          </form>
        )}
        <table className="data">
          <thead>
            <tr><th>Username</th><th>Display Name</th><th>Email</th><th>Provider</th>
              <th>Roles</th><th>Active</th><th>Last Login</th><th>Actions</th></tr>
          </thead>
          <tbody>
            {users.map((u) => (
              <tr key={u.id}>
                <td>{u.username}</td>
                <td>{u.displayName}</td>
                <td className="muted">{u.email}</td>
                <td><Badge value={u.provider} /></td>
                <td>{u.roles.map((r: string) => <span key={r} className="badge blue" style={{ marginRight: 4 }}>{r}</span>)}</td>
                <td>{u.isActive ? <Badge value="Active" /> : <Badge value="Inactive" />}</td>
                <td className="muted">{formatDate(u.lastLoginAt)}</td>
                <td>{u.provider === 'Local' && <button className="secondary" onClick={() => { setResetUser(u); setNewPassword(''); setError(''); }}>Reset password</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {resetUser && <div className="card" style={{ position: 'fixed', top: 100, right: 24, width: 360, zIndex: 10 }}>
        <h3>Reset password: {resetUser.username}</h3>
        <input type="password" placeholder="New password (12+ characters)" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} />
        {error && <div className="error" style={{ marginTop: 8 }}>{error}</div>}
        <div style={{ marginTop: 10 }}><button onClick={resetPassword}>Reset</button><button className="secondary" onClick={() => setResetUser(null)} style={{ marginLeft: 6 }}>Cancel</button></div>
      </div>}

      <h2 className="section-title">Roles</h2>
      <div className="card">
        <table className="data">
          <thead><tr><th>Role</th><th>Description</th><th>Permissions</th></tr></thead>
          <tbody>
            {roles.map((r) => (
              <tr key={r.id}>
                <td>{r.name} {r.isSystem && <span className="badge muted">system</span>}</td>
                <td className="muted">{r.description}</td>
                <td className="muted" style={{ maxWidth: 480 }}>{r.permissions?.join(', ')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
