import { test, expect } from '@playwright/test'
import { CHIEF_EMAIL, SHARED_PASSWORD, NS, loginWithPassword, captureConsoleErrors, nonBenign } from './ui-helpers'

test.describe('Schedule', () => {
  test('view schedules, create a shift, and confirm department-type contacts are not assignable', async ({ page }) => {
    test.setTimeout(90000)
    const errors = captureConsoleErrors(page)
    const stamp = Date.now()
    const unitName = `${NS}SchedGapUnit_${stamp}`
    const personFirst = `${NS}SchedPerson`
    const personLast = `Test${stamp}`
    const personEmail = `${NS.toLowerCase()}schedperson_${stamp}@e2e.test`

    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)

    // ── Seed a department-type contact (must never be assignable) and a real person ──
    await page.goto('/dashboard/directory')
    await expect(page.getByRole('heading', { name: 'Phone Directory' })).toBeVisible()

    await page.getByRole('button', { name: 'Add Employee' }).click()
    await page.getByRole('button', { name: 'Unit / department' }).click()
    await page.getByLabel('Name').fill(unitName)
    await page.getByLabel('Office Phone').fill('+12025557000')
    await page.getByRole('button', { name: 'Create Employee' }).click()
    await expect(page.getByRole('heading', { name: unitName })).toBeVisible({ timeout: 10000 })

    await page.getByRole('button', { name: 'Add Employee' }).click()
    await page.getByRole('button', { name: 'Person', exact: true }).click()
    await page.getByLabel('First Name').fill(personFirst)
    await page.getByLabel('Last Name').fill(personLast)
    await page.getByLabel('Email', { exact: true }).fill(personEmail)
    await page.getByRole('button', { name: 'Create Employee' }).click()
    await expect(page.getByRole('heading', { name: `${personFirst} ${personLast}` })).toBeVisible({ timeout: 10000 })

    // ── Create a schedule ──
    await page.goto('/dashboard/schedule')
    await expect(page.getByRole('heading', { name: 'On-Call Schedule' })).toBeVisible()
    await page.getByRole('button', { name: 'New Schedule' }).click()
    await expect(page.getByRole('heading', { name: 'New Schedule' })).toBeVisible()

    // NOTE: none of this modal's <label> elements use htmlFor/id or wrap their input, so
    // they are not programmatically associated — getByLabel() cannot find these fields.
    // Falling back to placeholder/type locators to drive the form; see the accessibility
    // finding in the report for the underlying defect.
    const scheduleName = `${NS}Schedule_${stamp}`
    await page.getByPlaceholder('e.g., ER Attending Rotation - July').fill(scheduleName)

    const today = new Date()
    const inAWeek = new Date(today.getTime() + 7 * 24 * 60 * 60 * 1000)
    const fmt = (d: Date) => d.toISOString().slice(0, 10)
    const dateInputs = page.locator('input[type="date"]')
    await dateInputs.nth(0).fill(fmt(today))
    await dateInputs.nth(1).fill(fmt(inAWeek))

    await page.getByRole('button', { name: 'Create Schedule' }).click()
    await expect(page.getByRole('heading', { name: 'New Schedule' })).toHaveCount(0, { timeout: 10000 })

    // The new schedule is auto-selected; the weekly grid should now be visible.
    await expect(page.getByText('Select a schedule to view the weekly calendar')).toHaveCount(0, { timeout: 10000 })

    // ── Open the Assign Shift modal via a gap cell ──
    // A brand-new schedule has no shifts at all, so every calendar cell is a clickable gap.
    const gapCell = page.locator('div.bg-red-600\\/5.cursor-pointer').first()
    await expect(gapCell).toBeVisible({ timeout: 10000 })
    await gapCell.click()
    await expect(page.getByRole('heading', { name: 'Assign Shift' })).toBeVisible()

    // ── Department-type contact must not be offered as an assignable person ──
    await page.getByPlaceholder('Search by name, email, or title...').fill(unitName)
    await page.waitForTimeout(400)
    await expect(page.getByText('No employees found')).toBeVisible()

    // ── A real person IS assignable and can be picked ──
    await page.getByPlaceholder('Search by name, email, or title...').fill(personFirst)
    await page.waitForTimeout(400)
    await expect(page.getByText('No employees found')).toHaveCount(0)
    await page.getByText(`${personFirst} ${personLast}`, { exact: false }).first().click()

    await page.getByRole('button', { name: 'Assign Shift', exact: true }).click()

    // Modal closes on success and the shift now occupies that cell.
    await expect(page.getByRole('heading', { name: 'Assign Shift' })).toHaveCount(0, { timeout: 10000 })

    const bad = nonBenign(errors)
    expect(bad, `Console errors during schedule creation/assignment: ${bad.join(' | ')}`).toEqual([])
  })
})
