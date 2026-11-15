import { session } from '../src/auth/session';
import { apiFetch, refreshSession } from '../src/api/client';
import { queryClient } from '../src/query/QueryProvider';
import { fetchWithTimeout } from '../src/api/request';
import { TokenResponse } from '../src/types/api';

let mockStoredToken: string | null = null;
let mockStoredUserId: string | null = null;
let mockSaveFailure = false;
let mockSaveBarrier: Promise<void> | null = null;
let mockSaveStarted = false;
jest.mock('../src/storage/secureSession', () => ({
  getRefreshCredentials: async () => mockStoredToken ? { token: mockStoredToken, userId: mockStoredUserId } : null,
  saveRefreshToken: async (token: string, userId: string) => {
    mockSaveStarted = true;
    await mockSaveBarrier;
    if (mockSaveFailure) throw new Error('Keystore недоступен');
    mockStoredToken = token;
    mockStoredUserId = userId;
  },
  clearRefreshToken: async () => { mockStoredToken = null; mockStoredUserId = null; },
}));
jest.mock('../src/api/request', () => ({ fetchWithTimeout: jest.fn() }));
const tokens = (id: string): TokenResponse => ({ accessToken: `access-${id}`, refreshToken: `refresh-${id}`, accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user: { id, login: id, role: 'InventoryEmployee', employeeId: null } });
const response = (status: number, body: unknown) => ({
  status, ok: status < 400,
  headers: new Headers(status === 401 ? { 'www-authenticate': 'Bearer' } : {}),
  json: async () => body,
}) as Response;

beforeEach(async () => {
  mockSaveFailure = false; mockSaveBarrier = null; mockSaveStarted = false;
  await session.clear();
  jest.mocked(fetchWithTimeout).mockReset();
});
afterEach(async () => { await session.clear(); });

it('clears list AND detail cache on logout and account changes', async () => {
  await session.setTokens(tokens('first'));
  queryClient.setQueryData(['mobile-inventories'], ['secret-first']);
  queryClient.setQueryData(['mobile-inventory', 'id'], { name: 'secret-first' });
  await session.clear();
  expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  queryClient.setQueryData(['mobile-inventories'], ['stale']);
  await session.setTokens(tokens('second'));
  expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
});

it('does not publish a new account when secure storage fails', async () => {
  await session.setTokens(tokens('old'));
  queryClient.setQueryData(['mobile-inventories'], ['old']);
  mockSaveFailure = true;
  await expect(session.setTokens(tokens('new'))).rejects.toThrow('Keystore');
  expect(session.getUser()?.id).toBe('old');
  expect(session.getAccessToken()).toBe('access-old');
  expect(mockStoredToken).toBe('refresh-old');
  expect(queryClient.getQueryData(['mobile-inventories'])).toEqual(['old']);
});

it('cannot republish an account when logout happens during a secure-storage write', async () => {
  let release!: () => void;
  mockSaveBarrier = new Promise(resolve => { release = resolve; });
  const pending = session.setTokens(tokens('new'));
  while (!mockSaveStarted) await Promise.resolve();
  const logout = session.clear();
  release();
  await expect(pending).rejects.toThrow('Сессия изменилась');
  await logout;
  expect(session.getUser()).toBeNull(); expect(session.getAccessToken()).toBeNull(); expect(mockStoredToken).toBeNull();
});

it('does not clear the current account cache on token rotation', async () => {
  await session.setTokens(tokens('same'));
  queryClient.setQueryData(['mobile-inventories'], ['same']);
  await session.setTokens(tokens('same'));
  expect(queryClient.getQueryData(['mobile-inventories'])).toEqual(['same']);
});

it('shares a single refresh between simultaneous bootstrap calls', async () => {
  mockStoredToken = 'old';
  jest.mocked(fetchWithTimeout).mockResolvedValue(response(200, tokens('restored')));
  expect(await Promise.all([refreshSession(), refreshSession()])).toEqual([true, true]);
  expect(fetchWithTimeout).toHaveBeenCalledTimes(1);
  expect(session.getUser()?.id).toBe('restored');
});

it('never repeats a mobile mutation with another account from refresh', async () => {
  await session.setTokens(tokens('first'));
  jest.mocked(fetchWithTimeout).mockResolvedValueOnce(response(401, {}))
    .mockResolvedValueOnce(response(200, tokens('second')))
    .mockResolvedValueOnce(response(204, undefined));
  await expect(apiFetch('/mobile/inventories/test/confirm', { method: 'POST' })).rejects.toMatchObject({ status: 401 });
  expect(fetchWithTimeout).toHaveBeenCalledTimes(2);
  expect(new Headers(jest.mocked(fetchWithTimeout).mock.calls[1][1]?.headers).get('X-Expected-User-Id')).toBe('first');
  expect(session.getUser()).toBeNull();
});

it('leaves the other tab’s stored credentials intact when signing out of an old account', async () => {
  await session.setTokens(tokens('first'));
  mockStoredToken = 'refresh-second';
  mockStoredUserId = 'second';
  expect(await session.signOut()).toBeNull();
  expect(session.getUser()).toBeNull();
  expect(mockStoredToken).toBe('refresh-second');
});

it('keeps another account’s legacy token when the API reports an owner mismatch', async () => {
  await session.setTokens(tokens('first'));
  mockStoredToken = 'legacy-other';
  mockStoredUserId = null;
  jest.mocked(fetchWithTimeout).mockResolvedValue(response(401, { code: 'session_account_mismatch' }));
  expect(await refreshSession()).toBe(false);
  expect(session.getUser()).toBeNull();
  expect(mockStoredToken).toBe('legacy-other');
});

it('cannot restore an old session after logout during a pending refresh', async () => {
  mockStoredToken = 'old';
  let complete!: (value: Response) => void;
  jest.mocked(fetchWithTimeout).mockImplementation(() => new Promise(resolve => { complete = resolve; }));
  const pending = refreshSession();
  while (!complete) await Promise.resolve();
  await session.clear();
  complete(response(200, tokens('old-user')));
  expect(await pending).toBe(false);
  expect(session.getUser()).toBeNull();
  expect(mockStoredToken).toBeNull();
});

it('retains refresh credentials on temporary server failure', async () => {
  mockStoredToken = 'old';
  jest.mocked(fetchWithTimeout).mockResolvedValue(response(503, { detail: 'Temporary failure' }));
  await expect(refreshSession()).rejects.toThrow('Temporary failure');
  expect(mockStoredToken).toBe('old');
});

it('clears rejected refresh credentials', async () => {
  mockStoredToken = 'old';
  jest.mocked(fetchWithTimeout).mockResolvedValue(response(401, {}));
  expect(await refreshSession()).toBe(false);
  expect(mockStoredToken).toBeNull();
});
