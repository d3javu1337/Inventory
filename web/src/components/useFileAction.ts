import { useRef, useState } from 'react';
import { download, openFile } from '../api/httpClient';

export function useFileAction() {
  const busy = useRef(false);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const run = async (path: string, print = false) => {
    if (busy.current) return;
    busy.current = true; setPending(true); setError('');
    try { if (print) await openFile(path, true); else await download(path); }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Не удалось получить файл. Повторите попытку.'); }
    finally { busy.current = false; setPending(false); }
  };
  return { run, error, pending };
}
