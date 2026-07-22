import { createContext, useContext, useState, ReactNode } from 'react';
import client from '../api/client';

interface AuthState {
  token: string | null;
  displayName: string | null;
  roles: string[];
  userId: string | null;
  login: (username: string, password: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthState>(null!);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(localStorage.getItem('esar_token'));
  const [displayName, setDisplayName] = useState<string | null>(localStorage.getItem('esar_name'));
  const [roles, setRoles] = useState<string[]>(JSON.parse(localStorage.getItem('esar_roles') || '[]'));
  const [userId, setUserId] = useState<string | null>(localStorage.getItem('esar_user_id'));

  const login = async (username: string, password: string) => {
    const { data } = await client.post('/auth/login', { username, password });
    localStorage.setItem('esar_token', data.token);
    localStorage.setItem('esar_name', data.displayName ?? username);
    localStorage.setItem('esar_roles', JSON.stringify(data.roles ?? []));
    if (data.userId) localStorage.setItem('esar_user_id', data.userId);
    else localStorage.removeItem('esar_user_id');
    setToken(data.token);
    setDisplayName(data.displayName ?? username);
    setRoles(data.roles ?? []);
    setUserId(data.userId ?? null);
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

  return (
    <AuthContext.Provider value={{ token, displayName, roles, userId, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export const useAuth = () => useContext(AuthContext);
