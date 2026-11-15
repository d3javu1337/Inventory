import { expect, test } from '@playwright/test';

test('повтор кадрового события: подтверждение, ошибка, очередь и причина пропуска', async ({ page }) => {
  let retries = 0;
  let status = 'Failed';
  const failedId = '01992e83-1234-7000-8000-123456789abc';
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) return route.fulfill({ json: { accessToken: 'test', user: { id: failedId, login: 'admin', role: 'Administrator' } } });
    if (path.endsWith(`/${failedId}/retry`)) {
      expect(route.request().method()).toBe('POST');
      retries++;
      if (retries === 1) return route.fulfill({ status: 503, json: { title: 'Временная ошибка сервера' } });
      status = 'Accepted';
      return route.fulfill({ status: 202, json: { id: failedId, status, attempts: 0 } });
    }
    if (path.endsWith('/inventory-events/paged')) return route.fulfill({ json: {
      page: 1, pageSize: 20, totalCount: 2, items: [
        { id: failedId, eventId: 'hr-failed', type: 'EmployeeDismissed', status, attempts: status === 'Failed' ? 5 : 0, lastError: status === 'Failed' ? 'Ошибка соединения' : null, receivedAt: '2026-11-05T10:00:00Z' },
        { id: 'other', eventId: 'hr-without-assets', type: 'MolChanged', status: 'Processed', attempts: 1, resultMessage: 'Инвентаризация не создана: за МОЛ нет активных ОС.', receivedAt: '2026-11-05T09:00:00Z' },
      ],
    } });
    return route.fulfill({ json: [] });
  });
  await page.goto('/integration-events');
  await expect(page.getByText('Инвентаризация не создана: за МОЛ нет активных ОС.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Повторить обработку', exact: true })).toHaveCount(1);
  await page.getByRole('button', { name: 'Повторить обработку', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('hr-failed');
  await page.getByRole('button', { name: 'Отмена', exact: true }).click();
  expect(retries).toBe(0);
  await page.getByRole('button', { name: 'Повторить обработку', exact: true }).click();
  await page.getByRole('button', { name: 'Подтвердить', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('Временная ошибка сервера');
  await page.getByRole('button', { name: 'Подтвердить', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByText('Принято в обработку', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Повторить обработку', exact: true })).toHaveCount(0);
  expect(retries).toBe(2);
});
