import { test, expect, Page, APIRequestContext } from '@playwright/test';

const inventoryId = process.env.MOBILE_TEST_INVENTORY;
const api = 'http://localhost:5147/api/v1';
const password = 'Manual-MVP-2026!';
type Pair = { accessToken: string; refreshToken: string };
const visible = (page: Page, text: string) => page.getByText(text, { exact: true }).and(page.locator(':visible'));
async function workerLogin(page: Page) {
  await page.goto('/');
  await page.getByPlaceholder('Логин').fill('mvp.worker2');
  await page.getByPlaceholder('Пароль').fill(password);
  const response = page.waitForResponse(r => r.url().endsWith('/auth/mobile/login'));
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  return await (await response).json() as Pair;
}
async function read(request: APIRequestContext, pair: Pair, filter = 'remaining') {
  const response = await request.get(`${api}/mobile/inventories/${inventoryId}?page=1&pageSize=20&filter=${filter}`, { headers: { Authorization: `Bearer ${pair.accessToken}` } });
  expect(response.ok()).toBeTruthy();
  const detail = await response.json();
  expect(detail.sourceInventoryNumber).toBe('INV-MVP-002');
  return detail;
}

test('API unavailable shows a retryable list error and server status filters recover', async ({ page, request }) => {
  let pair: Pair | undefined;
  try {
    await page.route('**/api/v1/mobile/inventories?**', route => route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Temporarily unavailable', status: 503 }) }));
    pair = await workerLogin(page);
    await expect(page.getByText('Не удалось загрузить проверки.', { exact: true })).toBeVisible({ timeout: 20000 });
    await page.unroute('**/api/v1/mobile/inventories?**');
    await page.getByText('Повторить', { exact: true }).click();
    await expect(page.getByText('Назначенные проверки', { exact: true })).toBeVisible();
    const completed = page.waitForResponse(r => r.url().includes('/mobile/inventories?') && r.url().includes('status=Completed'));
    await page.getByRole('button', { name: 'Завершённые', exact: true }).click();
    expect((await completed).ok()).toBeTruthy();
    await expect(page.getByText('INV-MVP-002', { exact: true })).toBeVisible();
    const all = page.waitForResponse(r => r.url().includes('/mobile/inventories?') && !r.url().includes('status='));
    await page.getByRole('button', { name: 'Все', exact: true }).click();
    expect((await all).ok()).toBeTruthy();
    await expect(page.getByText('Назначенные проверки', { exact: true })).toBeVisible();
  } finally {
    if (pair) await request.post(`${api}/auth/mobile/logout`, { data: { refreshToken: pair.refreshToken } });
  }
});

test('network failure preserves comment, authenticated expiry refreshes and retry saves once', async ({ page, request }) => {
  test.skip(!inventoryId, 'Explicit synthetic clone UUID required; changes one pending fixture asset.');
  let pair: Pair | undefined;
  try {
    pair = await workerLogin(page);
    const before = await read(request, pair);
    expect(before.status).toBe('Active');
    const target = before.items.find((i: { inventoryNumber: string }) => i.inventoryNumber.startsWith('MVP-'));
    expect(target).toBeTruthy();
    await page.getByText(before.number, { exact: true }).click();
    await page.getByRole('button', { name: 'Сканировать QR', exact: true }).click();
    await page.getByPlaceholder('UUIDv7').fill(target.assetId);
    await page.getByRole('button', { name: 'Ввести UUID', exact: true }).click();
    await expect(visible(page, target.inventoryNumber)).toBeVisible();
    await page.getByRole('button', { name: 'Повреждено', exact: true }).click();
    const comment = 'Network regression: retained fixture comment';
    await page.getByRole('textbox', { name: 'Комментарий', exact: true }).fill(comment);
    const scanUrl = `**/mobile/inventories/${inventoryId}/scan`;
    await page.route(scanUrl, route => route.abort('failed'), { times: 1 });
    await page.getByRole('button', { name: 'Сохранить результат', exact: true }).click();
    await expect(page.getByText('Не удалось связаться с сервером. Проверьте соединение и повторите попытку.', { exact: true })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Комментарий', exact: true })).toHaveValue(comment);
    const unchanged = await read(request, pair);
    expect(unchanged.counts).toEqual(before.counts);
    await page.route(scanUrl, route => route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' }, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Expired authentication', status: 401 }) }), { times: 1 });
    const refreshed = page.waitForResponse(r => r.url().endsWith('/auth/mobile/refresh'));
    await page.getByRole('button', { name: 'Сохранить результат', exact: true }).click();
    const refresh = await refreshed;
    expect(refresh.ok()).toBeTruthy();
    pair = await refresh.json();
    await expect(visible(page, 'Нужно найти')).toBeVisible();
    const saved = await read(request, pair!, 'found');
    expect(saved.counts.found).toBe(before.counts.found + 1);
    expect(saved.counts.pending).toBe(before.counts.pending - 1);
    expect(saved.items.find((i: { assetId: string }) => i.assetId === target.assetId)?.comment).toBe(comment);
  } finally {
    if (pair) await request.post(`${api}/auth/mobile/logout`, { data: { refreshToken: pair.refreshToken } });
  }
});

