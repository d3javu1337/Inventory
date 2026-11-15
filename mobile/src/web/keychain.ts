const STORAGE_KEY = 'asset-inventory-mobile.refresh-token';

function readCredentials() {
  const value = window.localStorage.getItem(STORAGE_KEY);
  if (!value) return null;
  if (!value.startsWith('{')) return { username: 'refresh-token', password: value };
  try {
    const credentials = JSON.parse(value) as { username?: unknown; password?: unknown } | null;
    return typeof credentials?.username === 'string' && typeof credentials.password === 'string'
      ? { username: credentials.username, password: credentials.password }
      : null;
  } catch { return null; }
}

export async function getGenericPassword() {
  return readCredentials() ?? false;
}

export async function setGenericPassword(username: string, password: string) {
  window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ username, password }));
  return true;
}

export async function resetGenericPassword() {
  window.localStorage.removeItem(STORAGE_KEY);
  return true;
}

export function observeStoredAccount(onChange: (userId: string | null) => void) {
  const onStorage = (event: StorageEvent) => {
    if (event.key !== STORAGE_KEY && event.key !== null) return;
    const credentials = readCredentials();
    if (credentials?.username !== 'refresh-token') onChange(credentials?.username ?? null);
  };
  window.addEventListener('storage', onStorage);
  return () => window.removeEventListener('storage', onStorage);
}
