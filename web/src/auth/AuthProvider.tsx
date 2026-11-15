import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { authApi, session } from '../api/httpClient';
import type { AuthUser, WebSessionResponse } from '../api/types';

interface AuthContextValue {
  user: AuthUser | null;
  loading: boolean;
  authError: string;
  startLogin: (login: string) => Promise<boolean>;
  login: (login: string, password: string) => Promise<void>;
  setInitialPassword: (login: string, password: string) => Promise<void>;
  logout: () => Promise<void>;
  changePassword: (currentPassword: string, newPassword: string) => Promise<void>;
}
const AuthContext = createContext<AuthContextValue | null>(null);
const logoutKey = 'asset-inventory.web.logged-out';
const accountKey = 'asset-inventory.web.account';
function rememberAccount(userId: string) {
  try {
    localStorage.setItem(accountKey, userId);
    localStorage.removeItem(logoutKey);
  } catch { /* Storage may be disabled by the browser. */ }
}
function rememberLogout(userId: string | null) {
  try {
    const activeAccount = localStorage.getItem(accountKey);
    if (!activeAccount || activeAccount === userId) localStorage.setItem(logoutKey, `${userId ?? ''}:${Date.now()}`);
  } catch { /* Storage may be disabled by the browser. */ }
}
function logoutApplies(marker: string, userId: string | null) {
  // Existing installs may still have a timestamp-only logout marker.
  if (!marker.includes(':')) return true;
  const account = marker.split(':', 1)[0];
  return !account || !userId || account === userId;
}
function wasLoggedOut() {
  try {
    const marker = localStorage.getItem(logoutKey);
    return marker !== null && logoutApplies(marker, localStorage.getItem(accountKey));
  } catch { return false; }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const client = useQueryClient();
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [authError, setAuthError] = useState('');
  const clear = useCallback(() => { session.setToken(null); client.clear(); setUser(null); }, [client]);
  const accept = useCallback(async (tokens: WebSessionResponse, replace: boolean) => {
    if (tokens.user.role !== 'Administrator') {
      rememberAccount(tokens.user.id);
      rememberLogout(tokens.user.id);
      clear();
      const message = 'Административная панель доступна только администратору. Для инвентаризатора используйте мобильное приложение.';
      setAuthError(message);
      await authApi.logout(tokens.user.id).catch(() => undefined);
      throw new Error(message);
    }
    if (replace) { session.replaceToken(tokens.accessToken, tokens.user.id); client.clear(); }
    else session.setToken(tokens.accessToken, tokens.user.id);
    rememberAccount(tokens.user.id);
    setAuthError(''); setUser(tokens.user);
  }, [clear, client]);
  useEffect(() => {
    let active = true;
    session.setExpiredHandler(() => { clear(); setAuthError('Сессия истекла. Войдите снова.'); });
    const onStorage = (event: StorageEvent) => {
      const userId = session.getUserId();
      if (!userId || event.newValue === null) return;
      if (event.key === accountKey && event.newValue !== userId) {
        clear(); setAuthError('В другой вкладке выполнен вход под другой учетной записью.');
      } else if (event.key === logoutKey && logoutApplies(event.newValue, userId)) {
        clear(); setAuthError('Выполнен выход в другой вкладке.');
      }
    };
    window.addEventListener('storage', onStorage);
    if (wasLoggedOut()) setLoading(false);
    else authApi.refresh().then(async result => { if (active && !wasLoggedOut()) await accept(result, false); }).catch(() => { if (active) clear(); }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; session.setExpiredHandler(null); window.removeEventListener('storage', onStorage); };
  }, [accept, clear]);
  const startLogin = useCallback(async (loginValue: string) => { setAuthError(''); return (await authApi.startLogin(loginValue)).passwordSetupRequired; }, []);
  const login = useCallback(async (loginValue: string, password: string) => { await accept(await authApi.login(loginValue, password), true); }, [accept]);
  const setInitialPassword = useCallback(async (loginValue: string, password: string) => { await accept(await authApi.setInitialPassword(loginValue, password), true); }, [accept]);
  const logout = useCallback(async () => {
    const userId = session.getUserId();
    setAuthError(''); rememberLogout(userId); clear();
    try { await authApi.logout(userId); } catch { /* Local logout remains effective when the server is unavailable. */ }
  }, [clear]);
  const changePassword = useCallback(async (current: string, password: string) => { await accept(await authApi.changePassword(current, password), false); }, [accept]);
  const value = useMemo(() => ({ user, loading, authError, startLogin, login, setInitialPassword, logout, changePassword }), [user, loading, authError, startLogin, login, setInitialPassword, logout, changePassword]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
export const useAuth = () => { const value = useContext(AuthContext); if (!value) throw new Error('AuthProvider missing'); return value; };
