export async function fetchWithTimeout(url: string, init: RequestInit = {}, timeoutMs = 15000): Promise<Response> {
  const controller = new AbortController();
  let timedOut = false;
  const abort = () => controller.abort();
  if (init.signal?.aborted) controller.abort();
  else init.signal?.addEventListener('abort', abort);
  const timer = setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);
  try { return await fetch(url, { ...init, signal: controller.signal }); }
  catch (error) {
    if (timedOut) throw new Error('Сервер не отвечает. Проверьте соединение и повторите попытку.');
    if (!init.signal?.aborted) throw new Error('Не удалось связаться с сервером. Проверьте соединение и повторите попытку.');
    throw error;
  } finally {
    clearTimeout(timer);
    init.signal?.removeEventListener('abort', abort);
  }
}
