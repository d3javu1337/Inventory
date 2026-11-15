import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';

const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==', 'base64');

test('файлы: ошибка видна, печатное окно закрывается при сбое, скачивание повторяет 401', async ({ page }, testInfo) => {
  const id = '01992e83-1234-7000-8000-123456789abc';
  let refreshes = 0;
  let failed = false;
  let requireFreshToken = false;
  const contents = Buffer.from('test-qr-file-bytes');
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) return route.fulfill({ json: { accessToken: ++refreshes === 1 ? 'old' : 'new', user: { id, login: 'admin', role: 'Administrator' } } });
    if (path.endsWith(`/assets/${id}/qr`)) {
      if (failed) return route.fulfill({ status: 503, json: { title: 'QR временно недоступен' } });
      if (!requireFreshToken) return route.fulfill({ body: png, contentType: 'image/png' });
      if (route.request().headers().authorization === 'Bearer old') return route.fulfill({ status: 401 });
      return route.fulfill({ body: contents, headers: { 'Content-Type': 'image/png', 'Content-Disposition': "attachment; filename*=UTF-8''%D0%AD%D1%82%D0%B8%D0%BA%D0%B5%D1%82%D0%BA%D0%B0.png" } });
    }
    if (path.endsWith(`/assets/${id}`)) return route.fulfill({ json: { id, inventoryNumber: 'QR-TEST', name: 'Тест', status: 'InUse', initialCost: 100, acceptedAt: '2026-01-01' } });
    if (path.endsWith('/history')) return route.fulfill({ json: { items: [], page: 1, pageSize: 50, totalCount: 0 } });
    return route.fulfill({ json: [] });
  });
  await page.goto(`/assets/${id}`);
  await expect(page.getByRole('img', { name: 'QR-код ОС QR-TEST' })).toHaveJSProperty('naturalWidth', 1);
  failed = true;
  const popupReady = page.waitForEvent('popup');
  await page.getByRole('button', { name: 'Печать', exact: true }).click();
  const popup = await popupReady;
  await expect(page.getByRole('alert')).toHaveText('QR временно недоступен');
  await expect.poll(() => popup.isClosed()).toBe(true);
  failed = false;
  requireFreshToken = true;
  const downloading = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Скачать QR', exact: true }).click();
  const download = await downloading;
  expect(download.suggestedFilename()).toBe('Этикетка.png');
  const output = testInfo.outputPath('qr.png');
  await download.saveAs(output);
  expect(await readFile(output)).toEqual(contents);
  expect(refreshes).toBe(2);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('QR-этикетка в карточке и редактировании восстанавливается после сбоя и 401', async ({ page }) => {
  const id = '01992e83-1234-7000-8000-123456789abc';
  let refreshes = 0;
  let requests = 0;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) return route.fulfill({ json: {
      accessToken: ++refreshes === 1 ? 'old' : 'new', user: { id, login: 'admin', role: 'Administrator' },
    } });
    if (path.endsWith(`/assets/${id}/qr`)) {
      if (++requests === 1) return route.fulfill({ status: 503 });
      if (route.request().headers().authorization === 'Bearer old') return route.fulfill({ status: 401 });
      return route.fulfill({ body: png, contentType: 'image/png' });
    }
    if (path.endsWith(`/assets/${id}`)) return route.fulfill({ json: {
      id, inventoryNumber: 'QR-TEST', name: 'Тест', status: 'InUse', initialCost: 100,
      acceptedAt: '2026-01-01', accountingAccount: '01', departmentId: '', locationId: '', materiallyResponsibleEmployeeId: '', version: 1,
    } });
    if (path.endsWith('/history')) return route.fulfill({ json: { items: [], page: 1, pageSize: 50, totalCount: 0 } });
    return route.fulfill({ json: [] });
  });
  await page.goto(`/assets/${id}`);
  await expect(page.getByRole('heading', { name: /QR-TEST/ })).toBeVisible();
  const label = page.getByRole('region', { name: 'QR-этикетка' });
  const image = label.getByRole('img', { name: 'QR-код ОС QR-TEST' });
  await expect(label.getByRole('alert')).toContainText('Не удалось загрузить QR-код');
  await label.getByRole('button', { name: 'Повторить' }).click();
  await expect(image).toHaveJSProperty('naturalWidth', 1);
  expect(refreshes).toBe(2);
  expect(requests).toBe(3);
  await page.getByRole('link', { name: 'Изменить', exact: true }).click();
  await expect(page.getByLabel('Инвентарный номер')).toHaveValue('QR-TEST');
  await expect(image).toHaveJSProperty('naturalWidth', 1);
  expect(requests).toBe(3);
});
