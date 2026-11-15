import { apiFetchBlob } from '../src/api/client';
import { session } from '../src/auth/session';
import { fetchWithTimeout } from '../src/api/request';
import { TokenResponse } from '../src/types/api';

let mockStoredToken: string | null = null;
jest.mock('../src/storage/secureSession', () => ({
  getRefreshCredentials: async () => mockStoredToken ? { token: mockStoredToken, userId: 'worker' } : null,
  saveRefreshToken: async (token: string) => { mockStoredToken = token; },
  clearRefreshToken: async () => { mockStoredToken = null; },
}));
jest.mock('../src/api/request', () => ({ fetchWithTimeout: jest.fn() }));
const tokens = (accessToken: string): TokenResponse => ({ accessToken, refreshToken: 'refresh', accessTokenExpiresAt: '', refreshTokenExpiresAt: '', user: { id: 'worker', login: 'worker', role: 'InventoryEmployee', employeeId: null } });
const reply = (status: number, body: unknown, challenge = false): Response => ({ status, ok: status < 400, headers: { has: () => challenge }, json: async () => body, blob: async () => body }) as unknown as Response;
beforeEach(async () => { await session.clear(); await session.setTokens(tokens('old-access')); jest.clearAllMocks(); });
afterEach(async () => { await session.clear(); });

it('refreshes only after a challenge-401 and retries the original file once', async () => {
  const blob = {} as Blob;
  jest.mocked(fetchWithTimeout).mockResolvedValueOnce(reply(401, null, true)).mockResolvedValueOnce(reply(200, tokens('new-access'))).mockResolvedValueOnce(reply(200, blob));
  expect(await apiFetchBlob('/inventories/own/reports/inv-1.pdf')).toBe(blob);
  expect(fetchWithTimeout).toHaveBeenCalledTimes(3);
  const calls = jest.mocked(fetchWithTimeout).mock.calls;
  expect(calls[0][0]).toContain('/inventories/own/reports/inv-1.pdf');
  expect(calls[1][0]).toContain('/auth/mobile/refresh');
  expect(calls[2][0]).toBe(calls[0][0]);
  expect((calls[2][1]?.headers as Headers).get('Authorization')).toBe('Bearer new-access');
});
it('reports a missing or inaccessible report without attempting to save error JSON', async () => {
  jest.mocked(fetchWithTimeout).mockResolvedValue(reply(404, { title: 'Инвентаризация не найдена.' }));
  await expect(apiFetchBlob('/inventories/other/reports/inv-1.pdf')).rejects.toThrow('Инвентаризация не найдена.');
  expect(fetchWithTimeout).toHaveBeenCalledTimes(1);
});
it('does not loop when the repeated request is still unauthorized', async () => {
  jest.mocked(fetchWithTimeout).mockResolvedValueOnce(reply(401, {}, true)).mockResolvedValueOnce(reply(200, tokens('new-access'))).mockResolvedValueOnce(reply(401, { title: 'Сессия отозвана.' }, true));
  await expect(apiFetchBlob('/inventories/own/reports/inv-1.pdf')).rejects.toThrow('Сессия отозвана.');
  expect(fetchWithTimeout).toHaveBeenCalledTimes(3);
});
it('discards bytes if the account changes while the file body is read', async () => {
  let finish!: (blob: Blob) => void;
  const response = reply(200, null);
  response.blob = () => new Promise(done => { finish = done; });
  jest.mocked(fetchWithTimeout).mockResolvedValue(response);
  const downloading = apiFetchBlob('/inventories/own/reports/inv-1.pdf');
  while (!finish) await Promise.resolve();
  await session.clear();
  finish({} as Blob);
  await expect(downloading).rejects.toThrow('Сессия изменилась.');
});
