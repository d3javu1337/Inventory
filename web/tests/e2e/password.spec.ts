import { expect, test } from '@playwright/test';

test('смена пароля: валидация, текущий пароль, реактивный refresh и новая сессия', async ({ page }) => {
  let refreshes = 0;
  let changes = 0;
  let checkedNewToken = false;
  const user = { id: '01992e83-1234-7000-8000-123456789abc', login: 'owner', role: 'Administrator' };
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) { refreshes++; return route.fulfill({ json: { accessToken: 'refreshed-access', user } }); }
    if (path.endsWith('/auth/web/change-password')) {
      changes++;
      if (changes === 1) return route.fulfill({ status: 401, json: { title: 'Неверный текущий пароль.' } });
      if (changes === 2) return route.fulfill({ status: 401, headers: { 'WWW-Authenticate': 'Bearer' } });
      expect(route.request().headers().authorization).toBe('Bearer refreshed-access');
      expect(route.request().postDataJSON()).toEqual({ currentPassword: 'correct-password', newPassword: 'new-password' });
      return route.fulfill({ json: { accessToken: 'changed-access', user } });
    }
    if (path.endsWith('/inventory-events/paged')) {
      checkedNewToken = route.request().headers().authorization === 'Bearer changed-access';
      return route.fulfill({ json: { items: [], totalCount: 0, page: 1, pageSize: 20 } });
    }
    return route.fulfill({ json: {} });
  });
  await page.goto('/dashboard');
  await page.getByRole('button', { name: 'Сменить пароль', exact: true }).click();
  await page.getByRole('button', { name: 'Изменить пароль', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Введите текущий пароль.');
  await page.getByLabel('Текущий пароль', { exact: true }).fill('old-password');
  await page.getByLabel('Новый пароль', { exact: true }).fill('new-password');
  await page.getByLabel('Повторите новый пароль', { exact: true }).fill('other-password');
  await page.getByRole('button', { name: 'Изменить пароль', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Пароли не совпадают.');
  expect(changes).toBe(0);
  await page.getByLabel('Повторите новый пароль', { exact: true }).fill('new-password');
  await page.getByRole('button', { name: 'Изменить пароль', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Неверный текущий пароль.');
  expect(refreshes).toBe(1);
  await expect(page.getByLabel('Новый пароль', { exact: true })).toHaveValue('new-password');
  await page.getByLabel('Текущий пароль', { exact: true }).fill('correct-password');
  await page.getByRole('button', { name: 'Изменить пароль', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Пароль изменён.');
  expect(refreshes).toBe(2);
  expect(changes).toBe(3);
  await page.getByRole('button', { name: 'Закрыть', exact: true }).click();
  await page.getByRole('link', { name: 'События', exact: true }).click();
  await expect.poll(() => checkedNewToken).toBe(true);
});
