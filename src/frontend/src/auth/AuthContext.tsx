import { createContext, useContext, useEffect, useState, ReactNode } from 'react';
import client from '../api/client';

interface AuthState {
  token: string | null;
  displayName: string | null;
  roles: string[];
  userId: string | null;
  login: (username: string, password: string) => Promise<void>;
  loginWithEntra: (idToken: string) => Promise<void>;
  loginWithLdap: (username: string, password: string) => Promise<void>;
  logout: () => void;
}

interface LoginResponse {
  token: string;
  displayName?: string;
  roles?: string[];
  userId?: string;
}

const AuthContext = createContext<AuthState>(null!);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(localStorage.getItem('esar_token'));
  const [displayName, setDisplayName] = useState<string | null>(localStorage.getItem('esar_name'));
  const [roles, setRoles] = useState<string[]>(JSON.parse(localStorage.getItem('esar_roles') || '[]'));
  const [userId, setUserId] = useState<string | null>(localStorage.getItem('esar_user_id'));

  // Shared tail for every login path — local password, Entra SSO, AD login all end up with the
  // same shape of response (an ESAR JWT + roles), just from a different endpoint.
  const applyLogin = (data: LoginResponse, fallbackName: string) => {
    localStorage.setItem('esar_token', data.token);
    localStorage.setItem('esar_name', data.displayName ?? fallbackName);
    localStorage.setItem('esar_roles', JSON.stringify(data.roles ?? []));
    if (data.userId) localStorage.setItem('esar_user_id', data.userId);
    else localStorage.removeItem('esar_user_id');
    setToken(data.token);
    setDisplayName(data.displayName ?? fallbackName);
    setRoles(data.roles ?? []);
    setUserId(data.userId ?? null);
  };

  const login = async (username: string, password: string) => {
    const { data } = await client.post('/auth/login', { username, password });
    applyLogin(data, username);
  };

  const loginWithEntra = async (idToken: string) => {
    const { data } = await client.post('/auth/login/entra', { idToken });
    applyLogin(data, 'you');
  };

  const loginWithLdap = async (username: string, password: string) => {
    const { data } = await client.post('/auth/login/ldap', { username, password });
    applyLogin(data, username);
  };

  const logout = () => {
    localStorage.removeItem('esar_token');
    localStorage.removeItem('esar_name');
    localStorage.removeItem('esar_roles');
    localStorage.removeItem('esar_user_id');
    setToken(null);
    setDisplayName(null);
    setRoles([]);
    setUserId(null);
  };

  useEffect(() => {
    if (!token) return;

    let timeoutId: number | undefined;
    let idleMinutes = 30;
    const events = ['click', 'keydown', 'mousemove', 'scroll', 'touchstart'];

    const resetTimer = () => {
      window.clearTimeout(timeoutId);
      timeoutId = window.setTimeout(logout, idleMinutes * 60 * 1000);
    };

    client.get('/auth/config')
      .then((r) => {
        const value = Number(r.data?.idleTimeoutMinutes);
        if (value > 0) idleMinutes = value;
      })
      .finally(resetTimer);

    events.forEach((event) => window.addEventListener(event, resetTimer, { passive: true }));
    resetTimer();

    return () => {
      window.clearTimeout(timeoutId);
      events.forEach((event) => window.removeEventListener(event, resetTimer));
    };
  }, [token]);

  return (
    <AuthContext.Provider value={{ token, displayName, roles, userId, login, loginWithEntra, loginWithLdap, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export const useAuth = () => useContext(AuthContext);
