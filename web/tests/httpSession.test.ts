import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

let client: typeof import('../src/api/httpClient');
const mockFetch = vi.fn<typeof fetch>();
const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const tokens = { accessToken: 'new-token', user: { id: 'user', login: 'admin', role: 'Administrator' } };
beforeEach(async () => { vi.resetModules(); mockFetch.mockReset(); vi.stubGlobal('fetch', mockFetch); client = await import('../src/api/httpClient'); client.session.setToken('old-token', 'user'); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('реактивная web-сессия', () => {
  it('reserves the print window before fetching and closes it on a file error', async () => {
    const close = vi.fn();
    const open = vi.fn(() => ({ close, opener: {}, closed: false }));
    vi.stubGlobal('window', { open });
    let finish!: (response: Response) => void;
    mockFetch.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    const printing = client.openFile('/qr', true);
    expect(open).toHaveBeenCalledWith('about:blank', '_blank');
    finish(json(503, { title: 'Файл временно недоступен' }));
    await expect(printing).rejects.toMatchObject({ status: 503 });
    expect(close).toHaveBeenCalledOnce();
  });
  it('explains popup blocking without starting a file request', async () => {
    vi.stubGlobal('window', { open: () => null });
    await expect(client.openFile('/qr', true)).rejects.toThrow('Разрешите всплывающие окна');
    expect(mockFetch).not.toHaveBeenCalled();
  });
  it('uses the fresh token for a late 401 without a second refresh', async () => {
    let finishLate!: (response: Response) => void;
    let refreshes = 0;
    mockFetch.mockImplementation(async (url, init) => {
      if (String(url).endsWith('/refresh')) { refreshes++; return json(200, tokens); }
      const token = new Headers(init?.headers).get('Authorization');
      if (String(url).endsWith('/late') && token === 'Bearer old-token') return new Promise(resolve => { finishLate = resolve; });
      return token === 'Bearer old-token' ? json(401, {}) : json(200, { ok: true });
    });
    const late = client.request('/late');
    expect(await client.request('/first')).toEqual({ ok: true });
    finishLate(json(401, {}));
    expect(await late).toEqual({ ok: true });
    expect(refreshes).toBe(1);
  });
  it('rejects a refresh for another account and never resends the original write', async () => {
    const expired = vi.fn();
    client.session.setExpiredHandler(expired);
    mockFetch.mockImplementation(async url => String(url).endsWith('/refresh')
      ? json(200, { accessToken: 'other-token', user: { id: 'other', login: 'another-admin', role: 'Administrator' } })
      : json(401, {}));

    await expect(client.request('/assets/id', { method: 'PUT', body: '{"name":"Pending edit"}' })).rejects.toMatchObject({ status: 401 });
    expect(mockFetch.mock.calls.map(([url]) => url)).toEqual(['/assets/id', '/api/v1/auth/web/refresh']);
    expect(new Headers(mockFetch.mock.calls[1][1]?.headers).get('X-Expected-User-Id')).toBe('user');
    expect(expired).toHaveBeenCalledOnce();
  });
  it('shares concurrent refresh and does not expire a session on a temporary 503', async () => {
    const expired = vi.fn(); client.session.setExpiredHandler(expired);
    mockFetch.mockImplementation(async url => String(url).endsWith('/refresh') ? json(503, { title: 'Сервис временно недоступен' }) : json(401, {}));
    await expect(client.request('/assets')).rejects.toMatchObject({ status: 503 });
    expect(expired).not.toHaveBeenCalled();
    mockFetch.mockImplementation(async (url, init) => String(url).endsWith('/refresh') ? json(200, tokens) : new Headers(init?.headers).get('Authorization') === 'Bearer new-token' ? json(200, { ok: true }) : json(401, {}));
    expect(await client.request('/assets')).toEqual({ ok: true });
  });
  it('aborts a stalled refresh, releases the cookie lock and allows another 401-driven attempt', async () => {
    const deadline = new AbortController();
    vi.spyOn(AbortSignal, 'timeout').mockReturnValueOnce(deadline.signal);
    const expired = vi.fn();
    client.session.setExpiredHandler(expired);
    let refreshSignal: AbortSignal | null | undefined;
    let release!: (response: Response) => void;
    mockFetch.mockImplementation(async (url, init) => {
      if (!String(url).endsWith('/refresh')) return json(401, {});
      refreshSignal = init?.signal;
      return new Promise((resolve, reject) => {
        release = resolve;
        refreshSignal?.addEventListener('abort', () => reject(refreshSignal?.reason), { once: true });
      });
    });
    const request = client.request('/assets').catch(error => error);
    while (!release) await Promise.resolve();
    deadline.abort(new DOMException('Timed out', 'TimeoutError'));
    if (!refreshSignal?.aborted) release(json(503, {}));
    expect(await request).toHaveProperty('message', expect.stringContaining('Сервер авторизации не отвечает'));
    expect(refreshSignal?.aborted).toBe(true);
    expect(expired).not.toHaveBeenCalled();

    mockFetch.mockImplementation(async (url, init) => {
      if (String(url).endsWith('/refresh')) return json(200, tokens);
      return new Headers(init?.headers).get('Authorization') === 'Bearer new-token' ? json(200, { ok: true }) : json(401, {});
    });
    expect(await client.request('/assets')).toEqual({ ok: true });
  });
  it('ignores a refresh response that belongs to an account which logged out', async () => {
    let finish!: (response: Response) => void;
    mockFetch.mockImplementation(async url => String(url).endsWith('/refresh') ? new Promise(resolve => { finish = resolve; }) : json(401, {}));
    const oldRequest = client.request('/assets');
    while (!finish) await Promise.resolve();
    client.session.setToken(null); client.session.replaceToken('other-token', 'other');
    finish(json(200, tokens));
    await expect(oldRequest).rejects.toThrow('Сессия изменилась');
    mockFetch.mockImplementation(async (_url, init) => json(200, { authorization: new Headers(init?.headers).get('Authorization') }));
    expect(await client.request('/new-account')).toEqual({ authorization: 'Bearer other-token' });
  });
  it('discards delayed JSON including the ETag response path after logout', async () => {
    let finish!: (data: unknown) => void;
    mockFetch.mockResolvedValue({ ok: true, status: 200, json: () => new Promise(resolve => { finish = resolve; }) } as unknown as Response);
    const request = client.requestWithResponse('/assets/id');
    while (!finish) await Promise.resolve();
    client.session.setToken(null);
    finish({ secret: 'old-account' });
    await expect(request).rejects.toThrow('Сессия изменилась');
  });
});
