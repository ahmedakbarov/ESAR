import { FormEvent, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import client from '../api/client';
import { useAuth } from '../auth/AuthContext';
import { getMsal } from '../auth/msal';

interface AuthConfig {
  entraEnabled: boolean;
  ldapEnabled: boolean;
  entraTenantId?: string;
  entraClientId?: string;
}

export default function Login() {
  const { login, loginWithEntra, loginWithLdap } = useAuth();
  const navigate = useNavigate();
  const [config, setConfig] = useState<AuthConfig | null>(null);
  const [mode, setMode] = useState<'local' | 'ldap'>('local');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    client.get('/auth/config').then((r) => setConfig(r.data)).catch(() => setConfig({ entraEnabled: false, ldapEnabled: false }));
  }, []);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      if (mode === 'local') await login(username, password);
      else await loginWithLdap(username, password);
      navigate('/');
    } catch (err: any) {
      setError(err.response?.data?.error ?? 'Login failed');
    } finally {
      setBusy(false);
    }
  };

  // Must run synchronously from the click handler (no prior await) — otherwise Chrome/Edge treat
  // the popup as not user-initiated and silently block it.
  const microsoftLogin = () => {
    if (!config?.entraClientId || !config.entraTenantId) return;
    setError('');
    setBusy(true);
    getMsal(config.entraClientId, config.entraTenantId)
      .then((msal) => msal.loginPopup({ scopes: ['openid', 'profile', 'email'] }))
      .then((result) => loginWithEntra(result.idToken))
      .then(() => navigate('/'))
      .catch((err: any) => setError(err.response?.data?.error ?? err.errorMessage ?? 'Microsoft sign-in failed'))
      .finally(() => setBusy(false));
  };

  return (
    <div className="login-page">
      <div className="card login-card">
        <h2>E<span style={{ color: 'var(--accent)' }}>SAR</span></h2>
        <p>Enterprise Security Asset Registry</p>

        {config?.ldapEnabled && (
          <div style={{ display: 'flex', gap: 6, marginBottom: 12 }}>
            <button type="button" className={mode === 'local' ? '' : 'secondary'} onClick={() => setMode('local')}>
              Local
            </button>
            <button type="button" className={mode === 'ldap' ? '' : 'secondary'} onClick={() => setMode('ldap')}>
              Active Directory
            </button>
          </div>
        )}

        <form onSubmit={submit}>
          <input
            placeholder={mode === 'ldap' ? 'AD username' : 'Username'}
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            autoFocus
          />
          <input
            placeholder="Password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          {error && <div className="error">{error}</div>}
          <button disabled={busy || !username || !password}>
            {busy ? 'Signing in…' : 'Sign in'}
          </button>
        </form>

        {config?.entraEnabled && (
          <>
            <div className="muted" style={{ textAlign: 'center', margin: '14px 0' }}>or</div>
            <button type="button" className="secondary" style={{ width: '100%' }}
              disabled={busy} onClick={microsoftLogin}>
              Sign in with Microsoft
            </button>
          </>
        )}
      </div>
    </div>
  );
}
