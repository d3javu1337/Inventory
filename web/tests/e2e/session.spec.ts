import { expect, test } from '@playwright/test';

test('web: две вкладки последовательно обновляют общую refresh-cookie', async ({ context }) => {
  let active = 0;
  let maximum = 0;
  let calls = 0;
  await context.route('**/api/v1/**', async route => {
    if (new URL(route.request().url()).pathname.endsWith('/auth/web/refresh')) {
      calls++; active++; maximum = Math.max(maximum, active);
      await new Promise(resolve => setTimeout(resolve, 500));
      active--;
      return route.fulfill({ json: { accessToken: 'test', user: { id: 'admin', login: 'admin', role: 'Administrator' } } });
    }
    return route.fulfill({ json: {} });
  });
  const first = await context.newPage();
  const second = await context.newPage();
  await Promise.all([first.goto('/dashboard'), second.goto('/dashboard')]);
  await expect(first.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible();
  await expect(second.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible();
  expect(calls).toBe(2); expect(maximum).toBe(1);
});

test('web: вход под другим администратором закрывает старую вкладку', async ({ context }) => {
  let account = 'first-admin';
  await context.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh'))
      return route.fulfill({ json: { accessToken: account, user: { id: account, login: account, role: 'Administrator' } } });
    return route.fulfill({ json: {} });
  });
  const first = await context.newPage();
  await first.goto('/dashboard');
  await expect(first.getByText('first-admin', { exact: true })).toBeVisible();
  account = 'second-admin';
  const second = await context.newPage();
  await second.goto('/dashboard');
  await expect(second.getByText('second-admin', { exact: true })).toBeVisible();
  await expect(first.getByLabel('Логин')).toBeVisible();
  await expect(first.getByRole('alert')).toContainText('В другой вкладке выполнен вход');
  await expect(second.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible();
});

test('web: зависший refresh освобождает форму входа и блокировку cookie', async ({ page }) => {
  await page.addInitScript(() => {
    const timeout = AbortSignal.timeout.bind(AbortSignal);
    let first = true;
    AbortSignal.timeout = (milliseconds) => {
      if (first && milliseconds === 15_000) { first = false; return timeout(250); }
      return timeout(milliseconds);
    };
  });
  let releaseRefresh: (() => void) | undefined;
  let refreshes = 0;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) {
      refreshes++;
      await new Promise<void>(resolve => { releaseRefresh = resolve; });
      return route.abort().catch(() => undefined);
    }
    if (path.endsWith('/auth/web/login/start')) return route.fulfill({ json: { passwordSetupRequired: false } });
    if (path.endsWith('/auth/web/login')) return route.fulfill({ json: { accessToken: 'test', user: { id: 'admin', login: 'admin', role: 'Administrator' } } });
    return route.fulfill({ json: {} });
  });
  try {
    await page.goto('/dashboard');
    await expect(page.getByLabel('Логин')).toBeVisible();
    await page.getByLabel('Логин').fill('admin');
    await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
    await page.getByLabel('Пароль').fill('password-123');
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible();
    expect(refreshes).toBe(1);
  } finally {
    releaseRefresh?.();
  }
});

test('web: инвентаризатор не зацикливает вход в админку и может выбрать другой логин', async ({ page }) => {
  let logout = 0;
  let dashboards = 0;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) return route.fulfill({ status: 401 });
    if (path.endsWith('/auth/web/login/start')) return route.fulfill({ json: { passwordSetupRequired: false } });
    if (path.endsWith('/auth/web/login')) {
      const login = route.request().postDataJSON().login;
      return route.fulfill({ json: { accessToken: login, user: { id: login, login, role: login === 'admin' ? 'Administrator' : 'InventoryEmployee' } } });
    }
    if (path.endsWith('/auth/web/logout')) { logout++; return route.fulfill({ status: 204 }); }
    if (path.endsWith('/dashboard')) { dashboards++; return route.fulfill({ json: {} }); }
    return route.fulfill({ json: [] });
  });
  await page.goto('/login');
  await page.getByLabel('Логин').fill('worker');
  await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
  await page.getByLabel('Пароль').fill('password-123');
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Административная панель доступна только администратору');
  await expect.poll(() => logout).toBe(1);
  expect(dashboards).toBe(0);
  await page.getByRole('button', { name: 'Другой логин' }).click();
  await page.getByLabel('Логин').fill('admin');
  await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
  await page.getByLabel('Пароль').fill('password-123');
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page).toHaveURL(/\/dashboard$/);
  await expect(page.getByRole('heading', { name: 'Обзор', exact: true })).toBeVisible();
});

