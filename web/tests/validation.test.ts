import { describe, expect, it } from 'vitest';
import { assetSchema, dateSchema, dateTimeSchema, dateTimeToIso, toLocalDateTimeInput, organizationSchema, scheduleSchema, inventorySchema, userSchema } from '../src/shared/validation';
import { formatMoney } from '../src/shared/formatters';

describe('validation boundaries', () => {
  it('requires an employee for an inventory account and rejects blank passwords', () => {
    const account = { login: 'admin', password: 'password-123', confirm: 'password-123', role: 'Administrator', employeeId: '' };
    expect(userSchema.safeParse(account).success).toBe(true);
    expect(userSchema.safeParse({ ...account, role: 'InventoryEmployee' }).success).toBe(false);
    expect(userSchema.safeParse({ ...account, role: 'InventoryEmployee', employeeId: '01992e83-1234-7000-8000-123456789abc' }).success).toBe(true);
    expect(userSchema.safeParse({ ...account, password: '        ', confirm: '        ' }).success).toBe(false);
  });
  it('rejects text, rollover dates and year zero but accepts leap days', () => {
    for (const date of ['hello', '', '2026-02-29', '2026-02-31', '2026-04-31', '0000-01-01']) expect(dateSchema.safeParse(date).success, date).toBe(false);
    expect(dateSchema.safeParse('2024-02-29').success).toBe(true);
  });
  it('requires a complete calendar date and valid time', () => {
    for (const value of ['2026-02-31T10:00', '2026-10-03T24:00', '2026-10-03T10:60', '2026-10-03', 'abc']) expect(dateTimeSchema.safeParse(value).success, value).toBe(false);
    expect(dateTimeSchema.safeParse('2026-10-03T10:30').success).toBe(true);
  });
  it('preserves Yekaterinburg time when opening and saving a schedule', () => {
    const iso = '2026-10-03T05:30:00.000Z';
    expect(toLocalDateTimeInput(iso)).toBe('2026-10-03T10:30');
    expect(dateTimeToIso(toLocalDateTimeInput(iso))).toBe(iso);
  });
  it('validates the full cost range without rounding to a JavaScript number', () => {
    const id = '01992e83-1234-7000-8000-123456789abc';
    const asset = { inventoryNumber: 'A-1', name: 'Стол', accountingAccount: '01', acceptedAt: '2026-10-03', initialCost: '0', departmentId: id, locationId: id, materiallyResponsibleEmployeeId: id };
    expect(assetSchema.safeParse(asset).success).toBe(true);
    for (const cost of ['', 'Infinity', '-1', '1.001', '1e4', '100000000000000000']) expect(assetSchema.safeParse({ ...asset, initialCost: cost }).success).toBe(false);
    expect(assetSchema.parse({ ...asset, initialCost: ' 00010,29 ' }).initialCost).toBe('10.29');
    expect(assetSchema.parse({ ...asset, initialCost: '99999999999999999.99' }).initialCost).toBe('99999999999999999.99');
    expect(formatMoney('99999999999999999.99').replace(/\s/g, '')).toBe('99999999999999999,99₽');
    expect(assetSchema.safeParse({ ...asset, manufactureYear: 2020.5 }).success).toBe(false);
  });
  it('requires integer intervals and valid organization identifiers', () => {
    const schedule = { name: 'Проверка', intervalMonths: 12, nextRunAt: '2026-10-03T10:30', launchMode: 'CreateDraft' };
    for (const intervalMonths of [0, -1, 1.5, NaN, 1201]) expect(scheduleSchema.safeParse({ ...schedule, intervalMonths }).success).toBe(false);
    expect(scheduleSchema.safeParse(schedule).success).toBe(true);
    expect(organizationSchema.safeParse({ name: 'ООО', inn: '1234567890', kpp: '', okpo: null }).success).toBe(true);
    expect(organizationSchema.safeParse({ name: 'ООО', inn: 'abcdefghij' }).success).toBe(false);
  });
  it('requires an explanation for Other and a MOL for an event inventory', () => {
    const inventory = { type: 'EventDriven', reason: 'Other', reasonDetails: ' ', targetEmployeeId: '', plannedAt: '2026-10-03T10:30' };
    const result = inventorySchema.safeParse(inventory);
    expect(result.success).toBe(false);
    if (!result.success) expect(result.error.issues.map(i => i.path[0])).toEqual(expect.arrayContaining(['reasonDetails', 'targetEmployeeId']));
  });
});
