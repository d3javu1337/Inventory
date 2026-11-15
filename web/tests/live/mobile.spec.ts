import { test, expect } from '@playwright/test';

const inventoryId = process.env.MOBILE_TEST_INVENTORY;
test('mobile web saves a real fixture scan and removes it from remaining', async ({ page, request }) => {
  test.skip(!inventoryId, 'Explicit MOBILE_TEST_INVENTORY clone UUID is required: this test changes one synthetic result.');
  const api = 'http://localhost:5147/api/v1';
  let pair: { accessToken: string; refreshToken: string } | undefined;
  const visibleText = (text: string) => page.getByText(text, { exact: true }).and(page.locator(':visible'));
  try {
    await page.goto('/');
    await page.getByPlaceholder('Логин').fill('mvp.worker2');
    await page.getByPlaceholder('Пароль').fill('Manual-MVP-2026!');
    const loggedIn = page.waitForResponse(response => response.url().endsWith('/auth/mobile/login') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    const response = await loggedIn;
    expect(response.ok()).toBeTruthy();
    pair = await response.json();
    const headers = { Authorization: `Bearer ${pair!.accessToken}` };
    const currentResponse = await request.get(`${api}/mobile/inventories/${inventoryId}?page=1&filter=remaining`, { headers });
    expect(currentResponse.ok()).toBeTruthy();
    const current = await currentResponse.json();
    expect(current.sourceInventoryNumber).toBe('INV-MVP-002');
    expect(current.status).toBe('Active');
    const target = current.items.find((item: { inventoryNumber: string; result: string }) => item.inventoryNumber.startsWith('MVP-') && item.result === 'Pending');
    expect(target, 'A pending synthetic device must exist').toBeTruthy();
    await page.getByText(current.number, { exact: true }).click();
    await expect(visibleText('Нужно найти')).toBeVisible();
    const search = page.getByRole('textbox', { name: 'Поиск ОС', exact: true });
    await search.fill(target.inventoryNumber);
    await expect(visibleText(target.inventoryNumber)).toBeVisible();
    await expect(page.getByText('Обновляю список…', { exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Сканировать QR', exact: true }).click();
    await page.getByPlaceholder('UUIDv7').fill(target.assetId);
    await page.getByRole('button', { name: 'Ввести UUID', exact: true }).click();
    await expect(visibleText(target.inventoryNumber)).toBeVisible();
    await page.getByRole('button', { name: 'Повреждено', exact: true }).click();
    await page.getByRole('button', { name: 'Сохранить результат', exact: true }).click();
    await expect(page.getByText('Опишите повреждение или причину непригодности.', { exact: true })).toBeVisible();
    const comment = 'Browser regression: synthetic casing damage';
    await page.getByRole('textbox', { name: 'Комментарий', exact: true }).fill(comment);
    await page.getByRole('button', { name: 'Сохранить результат', exact: true }).click();
    await expect(visibleText('Нужно найти')).toBeVisible();
    await expect(visibleText(target.inventoryNumber)).toHaveCount(0);
    await search.fill(target.inventoryNumber);
    await expect(visibleText('Ничего не найдено')).toBeVisible();
    await page.getByRole('button', { name: `Найдено · ${current.counts.found + 1}`, exact: true }).click();
    await expect(visibleText(target.inventoryNumber)).toBeVisible();
    await expect(page.getByText(comment, { exact: true })).toBeVisible();
    const afterResponse = await request.get(`${api}/mobile/inventories/${inventoryId}?page=1&filter=found&search=${target.inventoryNumber}`, { headers });
    const after = await afterResponse.json();
    expect(after.counts.total).toBe(current.counts.total);
    expect(after.counts.found).toBe(current.counts.found + 1);
    expect(after.counts.pending).toBe(current.counts.pending - 1);
    expect(after.items[0].condition).toBe('Damaged');
    expect(after.items[0].comment).toBe(comment);
  } finally {
    if (pair) await request.post(`${api}/auth/mobile/logout`, { data: { refreshToken: pair.refreshToken } });
  }
});

test('mobile web UUID search retains found results (read-only)', async ({ page, request }) => {
  test.skip(!inventoryId, 'Explicit MOBILE_TEST_INVENTORY UUID is required.');
  const api = 'http://localhost:5147/api/v1';
  let pair: { accessToken: string; refreshToken: string } | undefined;
  try {
    await page.goto('/');
    await page.getByPlaceholder('Логин').fill('mvp.worker2');
    await page.getByPlaceholder('Пароль').fill('Manual-MVP-2026!');
    const loggedIn = page.waitForResponse(response => response.url().endsWith('/auth/mobile/login') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    pair = await (await loggedIn).json();
    // A read-only regression is also valid after the clone has been completed.
    await page.getByRole('button', { name: 'Все', exact: true }).click();
    const response = await request.get(`${api}/mobile/inventories/${inventoryId}?page=1&filter=found`, { headers: { Authorization: `Bearer ${pair!.accessToken}` } });
    expect(response.ok()).toBeTruthy();
    const current = await response.json();
    expect(current.sourceInventoryNumber).toBe('INV-MVP-002');
    const target = current.items.find((item: { inventoryNumber: string }) => item.inventoryNumber.startsWith('MVP-'));
    expect(target).toBeTruthy();
    await page.getByText(current.number, { exact: true }).click();
    await page.getByRole('button', { name: `Найдено · ${current.counts.found}`, exact: true }).click();
    await page.getByRole('textbox', { name: 'Поиск ОС', exact: true }).fill(target.assetId.toUpperCase());
    await expect(page.getByText('Обновляю список…', { exact: true })).toHaveCount(0);
    await expect(page.getByText(target.inventoryNumber, { exact: true }).and(page.locator(':visible'))).toBeVisible();
    await expect(page.getByText('Ничего не найдено', { exact: true })).toHaveCount(0);
  } finally {
    if (pair) await request.post(`${api}/auth/mobile/logout`, { data: { refreshToken: pair.refreshToken } });
  }
});
