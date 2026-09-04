import { test, expect } from '@playwright/test'
import { CHIEF_EMAIL, SHARED_PASSWORD, NS, loginWithPassword, captureConsoleErrors, nonBenign } from './ui-helpers'

test.describe('Directory', () => {
  test.beforeEach(async ({ page }) => {
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await page.goto('/dashboard/directory')
    await expect(page.getByRole('heading', { name: 'Phone Directory' })).toBeVisible()
  })

  test('browse and search the directory', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    await expect(page.getByPlaceholder(/Search by name, specialty/)).toBeVisible()
    // Search for something that should not exist to confirm the empty state renders sanely.
    await page.getByRole('searchbox', { name: 'Search the directory' }).fill(`${NS}__no_such_person__zzz`)
    await page.waitForTimeout(500)
    await expect(page.getByText('No employees found')).toBeVisible()
    // Clear back
    await page.getByRole('searchbox', { name: 'Search the directory' }).fill('')
    await page.waitForTimeout(500)

    const bad = nonBenign(errors)
    expect(bad, `Console errors while browsing/searching directory: ${bad.join(' | ')}`).toEqual([])
  })

  test('add a department-type contact with a phone and NO email: no broken mailto/Teams link, initials do not crash', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    const unitName = `${NS}3North_${Date.now()}`

    await page.getByRole('button', { name: 'Add Employee' }).click()
    await expect(page.getByRole('heading', { name: 'Add Employee' })).toBeVisible()

    await page.getByRole('button', { name: 'Unit / department' }).click()
    await page.getByLabel('Name').fill(unitName)
    await page.getByLabel('Extension (optional)').fill('4321')

    await page.getByRole('button', { name: 'Create Employee' }).click()

    // Modal closes and the new unit becomes selected in the detail pane.
    await expect(page.getByRole('heading', { name: 'Add Employee' })).toHaveCount(0, { timeout: 10000 })
    await expect(page.getByRole('heading', { name: unitName })).toBeVisible({ timeout: 10000 })

    // No email on file, so the detail pane must say so rather than blank.
    await expect(page.getByText('No email on file')).toBeVisible()

    // Critically: no dead mailto: link and no dead Teams-chat button, because there is no
    // address to build either from.
    await expect(page.locator('a[href^="mailto:"]')).toHaveCount(0)
    await expect(page.locator('a[href*="teams.microsoft.com"]')).toHaveCount(0)

    // The extension-only contact is not directly "call"-able by tel: unless it has an
    // officePhone/mobilePhone. Confirm this doesn't render a broken tel: link either.
    // (No officePhone/mobilePhone was set — only an extension.)
    const callLink = page.locator('a[href^="tel:"]')
    expect(await callLink.count()).toBe(0)

    // Initials/avatar must not crash: the avatar shows the first two characters of the
    // unit name, not throw or render blank.
    const initials = unitName.slice(0, 2).toUpperCase()
    await expect(page.locator('div.rounded-full', { hasText: initials }).first()).toBeVisible()

    const bad = nonBenign(errors)
    expect(bad, `Console errors adding a department contact: ${bad.join(' | ')}`).toEqual([])
  })

  test('add a department-type contact with an office phone (no email): Call link works, mailto/Teams absent', async ({ page }) => {
    const unitName = `${NS}5South_${Date.now()}`
    await page.getByRole('button', { name: 'Add Employee' }).click()
    await page.getByRole('button', { name: 'Unit / department' }).click()
    await page.getByLabel('Name').fill(unitName)
    await page.getByLabel('Office Phone').fill('+12025559999')
    await page.getByRole('button', { name: 'Create Employee' }).click()

    await expect(page.getByRole('heading', { name: unitName })).toBeVisible({ timeout: 10000 })
    await expect(page.locator('a[href="tel:+12025559999"]')).toBeVisible()
    await expect(page.locator('a[href^="mailto:"]')).toHaveCount(0)
    await expect(page.locator('a[href*="teams.microsoft.com"]')).toHaveCount(0)
  })

  test('department contact validation: rejects a unit with no name and no phone/extension', async ({ page }) => {
    await page.getByRole('button', { name: 'Add Employee' }).click()
    await page.getByRole('button', { name: 'Unit / department' }).click()
    await page.getByRole('button', { name: 'Create Employee' }).click()
    await expect(page.getByText('A department contact needs a name, e.g. "3North".')).toBeVisible()

    await page.getByLabel('Name').fill(`${NS}NoPhoneUnit`)
    await page.getByRole('button', { name: 'Create Employee' }).click()
    await expect(page.getByText('A department contact needs a phone number or an extension.')).toBeVisible()
  })
})
