import { expect, test } from '@playwright/test';

test('la shell applicativa si carica e il link "Vai al contenuto" è raggiungibile da tastiera', async ({ page }) => {
  await page.goto('/');

  await expect(page).toHaveTitle(/bProjects/);
  await expect(page.locator('.app-brand')).toHaveText('bProjects');

  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'Vai al contenuto' })).toBeFocused();
});
