export function withSessionLock<T>(operation: () => Promise<T>) {
  return operation();
}

export function observeStoredAccount(_onChange: (userId: string | null) => void) {
  return () => undefined;
}
