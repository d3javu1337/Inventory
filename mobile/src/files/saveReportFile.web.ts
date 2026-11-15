export async function saveReportFile(blob: Blob, fileName: string, _mimeType: string, stillCurrent: () => boolean): Promise<boolean> {
  if (!stillCurrent()) throw new Error('Сессия изменилась. Повторите запрос.');
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url; anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click(); anchor.remove();
  } finally { window.setTimeout(() => URL.revokeObjectURL(url), 60000); }
  return true;
}
