export { observeStoredAccount } from '../web/keychain';

export async function withSessionLock<T>(operation: () => Promise<T>) {
  const locks = window.navigator.locks;
  if (!locks) return operation();
  try {
    return await locks.request('asset-inventory.mobile.session', { signal: AbortSignal.timeout(15_000) }, operation);
  } catch (error) {
    if (error instanceof DOMException && error.name === 'TimeoutError')
      throw new Error('Обновление сессии занято в другой вкладке. Повторите попытку.');
    throw error;
  }
}
