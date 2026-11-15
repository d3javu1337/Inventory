import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';

test('mobile web: вкладки последовательно обновляют сессию и скрывают данные прежнего аккаунта', async ({ context }) => {
  let account = 'first-worker';
  let refreshToken = 'legacy-refresh';
  let rotations = 0;
  let concurrent = 0;
  let maxConcurrent = 0;
  const tokens = () => ({ accessToken: account, refreshToken, user: { id: account, login: account, role: 'InventoryEmployee' } });
  await context.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/mobile/refresh')) {
      concurrent++;
      maxConcurrent = Math.max(maxConcurrent, concurrent);
      try {
        await new Promise(resolve => setTimeout(resolve, 150));
        if (route.request().postDataJSON().refreshToken !== refreshToken) return await route.fulfill({ status: 401 });
        refreshToken = 'refresh-' + ++rotations;
        return await route.fulfill({ json: tokens() });
      } finally { concurrent--; }
    }
    const items = [{ id: account, number: account, status: 'Active', reason: 'MolChange', total: 1, pending: 1, found: 0, notFound: 0 }];
    return route.fulfill({ json: { items, page: 1, pageSize: 20, totalCount: items.length } });
  });

  const first = await context.newPage();
  await first.goto('/');
  await expect(first.getByPlaceholder('Логин', { exact: true })).toBeVisible();
  await first.evaluate(() => localStorage.setItem('asset-inventory-mobile.refresh-token', 'legacy-refresh'));
  const second = await context.newPage();
  await Promise.all([first.reload(), second.goto('/')]);
  await expect(first.getByText('first-worker', { exact: true })).toBeVisible();
  await expect(second.getByText('first-worker', { exact: true })).toBeVisible();
  expect(rotations).toBe(2);
  expect(maxConcurrent).toBe(1);

  account = 'second-worker';
  refreshToken = 'other-refresh';
  await second.evaluate(() => localStorage.setItem('asset-inventory-mobile.refresh-token',
    JSON.stringify({ username: 'second-worker', password: 'other-refresh' })));
  await second.reload();
  await expect(first.getByPlaceholder('Логин', { exact: true })).toBeVisible();
  await expect(first.getByText('first-worker', { exact: true })).toHaveCount(0);
  await expect(second.getByText('second-worker', { exact: true })).toBeVisible();
});

test('mobile: новая событийная проверка появляется в открытом списке', async ({ page }) => {
  const user = { id: 'worker', login: 'worker', role: 'InventoryEmployee', employeeId: null };
  const inventory = { id: '01992e83-1234-7000-8000-123456789abc', number: 'INV-EVENT-NOW', status: 'Active', type: 'EventDriven', reason: 'MolChange', total: 1, found: 0, notFound: 0, pending: 1 };
  let eventProcessed = false;
  await page.clock.install();
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/auth/mobile/login')) return route.fulfill({ json: { accessToken: 'access', refreshToken: 'refresh', user } });
    if (url.pathname === '/api/v1/mobile/inventories' && url.searchParams.get('status') === 'Active') {
      const items = eventProcessed ? [inventory] : [];
      return route.fulfill({ json: { page: 1, pageSize: 20, totalCount: items.length, items } });
    }
    return route.fulfill({ status: 404 });
  });
  await page.goto('/');
  await page.getByPlaceholder('Логин', { exact: true }).fill('worker');
  await page.getByPlaceholder('Пароль', { exact: true }).fill('test-password');
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page.getByText('В этой категории проверок нет.', { exact: false })).toBeVisible();
  eventProcessed = true;
  await page.clock.runFor(15_100);
  await expect(page.getByText('INV-EVENT-NOW', { exact: true })).toBeVisible();
});

test('mobile: сохранение PDF/XLSX, ошибка, refresh после 401 и точные байты', async ({ page }) => {
  const id = '01992e83-1234-7000-8000-123456789abc';
  const user = { id: 'worker', login: 'worker', role: 'InventoryEmployee', employeeId: null };
  const tokens = (accessToken: string) => ({ accessToken, refreshToken: 'refresh', user });
  const inventory = { id, number: 'INV-REPORT-1', status: 'Completed', type: 'EventDriven', reason: 'MolChange', total: 1, found: 1, notFound: 0, pending: 0 };
  const pdf = Buffer.from('%PDF-1.4\nimmutable-test-bytes\n%%EOF');
  const xlsx = Buffer.from([0x50, 0x4b, 0x03, 0x04, 1, 2, 3, 4]);
  let pdfRequests = 0;
  let refreshes = 0;
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/auth/mobile/login')) return route.fulfill({ json: tokens('old-access') });
    if (url.pathname.endsWith('/auth/mobile/refresh')) { refreshes++; return route.fulfill({ json: tokens('new-access') }); }
    if (url.pathname === '/api/v1/mobile/inventories') {
      const items = !url.searchParams.get('status') || url.searchParams.get('status') === 'Completed' ? [inventory] : [];
      return route.fulfill({ json: { page: 1, pageSize: 20, totalCount: items.length, items } });
    }
    if (url.pathname === `/api/v1/mobile/inventories/${id}`) return route.fulfill({ json: { ...inventory, items: [], unexpectedScans: [], commission: [{ id: 'member', userId: 'worker', login: 'worker', role: 'Chairperson', confirmationStatus: 'Confirmed' }], counts: { total: 1, found: 1, pending: 0, notFound: 0, damaged: 0, unexpected: 0 } } });
    if (url.pathname.endsWith('/reports/inv-1.pdf')) {
      pdfRequests++;
      if (pdfRequests === 1) return route.fulfill({ status: 503, json: { title: 'Временная ошибка отчёта' } });
      if (pdfRequests === 2) return route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' } });
      expect(route.request().headers().authorization).toBe('Bearer new-access');
      return route.fulfill({ contentType: 'application/pdf', body: pdf });
    }
    if (url.pathname.endsWith('/reports/inv-1.xlsx')) return route.fulfill({ contentType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet', body: xlsx });
    return route.fulfill({ status: 404, json: { title: 'Не найдено' } });
  });
  await page.goto('/');
  await page.getByPlaceholder('Логин', { exact: true }).fill('worker');
  await page.getByPlaceholder('Пароль', { exact: true }).fill('test-password');
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await page.getByText('Завершённые', { exact: true }).click();
  await page.getByText('INV-REPORT-1', { exact: true }).click();
  await page.getByRole('button', { name: 'Сохранить PDF', exact: true }).click();
  await expect(page.getByText('Временная ошибка отчёта', { exact: true })).toBeVisible();
  const pdfDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Сохранить PDF', exact: true }).click();
  const downloadedPdf = await pdfDownload;
  expect(downloadedPdf.suggestedFilename()).toBe('INV-1-INV-REPORT-1.pdf');
  expect(await readFile((await downloadedPdf.path())!)).toEqual(pdf);
  expect(refreshes).toBe(1);
  const xlsxDownload = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Сохранить XLSX', exact: true }).click();
  const downloadedXlsx = await xlsxDownload;
  expect(downloadedXlsx.suggestedFilename()).toBe('INV-1-INV-REPORT-1.xlsx');
  expect(await readFile((await downloadedXlsx.path())!)).toEqual(xlsx);
});
