import path from 'path'
import { fileURLToPath } from 'url'
import { test, expect } from '@playwright/test'
import { CHIEF_EMAIL, SHARED_PASSWORD, NS, loginWithPassword, captureConsoleErrors, nonBenign } from './ui-helpers'

// package.json sets "type": "module", so Playwright's TS files run as ESM here and
// __dirname is not defined (it crashes the entire suite at collection time, not just this
// file, since Playwright loads every spec to discover tests).
const __dirname = path.dirname(fileURLToPath(import.meta.url))
const FIXTURE = path.join(__dirname, 'fixtures', 'ui-import-multisheet.xlsx')

test.describe('Import wizard', () => {
  test.beforeEach(async ({ page }) => {
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await page.goto('/dashboard/directory')
    await expect(page.getByRole('heading', { name: 'Phone Directory' })).toBeVisible()
  })

  test('rejects an unsupported file type with a readable message, not a crash', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    await page.getByRole('button', { name: 'Import CSV' }).click()
    await expect(page.getByRole('heading', { name: 'Import directory' })).toBeVisible()

    const fileInput = page.locator('input[type="file"]')
    await fileInput.setInputFiles({
      name: 'roster.xls',
      mimeType: 'application/vnd.ms-excel',
      buffer: Buffer.from('not a real xls'),
    })
    await expect(page.getByText(/Legacy \.xls workbooks are not supported/)).toBeVisible()

    await fileInput.setInputFiles({
      name: 'roster.txt',
      mimeType: 'text/plain',
      buffer: Buffer.from('hello'),
    })
    await expect(page.getByText(/is not a spreadsheet/)).toBeVisible()

    const bad = nonBenign(errors)
    expect(bad, `Console errors on rejected file type: ${bad.join(' | ')}`).toEqual([])
  })

  test('walks Upload -> Sheets -> Mapping -> Review -> Commit with a real multi-sheet .xlsx, including Back navigation', async ({ page }) => {
    test.setTimeout(90000)
    const errors = captureConsoleErrors(page)

    await page.getByRole('button', { name: 'Import CSV' }).click()
    const modal = page.locator('div.max-w-3xl')
    await expect(modal.getByRole('heading', { name: 'Import directory' })).toBeVisible()

    // ── Upload ──
    await modal.locator('input[type="file"]').setInputFiles(FIXTURE)
    await expect(modal.getByText('ui-import-multisheet.xlsx')).toBeVisible()
    await modal.getByRole('button', { name: 'Read file' }).click()

    // ── Sheets ──
    await expect(modal.getByText(/2 sheets, 5 rows/)).toBeVisible({ timeout: 15000 })
    await expect(modal.getByText('Physicians')).toBeVisible()
    await expect(modal.getByText('Units')).toBeVisible()
    // Both included by default.
    const sheetCheckboxes = modal.locator('input[type="checkbox"]')
    await expect(sheetCheckboxes).toHaveCount(2)
    for (const cb of await sheetCheckboxes.all()) await expect(cb).toBeChecked()

    await modal.getByRole('button', { name: 'Next' }).click()

    // ── Mapping ──
    await expect(modal.getByText(/Everyday headings are recognised already/)).toBeVisible()
    // Auto-mapped: firstName column shows the "First name" mapping already selected.
    const firstNameRow = modal.locator('div.flex.items-center.gap-3', { has: modal.locator('p', { hasText: 'firstName' }) })
    await expect(firstNameRow.locator('select')).toHaveValue('firstName')

    // The unrecognised header "Cell #" must NOT have been silently dropped — it should sit
    // on "Ignore this column" until mapped by hand, and be visibly listed so the user knows
    // it exists.
    const cellRow = modal.locator('div.flex.items-center.gap-3', { has: modal.locator('p', { hasText: 'Cell #' }) })
    await expect(cellRow).toBeVisible()
    await expect(cellRow.locator('select')).toHaveValue('')
    await cellRow.locator('select').selectOption({ label: 'Mobile phone' })
    await expect(cellRow.locator('select')).toHaveValue('mobilePhone')

    // Switch to the second sheet's mapping and confirm its own columns are shown.
    await modal.getByRole('button', { name: 'Units', exact: true }).click()
    await expect(modal.locator('p[title="displayName"]')).toBeVisible()

    await modal.getByRole('button', { name: 'Next' }).click()

    // ── Review ──
    await expect(modal.getByText('Ready')).toBeVisible({ timeout: 15000 })
    await expect(modal.getByText('Problems')).toBeVisible()
    // Deterministic given the fixture: 2 rows are unimportable (duplicate email, bad department).
    const problemsLabel = modal.locator('p', { hasText: 'Problems' })
    const problemsValue = problemsLabel.locator('xpath=preceding-sibling::p[1]')
    await expect(problemsValue).toHaveText('2')

    // The two problems must read as plain, actionable English, not an exception dump.
    await expect(modal.getByText(/also appears on Physicians row/)).toBeVisible()
    await expect(modal.getByText(/Department 'UI_NonexistentDept_XYZ123' does not exist/)).toBeVisible()

    // ── Back navigation must actually work, and preserve the mapping already made ──
    await modal.getByRole('button', { name: 'Back' }).click()
    await expect(modal.getByText(/Everyday headings are recognised already/)).toBeVisible()
    // Mapping re-opens on whichever sheet tab was last active (Units); switch back to
    // Physicians to confirm the manual mapping survived the round trip back from Review.
    await modal.getByRole('button', { name: 'Physicians', exact: true }).click()
    await expect(cellRow.locator('select')).toHaveValue('mobilePhone')

    await modal.getByRole('button', { name: 'Back' }).click()
    await expect(modal.getByText(/Untick any you do not want/)).toBeVisible()

    // Forward again to Review.
    await modal.getByRole('button', { name: 'Next' }).click()
    await modal.getByRole('button', { name: 'Next' }).click()
    await expect(modal.getByText(/also appears on Physicians row/)).toBeVisible({ timeout: 15000 })

    // The two problem rows must be explicitly skipped before a commit will succeed — the
    // review copy says so ("Skip them, or fix the file and start again"). Do that here.
    const skipButtons = modal.getByRole('button', { name: 'Skip' })
    await expect(skipButtons).toHaveCount(2)
    await skipButtons.first().click()
    await expect(modal.getByRole('button', { name: 'Skip' })).toHaveCount(1, { timeout: 10000 })
    await modal.getByRole('button', { name: 'Skip' }).click()
    await expect(modal.getByRole('button', { name: 'Skip' })).toHaveCount(0, { timeout: 10000 })

    // ── Commit ──
    const importButton = modal.getByRole('button', { name: /^Import \d+$/ })
    await expect(importButton).toHaveText('Import 3')
    await importButton.click()

    await expect(modal.getByText(/entries? imported\./)).toBeVisible({ timeout: 15000 })
    await expect(modal.getByText('3 entries imported.')).toBeVisible()

    await modal.getByRole('button', { name: 'Close' }).click()
    await expect(page.getByRole('heading', { name: 'Import directory' })).toHaveCount(0)

    const bad = nonBenign(errors)
    expect(bad, `Console errors during the full import walkthrough: ${bad.join(' | ')}`).toEqual([])

    // Confirm the imported rows actually landed in the directory.
    await page.getByRole('searchbox', { name: 'Search the directory' }).fill('UI_import1')
    await page.waitForTimeout(600)
    await expect(page.getByText('UI_John UI_Import1')).toBeVisible()
    await expect(page.getByText('UI_Jane UI_Import2')).toBeVisible()
  })

  test('committing WITHOUT skipping problem rows refuses the whole import rather than silently writing the good rows', async ({ page }) => {
    test.setTimeout(60000)
    const errors = captureConsoleErrors(page)

    await page.getByRole('button', { name: 'Import CSV' }).click()
    const modal = page.locator('div.max-w-3xl')
    await modal.locator('input[type="file"]').setInputFiles(FIXTURE)
    await modal.getByRole('button', { name: 'Read file' }).click()
    await expect(modal.getByText(/2 sheets, 5 rows/)).toBeVisible({ timeout: 15000 })
    await modal.getByRole('button', { name: 'Next' }).click()
    await modal.getByRole('button', { name: 'Next' }).click()
    await expect(modal.getByText(/also appears on Physicians row/)).toBeVisible({ timeout: 15000 })

    // The "Import 3" label counts only the 3 clean rows, which invites the belief that
    // pressing it imports those 3 and simply leaves the 2 problem rows out. That is NOT
    // what happens while a problem row is still marked included (the default) — the
    // commit is atomic and is refused outright. Confirm that refusal is honest (a visible
    // error, zero rows written) rather than a silent partial import.
    const importButton = modal.getByRole('button', { name: /^Import \d+$/ })
    await expect(importButton).toHaveText('Import 3')
    await importButton.click()

    // Must NOT report a success.
    await expect(modal.getByText(/entries? imported\./)).toHaveCount(0, { timeout: 10000 })
    // Must show a readable, row-referenced refusal, not a stack trace or a blank failure.
    await expect(modal.getByText(/row \d+:/i).first()).toBeVisible({ timeout: 10000 })

    const bad = nonBenign(errors)
    expect(bad, `Console errors when committing with unresolved problem rows: ${bad.join(' | ')}`).toEqual([])
  })

  test('a committed job cannot be committed twice from the same modal state', async ({ page }) => {
    test.setTimeout(60000)
    // Re-run a minimal single-sheet import so this test is independent of the big one.
    await page.getByRole('button', { name: 'Import CSV' }).click()
    const modal = page.locator('div.max-w-3xl')
    await modal.locator('input[type="file"]').setInputFiles(FIXTURE)
    await modal.getByRole('button', { name: 'Read file' }).click()
    await expect(modal.getByText(/2 sheets, 5 rows/)).toBeVisible({ timeout: 15000 })
    await modal.getByRole('button', { name: 'Next' }).click()
    await modal.getByRole('button', { name: 'Next' }).click()
    await expect(modal.getByText(/also appears on Physicians row/)).toBeVisible({ timeout: 15000 })

    const skipButtons = modal.getByRole('button', { name: 'Skip' })
    await skipButtons.first().click()
    await expect(modal.getByRole('button', { name: 'Skip' })).toHaveCount(1, { timeout: 10000 })
    await modal.getByRole('button', { name: 'Skip' }).click()
    await expect(modal.getByRole('button', { name: 'Skip' })).toHaveCount(0, { timeout: 10000 })

    const importButton = modal.getByRole('button', { name: /^Import \d+$/ })
    await importButton.click()
    await expect(modal.getByText(/entries? imported\./)).toBeVisible({ timeout: 15000 })
    // After commit, only "Close" is offered — there is no way to accidentally re-commit.
    await expect(modal.getByRole('button', { name: 'Close' })).toBeVisible()
    await expect(modal.getByRole('button', { name: /^Import \d+$/ })).toHaveCount(0)
  })
})
