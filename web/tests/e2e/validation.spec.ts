import { expect, test, type Page } from '@playwright/test';
import { Buffer } from 'node:buffer';

const id = '01992e83-1234-7000-8000-123456789abc';
const otherId = '01992e83-1234-7000-8000-123456789abd';
const department = { id, code: 'D1', name: 'Отдел', isInventoryDepartment: true, isArchived: false };
const employee = { id, personnelNumber: '001', fullName: 'Иванов Иван', departmentId: id, departmentName: 'Отдел', isMateriallyResponsible: true, isEmployed: true, isArchived: false };
const asset = { id, inventoryNumber: 'OS-1', name: 'Стол', accountingAccount: '01', initialCost: 10.29, acceptedAt: '2026-10-03', departmentId: id, locationId: id, materiallyResponsibleEmployeeId: id, version: 3 };

async function mockApi(page: Page, inventoryOverrides: Record<string, unknown> = {}) {
  const writes: { path: string; body: any; headers: Record<string, string> }[] = [];
  await page.route('**/api/v1/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname.replace('/api/v1', '');
    if (path === '/auth/web/refresh') return route.fulfill({ json: { accessToken: 'test', user: { id, login: 'admin', role: 'Administrator' } } });
    if (request.method() !== 'GET') {
      writes.push({ path, body: request.postData() ? request.postDataJSON() : null, headers: request.headers() });
      return route.fulfill({ status: 400, json: { title: 'Тестовый ответ сервера' } });
    }
    const data: Record<string, unknown> = {
      '/departments': [department], '/employees': [employee],
      '/locations': [{ id, code: 'L1', name: 'Кабинет', departmentId: id, isArchived: false }],
      '/users': [{ id: otherId, login: 'employee', role: 'InventoryEmployee', employeeName: 'Петров Петр', isBlocked: false }], '/inventory-schedules': [], '/inventory-events': [],
      '/organization': { name: 'ООО Пример', inn: '1234567890', kpp: '', okpo: '', address: '', directorName: '' },
      [`/assets/${id}`]: asset,
      [`/inventories/${id}`]: { id, number: 'INV-1', status: 'Draft', type: 'Planned', reason: 'AnnualReporting', plannedAt: '2026-10-03T05:30:00Z', total: 0, items: [], unexpectedScans: [], commission: [{ id, userId: id, login: 'old', employeeName: 'Иванов Иван', role: 'Chairman', confirmationStatus: 'Pending' }], ...inventoryOverrides },
    };
    if (path.endsWith('/paged')) {
      const items = data[path.replace('/paged', '')] as unknown[] ?? [];
      return route.fulfill({ json: { items, page: 1, pageSize: 20, totalCount: items.length } });
    }
    return route.fulfill({ json: data[path] ?? { items: [], total: 0 }, headers: { ETag: '"3"' } });
  });
  return writes;
}

async function choose(page: Page, index: number, option: string) {
  await page.getByRole('combobox').nth(index).click();
  await page.getByRole('option', { name: option, exact: true }).click();
}

test('расписание показывает время следующей попытки после сбоя', async ({ page }) => {
  await mockApi(page);
  await page.route('**/api/v1/inventory-schedules/paged?**', route => route.fulfill({ json: {
    page: 1, pageSize: 20, totalCount: 1,
    items: [{ id, name: 'Годовая проверка', intervalMonths: 12, nextRunAt: '2026-11-07T09:30:00Z',
      launchMode: 'AutoStart', isEnabled: true, dayOfMonth: 31, consecutiveFailures: 3, retryAt: '2026-11-07T10:00:15Z' }],
  } }));
  await page.goto('/schedules');
  const row = page.getByRole('row').filter({ hasText: 'Годовая проверка' });
  await expect(row.getByText('31-го числа', { exact: true })).toBeVisible();
  await expect(row.getByText('Ошибка', { exact: true })).toBeVisible();
  await expect(row.getByText('Неудачных попыток 3', { exact: true })).toBeVisible();
  await expect(row.getByText(/Повтор не раньше .*15:00/)).toBeVisible();
});

