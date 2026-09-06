import { test, expect } from '@playwright/test'
import { CHIEF_EMAIL, SHARED_PASSWORD, loginWithPassword } from './ui-helpers'

/**
 * The bulk action bar has to stay reachable while you scroll.
 *
 * It was written as `sticky top-0` and silently did not stick: the app shell wraps content in a
 * `min-h-screen` column whose `main` carries `overflow-auto` but never actually scrolls — the
 * document does — so sticky had no scroll range and the bar left the screen entirely. Selecting
 * rows near the bottom of a long list therefore scrolled the Deactivate and Delete buttons out
 * of view, which is the one thing the bar exists to prevent.
 */
test('the bulk action bar stays reachable after scrolling a long list', async ({ page }) => {
  test.setTimeout(120000)
  await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
  await page.goto('/admin')
  await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible({ timeout: 30000 })

  await page.getByRole('button', { name: /Accounts/i }).first().click()
  const selectAll = page.getByRole('checkbox', { name: /Select all \d+ shown/ })
  await expect(selectAll).toBeVisible({ timeout: 30000 })
  await selectAll.check()

  const bar = page.getByTestId('bulk-action-bar')
  await expect(bar).toBeVisible()

  const before = await bar.boundingBox()
  await page.evaluate(() => window.scrollTo(0, 600))
  await page.waitForTimeout(400)
  const after = await bar.boundingBox()

  // Pinned to the viewport, so scrolling must not move it at all.
  expect(after?.y).toBe(before?.y)
  await expect(bar).toBeInViewport()

  // Scoped to the bar deliberately: every employee row also carries a "Delete permanently"
  // control, so an unscoped query matches one per row.
  await expect(bar.getByRole('button', { name: 'Delete permanently' })).toBeInViewport()
  await expect(bar.getByRole('button', { name: 'Deactivate' })).toBeEnabled()
})