test('rejected expired refresh clears the UI session and cached inventory list', async ({ page, request }) => {
  let pair: Pair | undefined;
  try {
    pair = await workerLogin(page);
    await page.getByRole('button', { name: 'Все', exact: true }).click();
    await expect(page.getByText('INV-MVP-002', { exact: true })).toBeVisible();
    await page.route('**/api/v1/mobile/inventories?**', route => route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' }, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Expired session', status: 401 }) }), { times: 1 });
    await page.route('**/auth/mobile/refresh', route => route.fulfill({ status: 401, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Refresh rejected', status: 401 }) }), { times: 1 });
    await page.getByRole('button', { name: 'Обновить', exact: true }).click();
    await expect(page.getByPlaceholder('Логин')).toBeVisible();
    await expect(page.getByText('INV-MVP-002', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Назначенные проверки', { exact: true })).toHaveCount(0);
  } finally {
    if (pair) await request.post(`${api}/auth/mobile/logout`, { data: { refreshToken: pair.refreshToken } });
  }
});

test('full fixture lifecycle rejects, restarts, rescans, confirms and downloads immutable reports', async ({ page, request, browser }) => {
  test.skip(!inventoryId || !process.env.MOBILE_TEST_ADMIN_PASSWORD, 'Explicit clone and administrator password required; completes synthetic clone.');
  test.setTimeout(120000);
  let pair: Pair | undefined;
  const adminContext = await browser.newContext();
  const admin = await adminContext.newPage();
  let adminToken: string | undefined;
  try {
    pair = await workerLogin(page);
    const before = await read(request, pair);
    expect(before.status).toBe('Active');
    const originalSnapshot = await read(request, pair, 'all');
    const sourceId = before.sourceInventoryId;
    await admin.goto('http://localhost:5173/login');
    await admin.getByLabel('Логин').fill('admin');
    await admin.getByRole('button', { name: 'Продолжить', exact: true }).click();
    await admin.getByLabel('Пароль').fill(process.env.MOBILE_TEST_ADMIN_PASSWORD!);
    const adminResponse = admin.waitForResponse(r => r.url().endsWith('/auth/web/login'));
    await admin.getByRole('button', { name: 'Войти', exact: true }).click();
    const login = await adminResponse;
    expect(login.ok()).toBeTruthy();
    adminToken = (await login.json()).accessToken;
    const headers = { Authorization: `Bearer ${adminToken}` };
    const sourceBefore = await request.get(`${api}/inventories/${sourceId}`, { headers });
    const source = await sourceBefore.json();
    expect(source.number).toBe('INV-MVP-002');
    expect(source.status).toBe('Completed');
    const originals = await Promise.all(['pdf', 'xlsx'].map(async format => {
      const response = await request.get(`${api}/inventories/${sourceId}/reports/inv-1.${format}`, { headers });
      expect(response.ok()).toBeTruthy();
      return response.body();
    }));
    await page.getByText(before.number, { exact: true }).click();
    const finish = async () => {
      await page.getByRole('button', { name: 'Завершить обход', exact: true }).click();
      await page.getByRole('button', { name: 'Завершить и проверить итоги', exact: true }).click();
      await page.getByRole('button', { name: 'Проверить и подтвердить итог', exact: true }).click();
    };
    await finish();
    await page.getByRole('textbox', { name: 'Пароль', exact: true }).fill(password);
    await page.getByRole('button', { name: 'Отклонить результаты', exact: true }).click();
    await expect(page.getByText('Укажите причину отклонения.', { exact: true })).toBeVisible();
    await page.getByRole('textbox', { name: 'Причина отклонения', exact: true }).fill('Browser lifecycle: repeat synthetic inspection');
    await page.getByRole('button', { name: 'Отклонить результаты', exact: true }).click();
    await expect(visible(page, 'Требует повторной проверки')).toBeVisible();
    expect((await read(request, pair)).status).toBe('ReviewRequired');
    await admin.goto(`http://localhost:5173/inventories/${inventoryId}`);
    await admin.getByRole('button', { name: 'Запустить', exact: true }).click();
    await admin.getByRole('dialog').getByRole('button', { name: 'Подтвердить', exact: true }).click();
    await expect(admin.getByRole('dialog')).toHaveCount(0);
    const restarted = await read(request, pair);
    expect(restarted.status).toBe('Active');
    expect(restarted.counts.found).toBe(0);
    expect(restarted.counts.pending).toBe(before.counts.total);
    expect(restarted.items.map((i: { id: string }) => i.id)).toEqual(originalSnapshot.items.map((i: { id: string }) => i.id));
    await page.getByRole('button', { name: 'Обновить', exact: true }).click();
    await page.getByRole('button', { name: 'Сканировать QR', exact: true }).click();
    const target = restarted.items.find((i: { inventoryNumber: string }) => i.inventoryNumber.startsWith('MVP-'));
    await page.getByPlaceholder('UUIDv7').fill(target.assetId);
    await page.getByRole('button', { name: 'Ввести UUID', exact: true }).click();
    await expect(visible(page, target.inventoryNumber)).toBeVisible();
    await page.getByRole('button', { name: 'Сохранить результат', exact: true }).click();
    await expect(visible(page, 'Нужно найти')).toBeVisible();
    await finish();
    await page.getByRole('textbox', { name: 'Пароль', exact: true }).fill('Incorrect-fixture-password!');
    await page.getByRole('button', { name: 'Подтвердить результаты', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    expect((await read(request, pair)).status).toBe('AwaitingConfirmation');
    await page.getByRole('textbox', { name: 'Пароль', exact: true }).fill(password);
    await page.getByRole('button', { name: 'Подтвердить результаты', exact: true }).click();
    await expect(visible(page, 'Завершена')).toBeVisible();
    const completed = await read(request, pair);
    expect(completed.status).toBe('Completed');
    expect(completed.counts.found).toBe(1);
    expect(completed.counts.notFound).toBe(before.counts.total - 1);
    expect(completed.counts.pending).toBe(0);
    await admin.getByRole('button', { name: 'Обновить', exact: true }).click();
    for (const format of ['pdf', 'xlsx']) {
      const download = admin.waitForEvent('download');
      await admin.getByRole('button', { name: format.toUpperCase(), exact: true }).click();
      expect(await (await download).failure()).toBeNull();
      const first = await request.get(`${api}/inventories/${inventoryId}/reports/inv-1.${format}`, { headers });
      const second = await request.get(`${api}/inventories/${inventoryId}/reports/inv-1.${format}`, { headers });
      expect(first.ok() && second.ok()).toBeTruthy();
      const bytes = await first.body();
      expect(bytes.length).toBeGreaterThan(1000);
      expect(await second.body()).toEqual(bytes);
      const unchanged = await request.get(`${api}/inventories/${sourceId}/reports/inv-1.${format}`, { headers });
      expect(await unchanged.body()).toEqual(originals[format === 'pdf' ? 0 : 1]);
    }
    const sourceAfter = await request.get(`${api}/inventories/${sourceId}`, { headers });
    expect((await sourceAfter.json()).counts).toEqual(source.counts);
  } finally {
    if (pair) await request.post(`${api}/auth/mobile/logout`, { data: { refreshToken: pair.refreshToken } });
    if (adminToken) await adminContext.request.post('http://localhost:5173/api/v1/auth/web/logout', { headers: { Authorization: `Bearer ${adminToken}` } });
    await adminContext.close();
  }
});