test('web: выход при недоступном API сохраняется после reload, старый ответ не попадает новому аккаунту', async ({ page }) => {
  let refreshes = 0;
  let releaseOld!: () => void;
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) { refreshes++; return route.fulfill({ json: { accessToken: 'old', user: { id: 'old', login: 'old', role: 'Administrator' } } }); }
    if (path.endsWith('/auth/web/logout')) return route.fulfill({ status: 503, json: { title: 'offline' } });
    if (path.endsWith('/auth/web/login/start')) return route.fulfill({ json: { passwordSetupRequired: false } });
    if (path.endsWith('/auth/web/login')) return route.fulfill({ json: { accessToken: 'new', user: { id: 'new', login: 'new', role: 'Administrator' } } });
    if (path.endsWith('/dashboard')) {
      if (route.request().headers().authorization === 'Bearer old') {
        await new Promise<void>(resolve => { releaseOld = resolve; });
        return route.fulfill({ json: { activeAssets: 111 } });
      }
      return route.fulfill({ json: { activeAssets: 222 } });
    }
    return route.fulfill({ json: [] });
  });
  await page.goto('/dashboard');
  await expect.poll(() => typeof releaseOld).toBe('function');
  await page.getByRole('button', { name: 'Выйти', exact: true }).click();
  await expect(page.getByLabel('Логин')).toBeVisible();
  releaseOld();
  await page.reload();
  await expect(page.getByLabel('Логин')).toBeVisible();
  expect(refreshes).toBe(1);
  await page.getByLabel('Логин').fill('new');
  await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
  await page.getByLabel('Пароль').fill('password-123');
  await page.getByRole('button', { name: 'Войти', exact: true }).click();
  await expect(page.getByText('222', { exact: true })).toBeVisible();
  await expect(page.getByText('111', { exact: true })).toHaveCount(0);
});

test('web: форма входа открывается до ответа logout и новая cookie переживает задержку', async ({ page }) => {
  let releaseLogout!: () => void;
  const pendingLogout = new Promise<void>(resolve => { releaseLogout = resolve; });
  let account: string | null = 'old-admin';
  const order: string[] = [];
  const tokens = (login: string) => ({ accessToken: login, user: { id: login, login, role: 'Administrator' } });
  await page.route('**/api/v1/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/web/refresh')) return account ? route.fulfill({ json: tokens(account) }) : route.fulfill({ status: 401 });
    if (path.endsWith('/auth/web/logout')) {
      order.push('logout request');
      await pendingLogout;
      account = null;
      order.push('logout response');
      return route.fulfill({ status: 204 });
    }
    if (path.endsWith('/auth/web/login/start')) return route.fulfill({ json: { passwordSetupRequired: false } });
    if (path.endsWith('/auth/web/login')) {
      order.push('login request');
      const login: string = route.request().postDataJSON().login;
      account = login;
      return route.fulfill({ json: tokens(login) });
    }
    return route.fulfill({ json: {} });
  });
  try {
    await page.goto('/dashboard');
    await page.getByRole('button', { name: 'Выйти', exact: true }).click();
    await expect.poll(() => order).toContain('logout request');
    await expect(page.getByLabel('Логин')).toBeVisible({ timeout: 2000 });
    await page.getByLabel('Логин').fill('new-admin');
    await page.getByRole('button', { name: 'Продолжить', exact: true }).click();
    await page.getByLabel('Пароль').fill('password-123');
    await page.getByRole('button', { name: 'Войти', exact: true }).click();
    await expect(page.getByRole('progressbar')).toBeVisible();
    releaseLogout();
    await expect(page.getByText('new-admin', { exact: true })).toBeVisible();
    expect(order).toEqual(['logout request', 'logout response', 'login request']);
    await page.reload();
    await expect(page.getByText('new-admin', { exact: true })).toBeVisible();
  } finally { releaseLogout(); }
});
