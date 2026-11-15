import { expect, test } from '@playwright/test';

test('аудит: история из карточки, страницы, запрос, период и системный пользователь', async ({ page }) => {
  const id = '01992e83-1234-7000-8000-123456789abc';
  const system = '00000000-0000-0000-0000-000000000000';
  const requests: URL[] = [];
  const logs = Array.from({ length: 60 }, (_, i) => ({ id: `audit-${i}`, action: 'asset.updated', entityType: 'Asset', entityId: id, entityDisplayName: 'Тестовое ОС', userId: i % 2 ? id : system, userDisplayName: i % 2 ? 'Иванов Иван' : null, createdAt: '2026-11-05T10:00:00Z', correlationId: `trace-${i + 1}`, dataJson: JSON.stringify({ displayName: 'Тестовое ОС', changes: { Name: { before: 'До', after: 'После' } } }) }));
  await page.route('**/api/v1/**', async route => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (path.endsWith('/auth/web/refresh')) return route.fulfill({ json: { accessToken: 'test', user: { id, login: 'admin', role: 'Administrator' } } });
    if (path === `/api/v1/assets/${id}`) return route.fulfill({ json: { id, inventoryNumber: 'OS-AUDIT', name: 'Тестовое ОС', status: 'InUse', acceptedAt: '2026-01-01', initialCost: 100, version: 1 }, headers: { ETag: '"1"' } });
    if (path.includes('/history')) return route.fulfill({ json: { items: [], totalCount: 0, page: 1, pageSize: 20 } });
    if (path === '/api/v1/users') return route.fulfill({ json: [{ id, login: 'worker', employeeName: 'Иванов Иван', role: 'InventoryEmployee', isBlocked: false }] });
    if (path === '/api/v1/audit') {
      requests.push(url);
      const p = url.searchParams;
      const rows = logs.filter(log => (!p.get('entityId') || p.get('entityId') === log.entityId)
        && (!p.get('entityType') || p.get('entityType') === log.entityType)
        && (!p.get('action') || p.get('action') === log.action)
        && (!p.get('userId') || p.get('userId') === log.userId)
        && `${log.entityDisplayName} ${log.correlationId}`.toLowerCase().includes((p.get('search') ?? '').toLowerCase()));
      const currentPage = Math.max(1, Math.min(Number(p.get('page') ?? 1), Math.ceil(rows.length / 50)));
      return route.fulfill({ json: { items: rows.slice((currentPage - 1) * 50, currentPage * 50), totalCount: rows.length, page: currentPage, pageSize: 50 } });
    }
    return route.fulfill({ json: [] });
  });
  await page.goto(`/assets/${id}`);
  await page.getByRole('link', { name: 'История действий', exact: true }).click();
  await expect(page.getByLabel('ID сущности', { exact: true })).toHaveValue(id);
  await expect(page.getByRole('combobox', { name: 'Тип сущности', exact: true })).toContainText('Основное средство');
  await expect(page.getByText('Найдено действий: 60', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Go to page 2', exact: true }).click();
  await expect(page.getByText('trace-60', { exact: true })).toBeVisible();
  await page.getByLabel('Поиск в аудите', { exact: true }).fill('trace-60');
  await expect(page.getByText('Найдено действий: 1', { exact: true })).toBeVisible();
  await expect(page).not.toHaveURL(/page=2/);
  await page.getByRole('button', { name: 'Показать JSON', exact: true }).click();
  await expect(page.getByText(/"after": "После"/)).toBeVisible();
  await page.getByLabel('Поиск в аудите', { exact: true }).fill('');
  await page.getByRole('combobox', { name: 'Действие', exact: true }).click();
  await page.getByRole('option', { name: 'Изменение ОС', exact: true }).click();
  await page.getByLabel('С даты', { exact: true }).fill('2026-11-06');
  await page.getByLabel('По дату', { exact: true }).fill('2026-11-05');
  await expect(page.getByRole('alert')).toContainText('Начальная дата не может быть позже конечной.');
  expect(requests.some(url => url.searchParams.get('dateFrom') === '2026-11-06' && url.searchParams.get('dateTo') === '2026-11-05')).toBe(false);
  await page.getByLabel('С даты', { exact: true }).fill('2026-11-05');
  await expect(page.getByText('Найдено действий: 60', { exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'Пользователь', exact: true }).click();
  await page.getByRole('option', { name: 'Система', exact: true }).click();
  await expect(page.getByText('Найдено действий: 30', { exact: true })).toBeVisible();
  expect(requests.at(-1)?.searchParams.get('dateFrom')).toBe('2026-11-05');
  expect(requests.at(-1)?.searchParams.get('dateTo')).toBe('2026-11-05');
  expect(requests.at(-1)?.searchParams.get('userId')).toBe(system);
  await page.reload();
  await expect(page.getByLabel('По дату', { exact: true })).toHaveValue('2026-11-05');
  await expect(page.getByText('Найдено действий: 30', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Сбросить', exact: true }).click();
  await expect(page).toHaveURL(/\/audit$/);
  await expect(page.getByLabel('ID сущности', { exact: true })).toHaveValue('');
  await expect(page.getByText('Найдено действий: 60', { exact: true })).toBeVisible();
});
