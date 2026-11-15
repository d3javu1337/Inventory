import type { ApiProblem, WebSessionResponse } from './types';

const baseUrl = import.meta.env.VITE_API_BASE_URL ?? '';
const expectedUserHeader = 'X-Expected-User-Id';
let accessToken: string | null = null;
let sessionUserId: string | null = null;
let refreshFlight: { revision: number; promise: Promise<WebSessionResponse>; controller: AbortController } | null = null;
let sessionRevision = 0;
let onSessionExpired: (() => void) | null = null;
let cookieQueue = Promise.resolve();

async function withAuthDeadline<T>(operation: (signal: AbortSignal) => Promise<T>, requestSignal?: AbortSignal): Promise<T> {
  const timeout = AbortSignal.timeout(15_000);
  const signal = requestSignal ? AbortSignal.any([requestSignal, timeout]) : timeout;
  try {
    return await operation(signal);
  } catch (error) {
    if (signal.aborted && signal.reason?.name === 'TimeoutError')
      throw new Error('Сервер авторизации не отвечает. Повторите попытку.');
    throw error;
  }
}

function withCookieLock<T>(operation: (signal: AbortSignal) => Promise<T>, requestSignal?: AbortSignal): Promise<T> {
  return withAuthDeadline(signal => {
    const run = () => { signal.throwIfAborted(); return operation(signal); };
    // These requests write the same HttpOnly cookie, including across tabs.
    if (typeof navigator !== 'undefined' && navigator.locks)
      return navigator.locks.request('asset-inventory.web.refresh', { signal }, run);
    const pending = cookieQueue.then(run);
    cookieQueue = pending.then(() => undefined, () => undefined);
    return pending;
  }, requestSignal);
}

export class ApiError extends Error {
  constructor(public status: number, public problem: ApiProblem) { super(problem.title || problem.detail || 'Ошибка запроса'); }
}

export const session = {
  setToken(token: string | null, userId: string | null = null) {
    if (token === null) { sessionRevision++; refreshFlight?.controller.abort(); }
    accessToken = token;
    sessionUserId = userId;
  },
  replaceToken(token: string, userId: string) {
    sessionRevision++; refreshFlight?.controller.abort();
    accessToken = token;
    sessionUserId = userId;
  },
  getRevision: () => sessionRevision,
  getUserId: () => sessionUserId,
  setExpiredHandler(handler: (() => void) | null) { onSessionExpired = handler; },
};

function assertSession(revision: number) {
  if (sessionRevision !== revision) throw new Error('Сессия изменилась. Повторите запрос.');
}

async function parseError(response: Response): Promise<ApiProblem> {
  try { return await response.json() as ApiProblem; } catch { return { title: `Ошибка HTTP ${response.status}`, status: response.status }; }
}

async function refresh(): Promise<WebSessionResponse> {
  const revision = sessionRevision;
  if (refreshFlight?.revision !== revision) {
    const controller = new AbortController();
    const expectedUserId = sessionUserId;
    const rotate = async (signal: AbortSignal) => {
      assertSession(revision);
      const headers = expectedUserId ? { [expectedUserHeader]: expectedUserId } : undefined;
      const response = await fetch(`${baseUrl}/api/v1/auth/web/refresh`, { method: 'POST', credentials: 'include', headers, signal });
      assertSession(revision);
      if (!response.ok) throw new ApiError(response.status, await parseError(response));
      const tokens = await response.json() as WebSessionResponse;
      assertSession(revision);
      if (expectedUserId && tokens.user.id !== expectedUserId)
        throw new ApiError(401, { title: 'В браузере открыта сессия другой учетной записи.' });
      accessToken = tokens.accessToken;
      sessionUserId = tokens.user.id;
      return tokens;
    };
    const promise: Promise<WebSessionResponse> = withCookieLock(rotate, controller.signal)
      .finally(() => { if (refreshFlight?.promise === promise) refreshFlight = null; });
    refreshFlight = { revision, promise, controller };
  }
  return refreshFlight.promise;
}

