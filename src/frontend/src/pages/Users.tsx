import { FormEvent, useEffect, useState } from 'react';
import client from '../api/client';
import { Badge, formatDate, Modal } from '../components/Ui';
import { useAuth } from '../auth/AuthContext';

function ResetPasswordModal({ user, onClose, onDone, minPasswordLength }: {
  user: { id: string; username: string }; onClose: () => void; onDone: () => void; minPasswordLength: number;
}) {
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    setError('');
    if (password.length < minPasswordLength) {
      setError(`Password must be at least ${minPasswordLength} characters`);
      return;
    }
    setBusy(true);
    try {
      await client.put(`/users/${user.id}`, { newPassword: password });
      onDone();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Failed to reset password');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={`Reset password — ${user.username}`} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <p className="muted" style={{ margin: 0 }}>
          Sets a new password for this user. Share it over a secure channel; they should change it after signing in.
        </p>
        <input type="password" placeholder={`New password (${minPasswordLength}+ chars)`} value={password}
          autoComplete="new-password" minLength={minPasswordLength} onChange={(e) => setPassword(e.target.value)} />
        {error && <div className="error">{error}</div>}
        <div>
          <button disabled={busy || !password} onClick={submit}>{busy ? 'Saving…' : 'Reset password'}</button>
        </div>
      </div>
    </Modal>
  );
}

