import { expect, test } from '@playwright/test';
test('страница входа сначала запрашивает логин',async({page})=>{await page.goto('/login');await expect(page.getByRole('heading',{name:'Asset Inventory'})).toBeVisible();await expect(page.getByLabel('Логин')).toBeVisible();await expect(page.getByRole('button',{name:'Продолжить'})).toBeVisible()});