async function fetchWithAuth(path: string, init: RequestInit = {}, retry = true): Promise<Response> {
  const revision = sessionRevision;
  let sentToken = accessToken;
  const send = (signal = init.signal) => {
    assertSession(revision);
    sentToken = accessToken;
    const headers = new Headers(init.headers);
    if (!(init.body instanceof FormData) && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
    if (sentToken) headers.set('Authorization', `Bearer ${sentToken}`);
    return fetch(`${baseUrl}${path}`, { ...init, signal, headers, credentials: 'include' });
  };
  let response: Response;
  if (path.startsWith('/api/v1/auth/web/')) {
    const authRequest = path.endsWith('/login/start') ? withAuthDeadline : withCookieLock;
    response = await authRequest(send, init.signal ?? undefined);
  } else {
    response = await send();
  }
  assertSession(revision);
  const canRefresh = !path.includes('/auth/web/') || (path.endsWith('/change-password') && response.headers.has('WWW-Authenticate'));
  if (response.status === 401 && retry && canRefresh) {
    try {
      if (sentToken === accessToken) await refresh();
      assertSession(revision);
      return fetchWithAuth(path, init, false);
    } catch (error) {
      assertSession(revision);
      if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
        session.setToken(null);
        onSessionExpired?.();
        throw new ApiError(401, { title: 'Сессия истекла' });
      }
      throw error;
    }
  }
  return response;
}

export async function request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
  const revision = sessionRevision;
  const response = await fetchWithAuth(path, init, retry);
  if (!response.ok) throw new ApiError(response.status, await parseError(response));
  if (response.status === 204) return undefined as T;
  const data = await response.json() as T;
  assertSession(revision);
  return data;
}

export async function requestWithResponse<T>(path: string, init: RequestInit = {}): Promise<{ data: T; response: Response }> {
  const revision = sessionRevision;
  const response = await fetchWithAuth(path, init);
  if (!response.ok) throw new ApiError(response.status, await parseError(response));
  const data = await response.json() as T;
  assertSession(revision);
  return { data, response };
}

async function fetchFile(path: string, signal?: AbortSignal): Promise<Response> {
  const response = await fetchWithAuth(path, { signal });
  if (!response.ok) throw new ApiError(response.status, await parseError(response));
  return response;
}

export async function requestBlob(path: string, signal: AbortSignal): Promise<Blob> {
  const revision = sessionRevision;
  const response = await fetchFile(path, signal);
  const blob = await response.blob();
  assertSession(revision);
  return blob;
}

export async function download(path: string): Promise<void> {
  const revision = sessionRevision;
  const response = await fetchFile(path);
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const utf8 = disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1];
  const plain = disposition.match(/filename="?([^";]+)"?/i)?.[1];
  let name = plain ?? 'download';
  if (utf8) { try { name = decodeURIComponent(utf8); } catch { /* Keep the plain filename when an upstream header is malformed. */ } }
  const blob = await response.blob();
  assertSession(revision);
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click();
  URL.revokeObjectURL(url);
}

export async function openFile(path: string, print = false): Promise<void> {
  // Reserve the window during the user gesture, before network/refresh awaits.
  const popup = window.open('about:blank', '_blank');
  if (!popup) throw new Error('Разрешите всплывающие окна для открытия файла и печати.');
  popup.opener = null;
  const revision = sessionRevision;
  try {
    const response = await fetchFile(path);
    const blob = await response.blob();
    assertSession(revision);
    if (popup.closed) return;
    const url = URL.createObjectURL(blob);
    if (print) popup.addEventListener('load', () => { if (!popup.closed) popup.print(); }, { once: true });
    popup.location.replace(url);
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
  } catch (error) { popup.close(); throw error; }
}

export const authApi = {
  changePassword: async (currentPassword: string, newPassword: string) => {
    if (refreshFlight?.revision === sessionRevision) await refreshFlight.promise;
    return request<WebSessionResponse>('/api/v1/auth/web/change-password', { method: 'POST', body: JSON.stringify({ currentPassword, newPassword }) });
  },
  startLogin: (login: string) => request<{ passwordSetupRequired: boolean }>('/api/v1/auth/web/login/start', { method: 'POST', body: JSON.stringify({ login }) }),
  login: (login: string, password: string) => request<WebSessionResponse>('/api/v1/auth/web/login', { method: 'POST', body: JSON.stringify({ login, password, device: navigator.userAgent }) }),
  setInitialPassword: (login: string, password: string) => request<WebSessionResponse>('/api/v1/auth/web/initial-password', { method: 'POST', body: JSON.stringify({ login, password, device: navigator.userAgent }) }),
  refresh,
  logout: (userId: string | null) => request<void>('/api/v1/auth/web/logout', {
    method: 'POST', headers: userId ? { [expectedUserHeader]: userId } : undefined, signal: AbortSignal.timeout(5000),
  }),
};
