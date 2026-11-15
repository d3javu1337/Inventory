import { expect, test } from '@playwright/test';

test('поиск инвентаризаций: основание, серверные страницы и возврат из карточки', async ({ page }) => {
  const inventories = Array.from({ length: 105 }, (_, i) => ({
    id: `01992e83-1234-7000-8000-${String(i + 1).padStart(12, '0')}`,
    number: `INV-TEST-${String(i + 1).padStart(3, '0')}`, type: 'EventDriven', status: 'Active',
    reason: i % 2 === 0 ? 'MolChange' : 'MolDismissal', plannedAt: '2026-11-05T10:00:00Z',
    targetEmployeeName: 'Петрова Анна', assigneeName: 'Иванов Иван', total: 10, found: 2, notFound: 0, pending: 8,
  }));
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/auth/web/refresh')) return route.fulfill({ json: { accessToken: 'test', user: { id: 'admin', login: 'admin', role: 'Administrator' } } });
    if (url.pathname === '/api/v1/inventories') {
      const search = (url.searchParams.get('search') ?? '').toLowerCase();
      const reason = url.searchParams.get('reason');
      const rows = inventories.filter(x => (!reason || x.reason === reason) && `${x.number} ${x.targetEmployeeName} ${x.assigneeName}`.toLowerCase().includes(search));
      const currentPage = Math.max(1, Math.min(Number(url.searchParams.get('page') ?? 1), Math.ceil(rows.length / 50)));
      return route.fulfill({ json: { items: rows.slice((currentPage - 1) * 50, currentPage * 50), page: currentPage, pageSize: 50, totalCount: rows.length } });
    }
    const detail = inventories.find(x => url.pathname.endsWith(`/${x.id}`));
    if (detail) return route.fulfill({ json: { ...detail, commission: [], items: [], unexpectedScans: [], counts: { total: 10, found: 2, pending: 8, notFound: 0, discrepancies: 0, damaged: 0, unexpected: 0 } } });
    return route.fulfill({ json: [] });
  });
  await page.goto('/inventories?page=3');
  await expect(page.getByText('INV-TEST-105', { exact: true })).toBeVisible();
  await page.getByLabel('Поиск инвентаризаций', { exact: true }).fill('Иванов');
  await expect(page).not.toHaveURL(/page=3/);
  await expect(page.getByText('INV-TEST-001', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Поиск инвентаризаций', { exact: true })).toBeFocused();
  await page.getByRole('combobox', { name: 'Основание', exact: true }).click();
  await page.getByRole('option', { name: 'Смена МОЛ', exact: true }).click();
  await expect(page.getByText('Всего: 53', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Go to page 2', exact: true }).click();
  await expect(page.getByText('INV-TEST-101', { exact: true })).toBeVisible();
  const row = page.getByRole('row').filter({ hasText: 'INV-TEST-105' });
  await expect(row).toContainText('Петрова Анна');
  await expect(row).toContainText('Иванов Иван');
  await row.getByRole('link', { name: 'Открыть INV-TEST-105' }).click();
  await expect(page.getByRole('heading', { name: 'Инвентаризация INV-TEST-105', exact: true })).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(/page=2/);
  await expect(page.getByLabel('Поиск инвентаризаций', { exact: true })).toHaveValue('Иванов');
  await expect(page.getByText('INV-TEST-101', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Сбросить', exact: true }).click();
  await expect(page.getByLabel('Поиск инвентаризаций', { exact: true })).toHaveValue('');
  await expect(page.getByText('Всего: 105', { exact: true })).toBeVisible();
  await expect(page).toHaveURL(/\/inventories$/);
});