function EditUserModal({ user, roles, onClose, onDone }: {
  user: any; roles: any[]; onClose: () => void; onDone: () => void;
}) {
  const [email, setEmail] = useState(user.email ?? '');
  const [displayName, setDisplayName] = useState(user.displayName ?? '');
  const [isActive, setIsActive] = useState(!!user.isActive);
  const [selectedRoles, setSelectedRoles] = useState<string[]>(user.roles ?? []);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const toggleRole = (name: string) => {
    setSelectedRoles((prev) => prev.includes(name) ? prev.filter((r) => r !== name) : [...prev, name]);
  };

  const submit = async () => {
    setError('');
    setBusy(true);
    try {
      await client.put(`/users/${user.id}`, { email, displayName, isActive, roles: selectedRoles });
      onDone();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Failed to update user');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={`Edit user — ${user.username}`} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <input type="email" placeholder="Email" value={email} onChange={(e) => setEmail(e.target.value)} />
        <input placeholder="Display name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)}
            style={{ width: 'auto' }} />
          Active
        </label>
        <div>
          <div className="muted" style={{ marginBottom: 6 }}>Roles</div>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
            {roles.map((r) => (
              <label key={r.id} className="badge" style={{ cursor: 'pointer' }}>
                <input type="checkbox" checked={selectedRoles.includes(r.name)}
                  onChange={() => toggleRole(r.name)} style={{ width: 'auto', marginRight: 6 }} />
                {r.name}
              </label>
            ))}
          </div>
        </div>
        {error && <div className="error">{error}</div>}
        <div style={{ display: 'flex', gap: 8 }}>
          <button disabled={busy || !email || !displayName} onClick={submit}>
            {busy ? 'Saving...' : 'Save changes'}
          </button>
          <button className="secondary" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </Modal>
  );
}

function RoleModal({ role, permissions, onClose, onDone }: {
  role?: any; permissions: any[]; onClose: () => void; onDone: () => void;
}) {
  const [name, setName] = useState(role?.name ?? '');
  const [description, setDescription] = useState(role?.description ?? '');
  const [selectedPermissions, setSelectedPermissions] = useState<string[]>(role?.permissions ?? []);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const togglePermission = (code: string) => {
    setSelectedPermissions((prev) =>
      prev.includes(code) ? prev.filter((p) => p !== code) : [...prev, code]);
  };

  const submit = async () => {
    setError('');
    setBusy(true);
    try {
      const payload = { name, description, permissions: selectedPermissions };
      if (role) await client.put(`/roles/${role.id}`, payload);
      else await client.post('/roles', payload);
      onDone();
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Failed to save role');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={role ? `Edit role — ${role.name}` : 'New role'} onClose={onClose}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        <input placeholder="Role name" value={name} disabled={!!role}
          onChange={(e) => setName(e.target.value)} />
        <input placeholder="Description" value={description}
          onChange={(e) => setDescription(e.target.value)} />
        <div>
          <div className="muted" style={{ marginBottom: 6 }}>Permissions</div>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', maxHeight: 260, overflow: 'auto' }}>
            {permissions.map((p) => (
              <label key={p.id} className="badge" style={{ cursor: 'pointer' }}>
                <input type="checkbox" checked={selectedPermissions.includes(p.code)}
                  onChange={() => togglePermission(p.code)} style={{ width: 'auto', marginRight: 6 }} />
                {p.code}
              </label>
            ))}
          </div>
        </div>
        {error && <div className="error">{error}</div>}
        <div style={{ display: 'flex', gap: 8 }}>
          <button disabled={busy || !name || selectedPermissions.length === 0} onClick={submit}>
            {busy ? 'Saving...' : 'Save role'}
          </button>
          <button className="secondary" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </Modal>
  );
}

export default function Users() {
  const { userId: myUserId } = useAuth();
  const [users, setUsers] = useState<any[]>([]);
  const [roles, setRoles] = useState<any[]>([]);
  const [permissions, setPermissions] = useState<any[]>([]);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState({
    username: '', email: '', displayName: '', password: '', provider: 'Local', role: 'Viewer',
  });
  const [error, setError] = useState('');
  const [resetUser, setResetUser] = useState<{ id: string; username: string } | null>(null);
  const [editUser, setEditUser] = useState<any | null>(null);
  const [roleModal, setRoleModal] = useState<any | 'new' | null>(null);
  const [notice, setNotice] = useState('');
  const [actionError, setActionError] = useState('');
  const [minPasswordLength, setMinPasswordLength] = useState(12);

  const load = () => {
    client.get('/users').then((r) => setUsers(r.data));
    client.get('/roles').then((r) => setRoles(r.data));
    client.get('/roles/permissions').then((r) => setPermissions(r.data));
    client.get('/auth/config')
      .then((r) => {
        const value = Number(r.data?.minPasswordLength);
        if (value > 0) setMinPasswordLength(value);
      })
      .catch(() => undefined);
  };
  useEffect(load, []);

  const toggleActive = async (u: any) => {
    setActionError('');
    try {
      await client.put(`/users/${u.id}`, { isActive: !u.isActive });
      load();
    } catch (err: any) {
      setActionError(err.response?.data?.error ?? 'Failed to update user');
    }
  };

  const removeUser = async (id: string, username: string) => {
    if (!window.confirm(
      `Delete user "${username}"? This permanently removes the account and cannot be undone. ` +
      'Consider Deactivate instead if you may need this account again.'
    )) return;
    setActionError('');
    try {
      await client.delete(`/users/${id}`);
      load();
    } catch (err: any) {
      setActionError(err.response?.data?.error ?? 'Delete failed');
    }
  };

  // Mirrors the backend's last-manager guard (AdminControllers.cs, IsLastUserManagerAsync) so the
  // Delete button can be disabled with an explanation instead of always failing after a click.
  const managerRoleNames = new Set(
    roles.filter((r) => r.permissions?.includes('users.manage')).map((r) => r.name)
  );
  const managerIds = users
    .filter((u) => u.isActive && u.roles.some((rn: string) => managerRoleNames.has(rn)))
    .map((u) => u.id);

  const removeRole = async (id: string, name: string) => {
    if (!window.confirm(`Delete role "${name}"? This cannot be undone.`)) return;
    setActionError('');
    try {
      await client.delete(`/roles/${id}`);
      load();
    } catch (err: any) {
      setActionError(err.response?.data?.error ?? 'Delete failed');
    }
  };

  const unlockUser = async (u: any) => {
    setActionError('');
    try {
      await client.post(`/users/${u.id}/unlock`);
      setNotice(`Unlocked ${u.username}.`);
      load();
    } catch (err: any) {
      setActionError(err.response?.data?.error ?? 'Unlock failed');
    }
  };

  const create = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      // The password field is hidden for federated providers, but form state can still hold a
      // stale value from before the provider was switched — strip it so we never send one.
      const { password, ...rest } = form;
      const payload = { ...rest, password: form.provider === 'Local' ? password : null, roles: [form.role] };
      await client.post('/users', payload);
      setShowForm(false);
      setForm({ username: '', email: '', displayName: '', password: '', provider: 'Local', role: 'Viewer' });
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
        {notice && <div className="muted" style={{ marginBottom: 10 }}>{notice}</div>}
        {actionError && <div className="error" style={{ marginBottom: 10 }}>{actionError}</div>}
        {showForm && (
          <form onSubmit={create} className="filters" style={{ marginBottom: 16 }}>
            <input placeholder="Username" value={form.username}
              onChange={(e) => setForm({ ...form, username: e.target.value })} required />
            <input placeholder="Email" type="email" value={form.email}
              onChange={(e) => setForm({ ...form, email: e.target.value })} required />
            <input placeholder="Display name" value={form.displayName}
              onChange={(e) => setForm({ ...form, displayName: e.target.value })} required />
            <select value={form.provider} onChange={(e) => setForm({ ...form, provider: e.target.value })}>
              <option value="Local">Local (password)</option>
              <option value="EntraId">Azure AD / Entra ID (SSO)</option>
              <option value="Ldap">Active Directory (AD login)</option>
            </select>
            {form.provider === 'Local' && (
              <input placeholder={`Password (${minPasswordLength}+ chars)`} type="password" value={form.password}
                onChange={(e) => setForm({ ...form, password: e.target.value })}
                required minLength={minPasswordLength} />
            )}
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
              <th>Roles</th><th>Status</th><th>Last Login</th><th>Actions</th></tr>
          </thead>
          <tbody>
            {users.map((u) => {
              const isSelf = !!myUserId && u.id === myUserId;
              const isLastManager = managerIds.length === 1 && managerIds[0] === u.id;
              const lockedUntil = u.lockedOutUntil ? new Date(u.lockedOutUntil) : null;
              const isLocked = !!lockedUntil && lockedUntil > new Date();
              const deleteBlock = u.isProtected ? 'This is a protected account and cannot be deleted.'
                : isSelf ? 'You cannot delete your own account.'
                : isLastManager ? 'This is the last user with user-management permission.' : undefined;
              return (
                <tr key={u.id}>
                  <td>
                    {u.username}
                    {u.isProtected && <> <span className="badge muted" title="Protected account — cannot be modified or deleted">protected</span></>}
                  </td>
                  <td>{u.displayName}</td>
                  <td className="muted">{u.email}</td>
                  <td><Badge value={u.provider} /></td>
                  <td>{u.roles.map((r: string) => <span key={r} className="badge blue" style={{ marginRight: 4 }}>{r}</span>)}</td>
                  <td>
                    <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', alignItems: 'center' }}>
                      {u.isActive ? <Badge value="Active" /> : <Badge value="Inactive" />}
                      {isLocked && <span className="badge red" title={`Locked until ${formatDate(u.lockedOutUntil)}`}>Locked</span>}
                      {!isLocked && u.failedLoginAttempts > 0 && (
                        <span className="badge amber" title="Failed local-password login attempts">
                          {u.failedLoginAttempts} failed
                        </span>
                      )}
                    </div>
                  </td>
                  <td className="muted">{formatDate(u.lastLoginAt)}</td>
                  <td>
                    {u.isProtected ? (
                      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', alignItems: 'center' }}>
                        <span className="muted" style={{ fontSize: 12 }}>protected</span>
                        {u.provider === 'Local' && isLocked && (
                          <button className="secondary" onClick={() => unlockUser(u)}>Unlock</button>
                        )}
                      </div>
                    ) : (
                      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                        {u.provider === 'Local' && (
                          <button className="secondary"
                            onClick={() => { setNotice(''); setResetUser({ id: u.id, username: u.username }); }}>
                            Reset password
                          </button>
                        )}
                        {u.provider === 'Local' && isLocked && (
                          <button className="secondary" onClick={() => unlockUser(u)}>Unlock</button>
                        )}
                        <button className="secondary" onClick={() => setEditUser(u)}>Edit</button>
                        <button className="secondary" onClick={() => toggleActive(u)}>
                          {u.isActive ? 'Deactivate' : 'Activate'}
                        </button>
                        <button className="danger" disabled={!!deleteBlock} title={deleteBlock}
                          onClick={() => removeUser(u.id, u.username)}>
                          Delete
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <h2 className="section-title">Roles</h2>
      <div className="card">
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
          <h3>Role definitions</h3>
          <button onClick={() => setRoleModal('new')}>New role</button>
        </div>
        <table className="data">
          <thead><tr><th>Role</th><th>Description</th><th>Permissions</th><th></th></tr></thead>
          <tbody>
            {roles.map((r) => (
              <tr key={r.id}>
                <td>{r.name} {r.isSystem && <span className="badge muted">system</span>}</td>
                <td className="muted">{r.description}</td>
                <td className="muted" style={{ maxWidth: 480 }}>{r.permissions?.join(', ')}</td>
                <td>
                  {r.isSystem
                    ? <span className="muted" style={{ fontSize: 12 }}>built-in</span>
                    : (
                      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                        <button className="secondary" onClick={() => setRoleModal(r)}>Edit</button>
                        <button className="danger" onClick={() => removeRole(r.id, r.name)}>Delete</button>
                      </div>
                    )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {resetUser && (
        <ResetPasswordModal
          user={resetUser}
          onClose={() => setResetUser(null)}
          onDone={() => { setNotice(`Password reset for ${resetUser.username}.`); setResetUser(null); load(); }}
          minPasswordLength={minPasswordLength}
        />
      )}
      {editUser && (
        <EditUserModal
          user={editUser}
          roles={roles}
          onClose={() => setEditUser(null)}
          onDone={() => { setNotice(`Updated ${editUser.username}.`); setEditUser(null); load(); }}
        />
      )}
      {roleModal && (
        <RoleModal
          role={roleModal === 'new' ? undefined : roleModal}
          permissions={permissions}
          onClose={() => setRoleModal(null)}
          onDone={() => { setNotice('Role saved.'); setRoleModal(null); load(); }}
        />
      )}
    </>
  );
}
