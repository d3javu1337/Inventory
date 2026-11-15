import { getRefreshCredentials as loadRefreshCredentials, saveRefreshToken, clearRefreshToken } from '../storage/secureSession';
import { AuthUser, TokenResponse } from '../types/api';
import { queryClient } from '../query/QueryProvider';
import { observeStoredAccount, withSessionLock } from './sessionPlatform';

let accessToken: string | null = null;
let currentUser: AuthUser | null = null;
const listeners = new Set<() => void>();
let revision = 0;
let storageWrites: Promise<void> = Promise.resolve();
function writeStorage(action: () => Promise<void>) {
  const operation = storageWrites.then(action);
  storageWrites = operation.catch(() => undefined);
  return operation;
}

function clearLocal() {
  revision++;
  accessToken = null;
  currentUser = null;
  queryClient.clear();
  listeners.forEach(listener => listener());
}

async function getRefreshCredentials() {
  await storageWrites;
  return loadRefreshCredentials();
}

function clearStored(expectedToken?: string) {
  return writeStorage(async () => {
    if (expectedToken === undefined || (await loadRefreshCredentials())?.token === expectedToken)
      await clearRefreshToken();
  });
}

export const session = {
  getAccessToken: () => accessToken,
  getUser: () => currentUser,
  getRevision: () => revision,
  setTokens: async (tokens: TokenResponse, expectedRevision = revision) => {
    await writeStorage(async () => {
      if (expectedRevision !== revision) throw new Error('Сессия изменилась. Повторите вход.');
      // Publish the account only after secure storage succeeds. A concurrent
      // logout advances revision immediately and queues credential removal.
      await saveRefreshToken(tokens.refreshToken, tokens.user.id);
      if (expectedRevision !== revision) throw new Error('Сессия изменилась. Повторите вход.');
      if (currentUser?.id !== tokens.user.id) { revision++; queryClient.clear(); }
      accessToken = tokens.accessToken;
      currentUser = tokens.user;
    });
  },
  clearLocal,
  clear: async (expectedToken?: string) => {
    clearLocal();
    await clearStored(expectedToken);
  },
  signOut: async () => {
    const userId = currentUser?.id;
    clearLocal();
    const signOutRevision = revision;
    if (!userId) return null;
    return withSessionLock(async () => {
      const saved = await getRefreshCredentials();
      if (revision !== signOutRevision || !saved || (saved.userId && saved.userId !== userId)) return null;
      await clearStored(saved.token);
      return saved.token;
    });
  },
  getRefreshCredentials,
  subscribe: (listener: () => void) => {
    listeners.add(listener);
    const unsubscribe = observeStoredAccount(userId => {
      if (currentUser && userId !== currentUser.id) clearLocal();
    });
    return () => { listeners.delete(listener); unsubscribe(); };
  },
};
