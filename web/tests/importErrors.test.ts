import { expect, it } from 'vitest';
import { importErrorsCsv } from '../src/features/assets/importErrors';

it('exports only error rows, preserves CSV quoting and neutralizes formulas', () => {
  const text = importErrorsCsv({ importId: 'test', totalRows: 2, validRows: 1, invalidRows: 1, appliedRows: 0, rows: [
    { rowNumber: 2, inventoryNumber: '=SUM(A1)', isValid: false, error: 'Ошибка; "кавычки"' },
    { rowNumber: 3, inventoryNumber: 'VALID', isValid: true, error: null },
  ] });
  expect(text).toContain(`"'=SUM(A1)"`);
  expect(text).toContain('"Ошибка; ""кавычки"""');
  expect(text).not.toContain('VALID');
});

it('neutralizes formula prefixes after whitespace and common full-width variants', () => {
  const text = importErrorsCsv({ importId: 'test', totalRows: 2, validRows: 0, invalidRows: 2, appliedRows: 0, rows: [
    { rowNumber: 2, inventoryNumber: '\t=SUM(A1)', isValid: false },
    { rowNumber: 3, inventoryNumber: '＋SUM(A1)', isValid: false },
  ] });
  expect(text).toContain(`"'\t=SUM(A1)"`);
  expect(text).toContain(`"'＋SUM(A1)"`);
});
