import { session } from '../auth/session';
import { withSessionLock } from '../auth/sessionPlatform';
import { ProblemDetails, TokenResponse } from '../types/api';
import { Platform } from 'react-native';
import { fetchWithTimeout } from './request';

// These two expressions are replaced with literals by babel.config.js; no Node runtime is used on Android.
declare const process: { env: { ASSET_DEBUG_API_URL?: string; ASSET_RELEASE_API_URL?: string } };

const DEBUG_API_BASE_URL = process.env.ASSET_DEBUG_API_URL || 'http://10.0.2.2:5147/api/v1';
const RELEASE_API_BASE_URL = process.env.ASSET_RELEASE_API_URL || 'https://asset-inventory.example/api/v1';
export const API_BASE_URL = Platform.OS === 'web' ? '/api/v1' : (__DEV__ ? DEBUG_API_BASE_URL : RELEASE_API_BASE_URL);

let refreshInFlight: { revision: number; promise: Promise<boolean> } | null = null;

async function parseResponse<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    const problem = (body ?? {}) as ProblemDetails;
    const error = new Error(problem.detail ?? problem.title ?? `HTTP ${response.status}`) as Error & { problem?: ProblemDetails; status?: number };
    error.problem = problem;
    error.status = response.status;
    throw error;
  }
  return body as T;
}

async function refresh(revision: number): Promise<boolean> {
  const credentials = await session.getRefreshCredentials();
  if (!credentials || revision !== session.getRevision()) return false;
  const userId = session.getUser()?.id;
  if (userId && credentials.userId && userId !== credentials.userId) {
    session.clearLocal();
    return false;
  }
  const expectedUserId = userId ?? credentials.userId;
  const headers = new Headers({ 'Content-Type': 'application/json' });
  if (expectedUserId) headers.set('X-Expected-User-Id', expectedUserId);
  const response = await fetchWithTimeout(`${API_BASE_URL}/auth/mobile/refresh`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ refreshToken: credentials.token, device: Platform.OS }),
  });
  if (revision !== session.getRevision()) return false;
  if (response.status === 401) {
    const problem = await response.json().catch(() => null) as ProblemDetails | null;
    if (revision !== session.getRevision()) return false;
    if (problem?.code === 'session_account_mismatch') session.clearLocal();
    else await session.clear(credentials.token);
    return false;
  }
  if (!response.ok) await parseResponse(response);
  const tokens = (await response.json()) as TokenResponse;
  if (revision !== session.getRevision()) return false;
  if (expectedUserId && tokens.user.id !== expectedUserId) {
    session.clearLocal();
    return false;
  }
  await session.setTokens(tokens, revision);
  return session.getAccessToken() === tokens.accessToken;
}

// Bootstrap and API 401s share one rotation, including StrictMode remounts.
export function refreshSession(): Promise<boolean> {
  const revision = session.getRevision();
  if (refreshInFlight?.revision === revision) return refreshInFlight.promise;
  const promise: Promise<boolean> = withSessionLock(() => refresh(revision)).finally(() => {
    if (refreshInFlight?.promise === promise) refreshInFlight = null;
  });
  refreshInFlight = { revision, promise };
  return promise;
}

async function fetchAuthenticated(path: string, init: RequestInit = {}, retry = true, timeoutMs = 15000): Promise<Response> {
  const headers = new Headers(init.headers);
  headers.set('Content-Type', 'application/json');
  const token = session.getAccessToken();
  const revision = session.getRevision();
  if (token) headers.set('Authorization', `Bearer ${token}`);
  const response = await fetchWithTimeout(`${API_BASE_URL}${path}`, { ...init, headers }, timeoutMs);
  if (revision !== session.getRevision()) throw new Error('Сессия изменилась. Повторите запрос.');
  const authenticationChallenge = response.headers.has('www-authenticate');
  if (response.status === 401 && authenticationChallenge && retry) {
    const refreshed = token !== session.getAccessToken() || await refreshSession();
    if (refreshed) {
      if (revision !== session.getRevision()) throw new Error('Сессия изменилась. Повторите запрос.');
      return fetchAuthenticated(path, init, false, timeoutMs);
    }
  }
  if (!response.ok) await parseResponse(response);
  return response;
}

export async function apiFetch<T>(path: string, init: RequestInit = {}, timeoutMs?: number): Promise<T> {
  const revision = session.getRevision();
  const response = await fetchAuthenticated(path, init, true, timeoutMs);
  const result = await parseResponse<T>(response);
  if (revision !== session.getRevision()) throw new Error('Сессия изменилась. Повторите запрос.');
  return result;
}

export async function apiFetchBlob(path: string): Promise<Blob> {
  const revision = session.getRevision();
  const response = await fetchAuthenticated(path, {}, true, 60000);
  const blob = await response.blob();
  if (revision !== session.getRevision()) throw new Error('Сессия изменилась. Повторите запрос.');
  return blob;
}
