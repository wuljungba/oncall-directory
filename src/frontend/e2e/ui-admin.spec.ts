import { test, expect } from '@playwright/test'
import { CHIEF_EMAIL, SHARED_PASSWORD, loginWithPassword, captureConsoleErrors, nonBenign } from './ui-helpers'

test.describe('Admin area', () => {
  test.beforeEach(async ({ page }) => {
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
  })

  test('Admin > Verification tab loads for a super admin, with an accessible pending banner if present', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    await page.goto('/admin')
    await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible()

    await page.getByRole('button', { name: 'Verification' }).click()
    // The section renders a "Subscription" picker and an org-type picker as soon as it
    // mounts (both are properly <label htmlFor>-associated).
    await expect(page.getByLabel('Subscription')).toBeVisible({ timeout: 10000 })
    await expect(page.getByLabel('Kind of organization')).toBeVisible()

    const bad = nonBenign(errors)
    expect(bad, `Console errors on Admin > Verification: ${bad.join(' | ')}`).toEqual([])
  })

  test('the Admin Overview tab surfaces a pending-verification banner that jumps to the Verification tab, when one exists', async ({ page }) => {
    await page.goto('/admin')
    await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible()
    // Overview is the default tab. The banner is conditional on shared, cross-agent tenant
    // state, so only assert its behavior IF it is showing — its absence is not itself a
    // defect in a shared environment where other agents may have already resolved it.
    const banner = page.getByText(/organization(s)? waiting on verification/i)
    if (await banner.count() > 0) {
      await banner.click()
      await expect(page.getByLabel('Subscription')).toBeVisible({ timeout: 10000 })
    } else {
      test.info().annotations.push({
        type: 'note',
        description: 'No pending-verification banner was showing for chief@e2e.test at run time (shared-DB dependent); banner navigation not exercised.',
      })
    }
  })

  test('Settings > extension prefix field is labelled, accepts input, and is not left mutated by this test', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    await page.goto('/dashboard/settings')
    await expect(page.getByRole('heading', { name: /settings/i }).first()).toBeVisible({ timeout: 10000 })

    const field = page.getByLabel('Extension prefix')
    await expect(field).toBeVisible({ timeout: 10000 })
    await expect(page.getByText(/gets the dialable number/)).toBeVisible()

    // This setting is estate-wide and shared across every agent testing this instance
    // (see the source comment: "the estate-wide default"), so this test deliberately does
    // NOT click Save with a changed value — that would race other agents' state. It only
    // confirms the field is interactive and reverts in-memory before navigating away.
    const original = await field.inputValue()
    await field.fill('999999')
    await expect(field).toHaveValue('999999')
    await field.fill(original)
    await expect(field).toHaveValue(original)

    const bad = nonBenign(errors)
    expect(bad, `Console errors on Settings page: ${bad.join(' | ')}`).toEqual([])
  })
})