for (const endpoint of ['locations', 'employees', 'users']) {
  test(`справочник ${endpoint}: сбой API виден и повтор восстанавливает список`, async ({ page }) => {
    await mockApi(page);
    let fail = true;
    await page.route(`**/api/v1/${endpoint}/paged?**`, route => fail
      ? route.fulfill({ status: 503, json: { title: 'Список временно недоступен' } })
      : route.fulfill({ json: { items: [], totalCount: 0, page: 1, pageSize: 20 } }));
    await page.goto(`/catalog/${endpoint}`);
    await expect(page.getByRole('alert')).toContainText('Список временно недоступен');
    fail = false;
    await page.getByRole('button', { name: 'Повторить', exact: true }).click();
    await expect(page.getByRole('alert')).toHaveCount(0);
    await expect(page.getByText('Нет данных', { exact: true })).toBeVisible();
  });
}

test('состав проверки: серверные страницы, поиск и глобальные счётчики', async ({ page }) => {
  await mockApi(page, { status: 'Completed' });
  await page.route(`**/api/v1/inventories/${id}?**`, route => {
    const url = new URL(route.request().url());
    const pageNumber = Number(url.searchParams.get('page') ?? 1);
    const search = url.searchParams.get('search') ?? '';
    const items = Array.from({ length: 45 }, (_, i) => ({ id: `item-${i}`, inventoryNumber: `OS-${String(i + 1).padStart(3, '0')}`, name: 'Устройство', result: 'NotFound' })).filter(item => item.inventoryNumber.includes(search));
    return route.fulfill({ json: { id, number: 'INV-TEST', status: 'Completed', type: 'Manual', reason: 'Other', commission: [], unexpectedScans: [], items: items.slice((pageNumber - 1) * 20, pageNumber * 20), page: pageNumber, pageSize: 20, itemsTotalCount: items.length, counts: { total: 45, found: 1, notFound: 44, pending: 0, damaged: 1, discrepancies: 45, unexpected: 2 } } });
  });
  await page.goto(`/inventories/${id}`);
  await expect(page.getByText('OS-001', { exact: true })).toBeVisible();
  await expect(page.getByText('OS-021', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Go to page 3' }).click();
  await expect(page.getByText('OS-045', { exact: true })).toBeVisible();
  await expect(page.getByText('OS-001', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Всего:', { exact: false }).first()).toContainText('45');
  await page.getByLabel('Поиск по номеру, названию, помещению').fill('OS-002');
  await expect(page.getByText('OS-002', { exact: true })).toBeVisible();
  await expect(page.getByText('Всего по фильтру: 1 · по 20 строк')).toBeVisible();
  await expect(page.getByText('Посторонние QR:', { exact: false })).toContainText('2');
});

test('справочник: последняя страница и поиск по всему набору', async ({ page }) => {
  await mockApi(page);
  await page.route('**/api/v1/departments/paged?**', route => {
    const url = new URL(route.request().url());
    const pageNumber = Number(url.searchParams.get('page'));
    const search = url.searchParams.get('search') ?? '';
    const items = Array.from({ length: 45 }, (_, i) => ({ ...department, id: `dep-${i}`, code: `D-${i + 1}`, name: `Отдел ${i + 1}` })).filter(item => item.name.includes(search));
    return route.fulfill({ json: { items: items.slice((pageNumber - 1) * 20, pageNumber * 20), page: pageNumber, pageSize: 20, totalCount: items.length } });
  });
  await page.goto('/catalog/departments');
  await page.getByRole('button', { name: 'Go to page 3' }).click();
  await expect(page.getByText('Отдел 45', { exact: true })).toBeVisible();
  await expect(page.getByText('Отдел 1', { exact: true })).toHaveCount(0);
  await page.getByLabel('Поиск в списке').fill('Отдел 2');
  await expect(page.getByText('Отдел 2', { exact: true })).toBeVisible();
  await expect(page.getByText('Всего: 11 · по 20 строк')).toBeVisible();
});

test('клонирование: отмененная проверка, обязательная дата и серверная ошибка без ухода со страницы', async ({ page }) => {
  const writes = await mockApi(page, { status: 'Cancelled' });
  await page.goto(`/inventories/${id}`);
  await page.getByRole('button', { name: 'Создать на основе этой' }).click();
  await page.getByLabel('Плановая дата новой проверки').fill('');
  await page.getByRole('button', { name: 'Создать черновик', exact: true }).click();
  expect(writes).toHaveLength(0);
  await page.getByLabel('Плановая дата новой проверки').fill('2026-10-05T10:30');
  await page.getByRole('button', { name: 'Создать черновик', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].path).toBe(`/inventories/${id}/clone`);
  expect(writes[0].body).toEqual({ plannedAt: '2026-10-05T05:30:00.000Z' });
  await expect(page.getByText('Тестовый ответ сервера')).toBeVisible();
  await expect(page).toHaveURL(new RegExp(`/inventories/${id}$`));
});

test('клонирование: завершенная проверка создает черновик и показывает ссылку на источник', async ({ page }) => {
  await mockApi(page, { status: 'Completed' });
  await page.route(`**/api/v1/inventories/${id}/clone`, route => route.fulfill({ status: 201, json: { id: otherId } }));
    await page.route(`**/api/v1/inventories/${otherId}?**`, route => route.fulfill({ json: {
    id: otherId, number: 'INV-2', type: 'Manual', reason: 'AnnualReporting', status: 'Draft', plannedAt: '2026-10-05T05:30:00Z',
    items: [], commission: [], unexpectedScans: [], sourceInventoryId: id, sourceInventoryNumber: 'INV-1',
  } }));
  await page.goto(`/inventories/${id}`);
  await page.getByRole('button', { name: 'Создать на основе этой' }).click();
  await page.getByRole('button', { name: 'Создать черновик', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/inventories/${otherId}$`));
  await expect(page.getByRole('link', { name: 'INV-1', exact: true })).toHaveAttribute('href', `/inventories/${id}`);
  await expect(page.getByRole('button', { name: 'Создать на основе этой' })).toHaveCount(0);
});

test('клонирование недоступно во время активной проверки', async ({ page }) => {
  await mockApi(page, { status: 'Active' });
  await page.goto(`/inventories/${id}`);
  await expect(page.getByRole('heading', { name: 'Инвентаризация INV-1' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Создать на основе этой' })).toHaveCount(0);
});

test('ошибка запуска видна в диалоге и не переносится в последующую отмену', async ({ page }) => {
  await mockApi(page);
  await page.goto(`/inventories/${id}`);
  await page.getByRole('button', { name: 'Запустить', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Подтвердить', exact: true }).click();
  await expect(page.getByRole('dialog').getByText('Тестовый ответ сервера')).toBeVisible();
  await page.getByRole('dialog').getByRole('button', { name: 'Отмена', exact: true }).click();
  await page.getByRole('button', { name: 'Отменить', exact: true }).click();
  await expect(page.getByRole('dialog').getByText('Тестовый ответ сервера')).toHaveCount(0);
});

test('ОС: неверные числа не отправляются; исправленная карточка отправляется с ETag', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto(`/assets/${id}/edit`);
  await expect(page.getByLabel('Наименование')).toHaveValue('Стол');
  await page.getByLabel('Первоначальная стоимость').fill('-1');
  await page.getByLabel('Год выпуска').fill('2020.5');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByText('Стоимость не может быть отрицательной')).toBeVisible();
  await expect(page.getByText('Год должен быть целым числом')).toBeVisible();
  expect(writes).toHaveLength(0);
  await page.getByLabel('Первоначальная стоимость').fill('');
  await expect(page.getByText('Введите стоимость числом')).toBeVisible();
  await page.getByLabel('Первоначальная стоимость').fill('10.29');
  await page.getByLabel('Год выпуска').fill('2020');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].headers['if-match']).toBe('"3"');
  expect(writes[0].body.initialCost).toBe('10.29');
});

test('ОС: редактирование названия сохраняет стоимость до копейки на верхней границе', async ({ page }) => {
  const writes = await mockApi(page);
  const exact = '99999999999999999.99';
  await page.route(`**/api/v1/assets/${id}`, route => route.request().method() === 'GET'
    ? route.fulfill({ json: { ...asset, initialCost: Number(exact), initialCostExact: exact }, headers: { ETag: '"3"' } })
    : route.fallback());
  await page.goto(`/assets/${id}/edit`);
  await expect(page.getByLabel('Первоначальная стоимость')).toHaveValue(exact);
  await page.getByLabel('Наименование').fill('Стол после правки');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].headers['if-match']).toBe('"3"');
  expect(writes[0].body).toMatchObject({ name: 'Стол после правки', initialCost: exact });
});

test('новая ОС: пустая форма показывает ошибки обязательных полей', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto('/assets/new');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByText('Заполните поле «Инвентарный номер»')).toBeVisible();
  await expect(page.getByText('Выберите подразделение', { exact: true })).toBeVisible();
  expect(writes).toHaveLength(0);
});

test('ОС: фоновое обновление сохраняет правки и исходный ETag, конфликт можно явно перезагрузить', async ({ page }) => {
  await page.clock.install();
  await mockApi(page);
  let reads = 0;
  let sentEtag = '';
  await page.route(`**/api/v1/assets/${id}`, async route => {
    if (route.request().method() === 'PUT') {
      sentEtag = route.request().headers()['if-match'];
      return route.fulfill({ status: 412, json: { title: 'Конфликт версий' } });
    }
    const version = ++reads === 1 ? 3 : 4;
    return route.fulfill({ json: { ...asset, name: version === 3 ? 'Стол' : 'Изменено другим', version }, headers: { ETag: `"${version}"` } });
  });
  await page.goto(`/assets/${id}/edit`);
  await expect(page.getByLabel('Наименование')).toHaveValue('Стол');
  await page.getByLabel('Наименование').fill('Мои правки');
  await page.clock.fastForward(21_000);
  await page.evaluate(() => { window.dispatchEvent(new Event('offline')); window.dispatchEvent(new Event('online')); });
  await page.clock.runFor(100);
  await expect.poll(() => reads).toBeGreaterThan(1);
  await expect(page.getByLabel('Наименование')).toHaveValue('Мои правки');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Загрузить актуальную карточку и сбросить правки' })).toBeVisible();
  expect(sentEtag).toBe('"3"');
  await page.getByRole('button', { name: 'Загрузить актуальную карточку и сбросить правки' }).click();
  await expect(page.getByLabel('Наименование')).toHaveValue('Изменено другим');
});

test('ОС: незавершённое число в необязательном поле не превращается в пропуск', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto(`/assets/${id}/edit`);
  await expect(page.getByLabel('Наименование')).toHaveValue('Стол');
  await page.getByLabel('Год выпуска').pressSequentially('e');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByText('Введите корректное значение')).toBeVisible();
  expect(writes).toHaveLength(0);
});

test('организация: ИНН из букв блокирует сохранение, корректный принимается', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto('/catalog/organization');
  await page.getByLabel('ИНН').fill('abcdefghij');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByText('ИНН должен содержать 10 или 12 цифр')).toBeVisible();
  expect(writes).toHaveLength(0);
  await page.getByLabel('ИНН').fill('1234567890');
  await page.getByLabel('ИНН').press('Enter');
  await expect.poll(() => writes.length).toBe(1);
});

for (const kind of ['departments', 'locations', 'employees']) {
  test(`справочник ${kind}: пустая форма блокируется, заполненная сохраняется`, async ({ page }) => {
    const writes = await mockApi(page);
    await page.goto(`/catalog/${kind}`);
    await page.getByRole('button', { name: 'Добавить', exact: true }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Сохранить' }).click();
    await expect(dialog.getByText('Исправьте ошибки в полях формы.')).toBeVisible();
    expect(writes).toHaveLength(0);
    await dialog.getByLabel(kind === 'employees' ? 'Табельный номер' : 'Код').fill('NEW');
    await dialog.getByLabel(kind === 'employees' ? 'ФИО' : 'Название').fill('Новое значение');
    if (kind !== 'departments') {
      await dialog.getByRole('combobox').click();
      await page.getByRole('option', { name: 'Отдел', exact: true }).click();
    }
    await dialog.getByRole('button', { name: 'Сохранить' }).click();
    await expect.poll(() => writes.length).toBe(1);
  });
}

test('пользователь: короткий пароль и несовпадение подтверждения блокируются', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto('/catalog/users');
  await page.getByRole('button', { name: 'Создать пользователя' }).click();
  await page.getByLabel('Логин').fill('new-user');
  await page.getByLabel('Пароль (не менее 8 символов)').fill('123');
  await page.getByLabel('Подтверждение пароля').fill('456');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByText('Минимум 8 символов')).toBeVisible();
  await expect(page.getByText('Пароли не совпадают')).toBeVisible();
  expect(writes).toHaveLength(0);
});

test('пользователь: сотрудник обычного отдела доступен для администратора', async ({ page }) => {
  const writes = await mockApi(page);
  await page.route('**/api/v1/departments?**', route => route.fulfill({
    json: [department, { ...department, id: otherId, isInventoryDepartment: false, name: 'Бухгалтерия' }],
  }));
  await page.route('**/api/v1/employees?**', route => route.fulfill({
    json: [employee, { ...employee, id: otherId, departmentId: otherId, fullName: 'Сотрудник бухгалтерии' }],
  }));
  await page.goto('/catalog/users');
  await page.getByRole('button', { name: 'Создать пользователя' }).click();
  const dialog = page.getByRole('dialog');
  const role = dialog.getByRole('combobox').first();
  const staff = dialog.getByRole('combobox').last();
  await role.click();
  await page.getByRole('option', { name: 'Администратор', exact: true }).click();
  await staff.click();
  await page.getByRole('option', { name: 'Сотрудник бухгалтерии', exact: true }).click();
  await role.click();
  await page.getByRole('option', { name: 'Инвентаризатор', exact: true }).click();
  await expect(staff).not.toContainText('Сотрудник бухгалтерии');
  await staff.click();
  await expect(page.getByRole('option', { name: 'Сотрудник бухгалтерии', exact: true })).toHaveCount(0);
  await page.keyboard.press('Escape');
  await role.click();
  await page.getByRole('option', { name: 'Администратор', exact: true }).click();
  await staff.click();
  await page.getByRole('option', { name: 'Сотрудник бухгалтерии', exact: true }).click();
  await dialog.getByLabel('Логин').fill('accountant-admin');
  await dialog.getByLabel('Пароль (не менее 8 символов)').fill('test-password');
  await dialog.getByLabel('Подтверждение пароля').fill('test-password');
  await dialog.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body).toMatchObject({ role: 'Administrator', employeeId: otherId });
});

test('новая учётная запись открывается без прежних паролей и ошибки', async ({ page }) => {
  await mockApi(page);
  await page.goto('/catalog/users');
  const open = page.getByRole('button', { name: 'Создать пользователя' });
  await open.click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Логин').fill('new-user');
  await dialog.getByLabel('Пароль (не менее 8 символов)').fill('test-password');
  await dialog.getByLabel('Подтверждение пароля').fill('test-password');
  await dialog.getByRole('combobox').first().click();
  await page.getByRole('option', { name: 'Администратор', exact: true }).click();
  await dialog.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Тестовый ответ сервера');
  await dialog.getByRole('button', { name: 'Отмена', exact: true }).click();
  await open.click();
  await expect(dialog.getByLabel('Логин')).toHaveValue('');
  await expect(dialog.getByLabel('Пароль (не менее 8 символов)')).toHaveValue('');
  await expect(dialog.getByLabel('Подтверждение пароля')).toHaveValue('');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
});

test('отмена редактирования справочника очищает прошлую ошибку', async ({ page }) => {
  await mockApi(page);
  await page.goto('/catalog/departments');
  const add = page.getByRole('button', { name: 'Добавить', exact: true });
  await add.click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Код').fill('DUP');
  await dialog.getByLabel('Название').fill('Дубликат');
  await dialog.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Тестовый ответ сервера');
  await dialog.getByRole('button', { name: 'Отмена', exact: true }).click();
  await add.click();
  await expect(dialog.getByLabel('Код')).toHaveValue('');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
});

test('расписание: дробный интервал и пустая дата блокируются', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto('/schedules');
  await page.getByRole('button', { name: 'Создать расписание' }).click();
  await page.getByLabel('Название').fill('Ежегодная');
  await page.getByLabel('Интервал, месяцев').fill('1.5');
  await page.getByLabel('Следующий запуск').fill('');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect(page.getByText('Интервал должен быть целым')).toBeVisible();
  await expect(page.getByText('Введите корректные дату и время')).toBeVisible();
  expect(writes).toHaveLength(0);
  await page.getByLabel('Интервал, месяцев').fill('12');
  await page.getByLabel('Следующий запуск').fill('2026-10-03T10:30');
  await page.getByRole('button', { name: 'Сохранить', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body.nextRunAt).toBe('2026-10-03T05:30:00.000Z');
});

test('событие: дата и МОЛ обязательны', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto('/integration-events');
  await page.getByRole('button', { name: 'Смена МОЛ', exact: true }).click();
  await page.getByLabel('Внешний Event ID').fill('event-1');
  await page.getByLabel('Дата события').fill('');
  await page.getByRole('button', { name: 'Зарегистрировать' }).click();
  await expect(page.getByText('Выберите сотрудника')).toBeVisible();
  await expect(page.getByText('Введите корректные дату и время')).toBeVisible();
  expect(writes).toHaveLength(0);
  await choose(page, 0, 'Иванов Иван');
  await page.getByLabel('Дата события').fill('2026-10-03T10:30');
  await page.getByRole('button', { name: 'Зарегистрировать' }).click();
  await expect.poll(() => writes.length).toBe(1);
});

test('инвентаризация: пустая дата блокируется до обращения к API', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto('/inventories/new');
  await page.getByLabel('Плановая дата').fill('');
  await page.getByRole('button', { name: 'Создать', exact: true }).click();
  await expect(page.getByText('Введите корректные дату и время')).toBeVisible();
  expect(writes).toHaveLength(0);
  await page.getByLabel('Плановая дата').fill('2026-10-03T10:30');
  await page.getByRole('button', { name: 'Создать', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
});

test('импорт: выбор неподдерживаемого файла сбрасывает предыдущий файл', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto('/assets/import');
  const input = page.locator('input[type=file]');
  await input.setInputFiles({ name: 'assets.csv', mimeType: 'text/csv', buffer: Buffer.from('name\nasset') });
  await expect(page.getByRole('button', { name: 'Проверить файл' })).toBeEnabled();
  await input.setInputFiles({ name: 'wrong.txt', mimeType: 'text/plain', buffer: Buffer.from('wrong') });
  await expect(page.getByText('Поддерживаются только CSV и XLSX')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Проверить файл' })).toBeDisabled();
  expect(writes).toHaveLength(0);
});

test('импорт: неудачная перепроверка сбрасывает предпросмотр, успешный повтор убирает ошибку', async ({ page }) => {
  await mockApi(page);
  let previews = 0;
  let applies = 0;
  await page.route('**/api/v1/assets/imports?**', route => {
    const apply = new URL(route.request().url()).searchParams.get('apply') === 'true';
    const attempt = apply ? ++applies : ++previews;
    if ((!apply && attempt === 2) || (apply && attempt === 1)) {
      return route.fulfill({ status: 503, json: { title: 'Временная ошибка импорта' } });
    }
    return route.fulfill({ json: { importId: id, totalRows: 1, validRows: 1, invalidRows: 0, appliedRows: apply ? 1 : 0, rows: [] } });
  });
  await page.goto('/assets/import');
  await page.locator('input[type="file"]').setInputFiles({ name: 'assets.csv', mimeType: 'text/csv', buffer: Buffer.from('sample') });
  const preview = page.getByRole('button', { name: 'Проверить файл', exact: true });
  const apply = page.getByRole('button', { name: 'Применить импорт', exact: true });
  await preview.click();
  await expect(apply).toBeVisible();
  await preview.click();
  await expect(page.getByRole('alert')).toContainText('Временная ошибка импорта');
  await expect(apply).toHaveCount(0);
  await preview.click();
  await expect(apply).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await apply.click();
  await expect(page.getByRole('alert')).toContainText('Временная ошибка импорта');
  await apply.click();
  await expect(page.getByText('Применено:', { exact: false })).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('замена исполнителя: текущий выбран автоматически, новый обязателен', async ({ page }) => {
  const writes = await mockApi(page);
  await page.goto(`/inventories/${id}`);
  await page.getByRole('tab', { name: 'Исполнитель', exact: true }).click();
  await page.getByRole('button', { name: 'Заменить исполнителя' }).click();
  await page.getByRole('button', { name: 'Заменить', exact: true }).click();
  await expect(page.getByText('Выберите нового исполнителя')).toBeVisible();
  expect(writes).toHaveLength(0);
  await page.getByRole('dialog').getByRole('combobox').nth(0).click();
  await page.getByRole('option', { name: /Иванов Иван/ }).click();
  await page.getByRole('dialog').getByRole('combobox').nth(1).click();
  await page.getByRole('option', { name: 'Петров Петр' }).click();
  await page.getByRole('button', { name: 'Заменить', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body).toEqual({ oldUserId: id, newUserId: otherId });
  await expect(page.getByRole('dialog').getByText('Тестовый ответ сервера')).toBeVisible();
});

test('пустой черновик: можно назначить первого исполнителя', async ({ page }) => {
  const writes = await mockApi(page, { commission: [] });
  await page.goto(`/inventories/${id}`);
  await page.getByRole('tab', { name: 'Исполнитель', exact: true }).click();
  await page.getByRole('button', { name: 'Назначить исполнителя' }).click();
  await expect(page.getByRole('dialog').getByRole('combobox').nth(0)).toHaveText('Не назначен');
  await page.getByRole('dialog').getByRole('combobox').nth(1).click();
  await page.getByRole('option', { name: 'Петров Петр' }).click();
  await page.getByRole('button', { name: 'Заменить', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body).toEqual({ oldUserId: '00000000-0000-0000-0000-000000000000', newUserId: otherId });
});

test('проверка требует внимания: доступны назначение и повторный запуск', async ({ page }) => {
  const writes = await mockApi(page, { status: 'NeedsAttention' });
  await page.goto(`/inventories/${id}`);
  await expect(page.getByRole('button', { name: 'Запустить', exact: true })).toBeVisible();
  await page.getByRole('tab', { name: 'Исполнитель', exact: true }).click();
  await page.getByRole('button', { name: 'Заменить исполнителя' }).click();
  await page.getByRole('dialog').getByRole('combobox').nth(1).click();
  await page.getByRole('option', { name: 'Петров Петр' }).click();
  await page.getByRole('button', { name: 'Заменить', exact: true }).click();
  await expect.poll(() => writes.length).toBe(1);
  expect(writes[0].body).toEqual({ oldUserId: id, newUserId: otherId });
});

test('события: видны тип смены МОЛ и дата следующего повтора', async ({ page }) => {
  await mockApi(page);
  await page.route('**/api/v1/inventory-events/paged?**', route => route.fulfill({ json: { items: [{
    id, eventId: 'HR-RETRY-1', type: 'MolChanged', status: 'Pending', attempts: 2,
    lastError: 'Временная ошибка', receivedAt: '2026-10-03T05:00:00Z', nextAttemptAt: '2026-10-03T05:30:00Z',
  }], page: 1, pageSize: 20, totalCount: 1 } }));
  await page.goto('/integration-events');
  await expect(page.getByRole('columnheader', { name: 'Следующая попытка' })).toBeVisible();
  const row = page.getByRole('row').filter({ hasText: 'HR-RETRY-1' });
  await expect(row.getByText('Смена МОЛ', { exact: true })).toBeVisible();
  await expect(row.getByText('Временная ошибка')).toBeVisible();
  await expect(row.getByText(/03\.10\.(?:20)?26.*10:30/)).toBeVisible();
});
