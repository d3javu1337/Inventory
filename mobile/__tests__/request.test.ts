import { fetchWithTimeout } from '../src/api/request';
import { mobileApi } from '../src/api/mobileApi';

describe('network request timeout', () => {
  const originalFetch = globalThis.fetch;
  afterEach(() => { globalThis.fetch = originalFetch; jest.useRealTimers(); });
  it('returns successful responses and clears its timeout', async () => {
    jest.useFakeTimers();
    const response = { ok: true } as Response;
    globalThis.fetch = jest.fn().mockResolvedValue(response);
    expect(await fetchWithTimeout('http://test')).toBe(response);
    expect(jest.getTimerCount()).toBe(0);
  });
  it('aborts a hanging request and shows a retryable Russian error', async () => {
    jest.useFakeTimers();
    globalThis.fetch = jest.fn((_url: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new Error('aborted')));
    }));
    const result = fetchWithTimeout('http://test', {}, 100);
    const assertion = result.then(() => { throw new Error('Expected timeout'); }, error => expect(error.message).toContain('Сервер не отвечает'));
    await jest.advanceTimersByTimeAsync(100);
    await assertion;
    expect(jest.getTimerCount()).toBe(0);
  });
  it('propagates caller cancellation without labeling it as a timeout', async () => {
    globalThis.fetch = jest.fn((_url: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new Error('caller aborted')));
    }));
    const controller = new AbortController();
    const result = fetchWithTimeout('http://test', { signal: controller.signal });
    controller.abort();
    await expect(result).rejects.toThrow('caller aborted');
  });
  it('shows a retryable Russian message on connection loss', async () => {
    globalThis.fetch = jest.fn().mockRejectedValue(new TypeError('Failed to fetch'));
    await expect(fetchWithTimeout('http://test')).rejects.toThrow('Не удалось связаться с сервером');
  });

  it('lets report generation during confirmation exceed the usual request timeout', async () => {
    jest.useFakeTimers();
    globalThis.fetch = jest.fn((_url: RequestInfo | URL, init?: RequestInit) => new Promise<Response>((resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new Error('aborted')));
      setTimeout(() => resolve({ ok: true, status: 204, headers: new Headers() } as Response), 20_000);
    }));
    await Promise.all([
      expect(mobileApi.confirm('check', 'password', 0)).resolves.toBeUndefined(),
      jest.advanceTimersByTimeAsync(20_000),
    ]);
    expect(globalThis.fetch).toHaveBeenCalledWith(expect.stringContaining('/confirm?includeDetails=false&expectedRound=0'), expect.objectContaining({ method: 'POST' }));
    expect(jest.getTimerCount()).toBe(0);
  });
});
