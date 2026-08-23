import { test, expect } from '@playwright/test';

test('has title', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle(/On.Call|Dashboard/);
});

test('can navigate to login if not authenticated', async ({ page }) => {
  await page.goto('/');
  // Should either show dashboard (if logged in via dev auth) or auth prompt
  const heading = page.locator('h1, h2');
  await expect(heading).toBeVisible();
});

test('page loads without errors', async ({ page }) => {
  let errorCount = 0;
  page.on('console', msg => {
    if (msg.type() === 'error') {
      console.error('Browser error:', msg.text());
      errorCount++;
    }
  });

  await page.goto('/');
  expect(errorCount).toBe(0);
});
