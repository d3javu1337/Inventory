import { test, expect } from '@playwright/test';

// Opt-in acceptance against the running local stack. No business writes.
test('real admin pages, asset pagination, QR download and import validation', async ({ page }) => {
  const password = process.env.MOBILE_TEST_ADMIN_PASSWORD;
  test.skip(!password, 'Explicit local admin password is required.');
  test.setTimeout(120000);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const failures: string[] = [];
  page.on('pageerror', error => failures.push(error.message));
  try {
    await page.goto('http://localhost:5173/login');
    await page.getByLabel('Логин').fill('admin');
    await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
    await page.getByLabel('Пароль').fill(password!);
    const login = page.waitForResponse(r => r.url().endsWith('/auth/web/login') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    const loginResponse = await login;
    expect(loginResponse.ok()).toBeTruthy();
    const routes = [
      ['dashboard', 'Обзор'], ['catalog/organization', 'Организация'],
      ['catalog/departments', 'Подразделения'], ['catalog/locations', 'Помещения'],
      ['catalog/employees', 'Сотрудники'], ['catalog/users', 'Пользователи'],
      ['inventories', 'Инвентаризации'], ['schedules', 'Расписания'],
      ['integration-events', 'Кадровые события'], ['audit', 'Аудит'],
    ];
    for (const [route, title] of routes) {
      await test.step(title, async () => {
        await page.goto(`http://localhost:5173/${route}`);
        await expect(page.getByRole('heading', { name: title, exact: true })).toBeVisible();
        await expect(page.getByRole('progressbar')).toHaveCount(0);
        await expect(page.locator('.MuiAlert-colorError')).toHaveCount(0);
      });
    }
    await page.goto('http://localhost:5173/assets');
    await expect(page.getByRole('heading', { name: 'Основные средства', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Go to page 21', exact: true }).click();
    await expect(page.locator('tbody tr')).toHaveCount(1);
    await page.getByLabel('Поиск', { exact: true }).fill('MVP-0001');
    await expect(page.locator('tbody tr')).toHaveCount(1);
    await expect(page.locator('tbody')).toContainText('MVP-0001');
    await page.locator('tbody').getByRole('link', { name: 'Открыть', exact: true }).click();
    await expect(page.getByRole('heading', { name: /MVP-0001/ })).toBeVisible();
    const qrEvent = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Скачать QR', exact: true }).click();
    const qr = await qrEvent;
    expect(await qr.failure()).toBeNull();
    const stream = await qr.createReadStream();
    const chunks: Buffer[] = [];
    for await (const chunk of stream!) chunks.push(Buffer.from(chunk));
    const png = Buffer.concat(chunks);
    expect(png.length).toBeGreaterThan(1000);
    expect(png.subarray(0, 8).toString('hex')).toBe('89504e470d0a1a0a');
    await page.getByRole('link', { name: 'Изменить', exact: true }).click();
    await expect(page.getByLabel('Инвентарный номер')).toHaveValue('MVP-0001');
    // Inspect edit only: do not change the historical fixture asset.
    await page.goto('http://localhost:5173/assets/new');
    await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
    await expect(page.locator('.Mui-error')).not.toHaveCount(0);
    await page.goto('http://localhost:5173/assets/import');
    await page.locator('input[type="file"]').setInputFiles({ name: 'invalid.txt', mimeType: 'text/plain', buffer: Buffer.from('test') });
    await expect(page.getByRole('alert')).toContainText('Поддерживаются только CSV и XLSX');
    await expect(page.getByRole('button', { name: 'Проверить файл', exact: true })).toBeDisabled();
    await page.locator('input[type="file"]').setInputFiles({ name: 'empty.csv', mimeType: 'text/csv', buffer: Buffer.alloc(0) });
    await expect(page.getByRole('alert')).toContainText('Файл пуст');
    await expect(page.getByRole('button', { name: 'Проверить файл', exact: true })).toBeDisabled();
    expect(failures).toEqual([]);
  } finally {
    // Web refresh is an HttpOnly cookie, not a token in JSON.
    const logout = await page.context().request.post('http://localhost:5173/api/v1/auth/web/logout', { headers: { Origin: 'http://localhost:5173' } });
    expect(logout.status()).toBe(204);
  }
});

test('admin restarts an explicitly selected rejected native test clone', async ({ page }) => {
  const password = process.env.MOBILE_TEST_ADMIN_PASSWORD;
  const id = process.env.NATIVE_TEST_RESTART_INVENTORY;
  test.skip(!password || !id, 'Explicit native test clone UUID and admin password required; this changes the clone.');
  const sourceId = '01a10624-3a80-7ce2-82f6-2b643bc0c9cc';
  expect(id).not.toBe(sourceId);
  await page.setViewportSize({ width: 1440, height: 1000 });
  try {
    await page.goto('http://localhost:5173/login');
    await page.getByLabel('Логин').fill('admin');
    await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
    await page.getByLabel('Пароль').fill(password!);
    const loginResponse = page.waitForResponse(r => r.url().endsWith('/auth/web/login') && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    const headers = { Authorization: `Bearer ${(await (await loginResponse).json()).accessToken}` };
    await expect(page).toHaveURL(/dashboard/);
    const api = 'http://localhost:5173/api/v1';
    const response = await page.request.get(`${api}/inventories/${id}`, { headers });
    expect(response.ok()).toBeTruthy();
    const before = await response.json();
    expect(before.sourceInventoryNumber).toBe('INV-MVP-002');
    // A previous interrupted assertion may already have restarted this same clone.
    expect(['ReviewRequired', 'Active']).toContain(before.status);
    const sourceFiles = await Promise.all(['pdf', 'xlsx'].map(async f => {
      const file = await page.request.get(`${api}/inventories/${sourceId}/reports/inv-1.${f}`, { headers });
      expect(file.ok()).toBeTruthy();
      return file.body();
    }));
    await page.goto(`http://localhost:5173/inventories/${id}`);
    if (before.status === 'ReviewRequired') {
      await page.getByRole('button', { name: 'Запустить', exact: true }).click();
      await page.getByRole('dialog').getByRole('button', { name: 'Подтвердить', exact: true }).click();
      await expect(page.getByRole('dialog')).toHaveCount(0);
    }
    const after = await (await page.request.get(`${api}/inventories/${id}?page=1&pageSize=20`, { headers })).json();
    expect(after.status).toBe('Active');
    expect(after.counts.found).toBe(0);
    expect(after.counts.pending).toBe(200);
    for (const [index, format] of ['pdf', 'xlsx'].entries()) {
      const bytes = await (await page.request.get(`${api}/inventories/${sourceId}/reports/inv-1.${format}`, { headers })).body();
      expect(bytes.equals(sourceFiles[index])).toBeTruthy();
    }
  } finally {
    const logout = await page.request.post('http://localhost:5173/api/v1/auth/web/logout', { headers: { Origin: 'http://localhost:5173' } });
    expect(logout.status()).toBe(204);
  }
});
