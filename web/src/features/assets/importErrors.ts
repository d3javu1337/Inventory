import type { ImportResult } from '../../api/types';

// Quote separators and neutralize spreadsheet formula prefixes independently.
const cell = (value: string) => `"${(/^(?:[\t\r\n]|\s*[=+@＝＋＠－-])/u.test(value) ? "'" : '') + value.replaceAll('"', '""')}"`;
export const importErrorsCsv = (result: ImportResult) => '\ufeff' + ['Строка;Инвентарный номер;Ошибка',
  ...result.rows.filter(row => !row.isValid).map(row => [String(row.rowNumber), row.inventoryNumber, row.error ?? ''].map(cell).join(';')),
].join('\r\n');

export function downloadImportErrors(result: ImportResult) {
  const url = URL.createObjectURL(new Blob([importErrorsCsv(result)], { type: 'text/csv;charset=utf-8' }));
  const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'import-errors.csv'; anchor.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
