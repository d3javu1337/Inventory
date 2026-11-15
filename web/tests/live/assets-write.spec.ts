import { test, expect, type Page } from '@playwright/test';
import type { Asset, Department, Location, Employee } from '../../src/api/types';

const root = 'http://localhost:5173';
const api = `${root}/api/v1`;
const csvHeaders = 'inventoryNumber;name;accountingAccount;serialNumber;manufactureYear;acceptedAt;initialCost;departmentCode;locationCode;molPersonnelNumber';
async function choose(page: Page, label: string, text: string) {
  await page.getByLabel(label).click();
  await page.getByRole('option', { name: text, exact: true }).click();
}
async function downloadBytes(page: Page, button: string) {
  const event = page.waitForEvent('download');
  await page.getByRole('button', { name: button, exact: true }).click();
  const download = await event;
  expect(await download.failure()).toBeNull();
  const stream = await download.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
  return Buffer.concat(chunks);
}

test('real asset CRUD, stale edit, CSV preview/errors/apply/upsert and archive', async ({ page }) => {
  const password = process.env.MOBILE_TEST_ADMIN_PASSWORD;
  test.skip(!password || process.env.ADMIN_TEST_ALLOW_WRITES !== '1', 'Explicit admin password and ADMIN_TEST_ALLOW_WRITES=1 required; creates and archives only new QA assets.');
  test.setTimeout(120000);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const prefix = `QA-LIVE-${Date.now()}`;
  const created = new Map<string, string>();
  let headers: Record<string, string> | undefined;
  let initialCount: number | undefined;
  const read = async <T,>(path: string): Promise<T> => {
    const response = await page.request.get(`${api}/${path}`, { headers });
    expect(response.ok(), `GET ${path}`).toBeTruthy();
    return response.json();
  };
  try {
    await page.goto(`${root}/login`);
    await page.getByLabel('Логин').fill('admin');
    await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
    await page.getByLabel('Пароль').fill(password!);
    const login = page.waitForResponse(r => r.url().endsWith('/auth/web/login') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    const response = await login;
    expect(response.ok()).toBeTruthy();
    headers = { Authorization: `Bearer ${(await response.json()).accessToken}` };
    initialCount = (await read<{ totalCount: number }>('assets?page=1&pageSize=1')).totalCount;
    const fixtures = await read<{ items: Asset[] }>('assets?search=MVP-0001&page=1&pageSize=20');
    const fixture = fixtures.items.find(x => x.inventoryNumber === 'MVP-0001');
    expect(fixture).toBeTruthy();
    const departments = await read<Department[]>('departments');
    const locations = await read<Location[]>('locations');
    const employees = await read<Employee[]>('employees');
    const department = departments.find(x => x.id === fixture!.departmentId)!;
    const location = locations.find(x => x.id === fixture!.locationId)!;
    const mol = employees.find(x => x.id === fixture!.materiallyResponsibleEmployeeId)!;
    const number = `${prefix}-FORM`;
    const importedNumber = `${prefix}-CSV`;
    await page.goto(`${root}/assets/new`);
    await page.getByLabel('Инвентарный номер').fill(number);
    await page.getByLabel('Наименование', { exact: false }).fill('UI тест — отдельный ноутбук');
    await page.getByLabel('Бухгалтерский счет').fill('01.01');
    await page.getByLabel('Серийный номер').fill(`${prefix}-SN`);
    await page.getByLabel('Год выпуска').fill('2025');
    await page.getByLabel('Дата принятия').fill('2026-10-04');
    await page.getByLabel('Первоначальная стоимость').fill('12345.67');
    await choose(page, 'Подразделение', `${department.code} — ${department.name}`);
    await choose(page, 'Помещение', `${location.code} — ${location.name}`);
    await choose(page, 'Материально ответственное лицо', mol.fullName);
    const save = page.waitForResponse(r => /\/api\/v1\/assets\/?$/.test(r.url()) && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    const saved = await save;
    expect(saved.status()).toBe(201);
    const asset: Asset = await saved.json();
    created.set(asset.id, number);
    expect(asset.inventoryNumber).toBe(number);
    expect(asset.initialCost).toBe(12345.67);
    expect(asset.acceptedAt).toBe('2026-10-04');
    await expect(page.getByRole('heading', { name: `${number} — UI тест — отдельный ноутбук`, exact: true })).toBeVisible();
    const stale = await page.context().newPage();
    await stale.goto(`${root}/assets/${asset.id}/edit`);
    await expect(stale.getByLabel('Наименование')).toHaveValue(asset.name);
    await page.getByRole('link', { name: 'Изменить', exact: true }).click();
    await page.getByLabel('Наименование').fill('UI тест — изменённый ноутбук');
    const edit = page.waitForResponse(r => r.url().endsWith(`/assets/${asset.id}`) && r.request().method() === 'PUT');
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    expect((await edit).ok()).toBeTruthy();
    await stale.getByLabel('Наименование').fill('UI тест — устаревшая попытка');
    const conflict = stale.waitForResponse(r => r.url().endsWith(`/assets/${asset.id}`) && r.request().method() === 'PUT');
    await stale.getByRole('button', { name: 'Сохранить', exact: true }).click();
    expect((await conflict).status()).toBe(412);
    await expect(stale.getByRole('alert')).toContainText('Карточка была изменена');
    expect((await read<Asset>(`assets/${asset.id}`)).name).toBe('UI тест — изменённый ноутбук');
    await stale.close();

    const importedName = 'UI импорт — "ноутбук"; проверка\nкириллицы';
    const row = (n: string, name: string, cost = '23456.78') =>
      [n, name, '01.01', `${prefix}-SN`, '2025', '2026-10-04', cost, department.code, location.code, mol.personnelNumber]
        .map(value => /[;"\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value).join(';');
    await page.goto(`${root}/assets/import`);
    const template = await downloadBytes(page, 'Скачать шаблон');
    expect(template.toString('utf8').replace(/^\uFEFF/, '').split('\r\n')[0]).toBe(csvHeaders);
    const badRows = Array.from({ length: 100 }, (_, i) => row(`${prefix}-BAD-${i + 1}`, 'Некорректная стоимость', '-1'));
    const badCsv = `${csvHeaders}\r\n${badRows.join('\r\n')}\r\n`;
    await page.locator('input[type="file"]').setInputFiles({ name: 'qa-invalid.csv', mimeType: 'text/csv', buffer: Buffer.from(badCsv) });
    const preview = page.waitForResponse(r => r.url().includes('/assets/imports?apply=false'));
    await page.getByRole('button', { name: 'Проверить файл', exact: true }).click();
    const invalid = await (await preview).json();
    expect(invalid.invalidRows).toBe(100);
    expect(invalid.appliedRows).toBe(0);
    await expect(page.locator('tbody tr')).toHaveCount(50);
    await page.getByRole('button', { name: 'Следующая страница', exact: true }).click();
    await expect(page.locator('tbody')).toContainText(`${prefix}-BAD-100`);
    await expect(page.getByRole('button', { name: 'Применить импорт', exact: true })).toHaveCount(0);
    const errors = (await downloadBytes(page, 'Скачать ошибки CSV')).toString('utf8');
    for (let i = 1; i <= 100; i++) expect(errors).toContain(`${prefix}-BAD-${i}`);
    const csv = `${csvHeaders}\r\n${row(number, importedName)}\r\n${row(importedNumber, 'UI импорт — новое ОС')}\r\n`;
    await page.locator('input[type="file"]').setInputFiles({ name: 'qa-valid.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
    const validPreview = page.waitForResponse(r => r.url().includes('/assets/imports?apply=false'));
    await page.getByRole('button', { name: 'Проверить файл', exact: true }).click();
    const checked = await (await validPreview).json();
    expect(checked.validRows).toBe(2);
    expect(checked.appliedRows).toBe(0);
    expect((await read<Asset>(`assets/${asset.id}`)).name).toBe('UI тест — изменённый ноутбук');
    expect((await read<{ totalCount: number }>(`assets?search=${importedNumber}`)).totalCount).toBe(0);
    const apply = page.waitForResponse(r => r.url().includes('/assets/imports?apply=true'));
    await page.getByRole('button', { name: 'Применить импорт', exact: true }).click();
    const appliedResponse = await apply;
    const imported = await read<{ items: Asset[] }>(`assets?search=${importedNumber}`);
    for (const item of imported.items) {
      expect(item.inventoryNumber).toBe(importedNumber);
      created.set(item.id, importedNumber);
    }
    expect(appliedResponse.ok()).toBeTruthy();
    expect((await appliedResponse.json()).appliedRows).toBe(2);
    expect(imported.items).toHaveLength(1);
    expect((await read<Asset>(`assets/${asset.id}`)).name).toBe(importedName);
    expect((await read<Asset>(`assets/${asset.id}`)).initialCost).toBe(23456.78);
    expect(imported.items[0].initialCost).toBe(23456.78);

    for (const [id, inventoryNumber] of created) {
      await page.goto(`${root}/assets`);
      await page.getByLabel('Поиск', { exact: true }).fill(inventoryNumber);
      await expect(page.locator('tbody tr')).toHaveCount(1);
      await page.locator('tbody').getByRole('button', { name: 'Архивировать', exact: true }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Подтвердить', exact: true }).click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.locator('tbody tr')).toHaveCount(0);
      await page.getByLabel('Показывать архив').check();
      await expect(page.locator('tbody tr')).toHaveCount(1);
      expect((await read<Asset>(`assets/${id}`)).archivedAt).toBeTruthy();
      await page.goto(`${root}/assets/${id}/edit`);
      await expect(page.getByRole('button', { name: 'Сохранить', exact: true })).toBeDisabled();
    }
    await page.goto(`${root}/assets/import`);
    await page.locator('input[type="file"]').setInputFiles({ name: 'qa-archived.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) });
    const archivedPreview = page.waitForResponse(r => r.url().includes('/assets/imports?apply=false'));
    await page.getByRole('button', { name: 'Проверить файл', exact: true }).click();
    const archived = await (await archivedPreview).json();
    expect(archived.invalidRows).toBe(2);
    expect(archived.rows.every((r: { error: string }) => r.error.includes('Архивное ОС нельзя обновить'))).toBeTruthy();
    await expect(page.getByRole('button', { name: 'Применить импорт', exact: true })).toHaveCount(0);
  } finally {
    // Only IDs created in this run may be cleaned up; archive is non-destructive.
    try {
      for (const [id, number] of created) {
        const asset = await read<Asset>(`assets/${id}`);
        expect(asset.inventoryNumber).toBe(number);
        expect(number.startsWith(`${prefix}-`)).toBeTruthy();
        if (!asset.archivedAt) expect((await page.request.delete(`${api}/assets/${id}`, { headers })).status()).toBe(204);
      }
      if (headers && initialCount !== undefined) expect((await read<{ totalCount: number }>('assets?page=1&pageSize=1')).totalCount).toBe(initialCount);
    } finally {
      expect((await page.request.post(`${api}/auth/web/logout`)).status()).toBe(204);
    }
  }
});
