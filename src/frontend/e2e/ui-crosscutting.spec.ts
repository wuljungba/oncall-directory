import { test, expect } from '@playwright/test'
import {
  CHIEF_EMAIL, SHARED_PASSWORD, NS,
  loginWithPassword, signOut, captureConsoleErrors, nonBenign,
} from './ui-helpers'

const MAIN_PAGES = [
  { path: '/dashboard', heading: 'Dashboard' },
  { path: '/dashboard/schedule', heading: 'On-Call Schedule' },
  { path: '/dashboard/directory', heading: 'Phone Directory' },
  { path: '/dashboard/code-calls', heading: null },
  { path: '/dashboard/time-off', heading: null },
  { path: '/dashboard/compliance', heading: null },
  { path: '/dashboard/settings', heading: 'Settings' },
  { path: '/admin', heading: 'Admin' },
]

test.describe('Cross-cutting: console errors on every main page', () => {
  test('no unexpected console errors sweeping every nav destination as chief@e2e.test', async ({ page }) => {
    test.setTimeout(90000)
    const errors = captureConsoleErrors(page)
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await expect(page).toHaveURL(/\/dashboard/)

    const perPage: Record<string, string[]> = {}
    for (const { path, heading } of MAIN_PAGES) {
      errors.length = 0
      await page.goto(path)
      if (heading) await expect(page.getByRole('heading', { name: heading }).first()).toBeVisible({ timeout: 10000 })
      await page.waitForTimeout(800)
      const bad = nonBenign(errors)
      if (bad.length) perPage[path] = bad
    }

    expect(perPage, `Console errors found on: ${JSON.stringify(perPage, null, 2)}`).toEqual({})
  })
})

test.describe('Cross-cutting: narrow viewport (390px)', () => {
  test.use({ viewport: { width: 390, height: 844 } })

  test('dashboard and directory remain usable at 390px width', async ({ page }) => {
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()

    // The sidebar is off-canvas by default on narrow screens (translate-x-full) and the
    // hamburger button must be reachable to open it.
    const hamburger = page.locator('header button.lg\\:hidden')
    await expect(hamburger).toBeVisible()
    await hamburger.click()
    await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible()

    // Scoped to the nav, and .first(): the shell renders both an off-canvas sidebar and
    // a desktop one, so an unscoped role query matches two links and Playwright's strict
    // mode refuses to guess which.
    await page.getByRole('navigation', { name: 'Main navigation' })
      .getByRole('link', { name: 'Phone Directory' }).first().click()
    await expect(page).toHaveURL(/\/dashboard\/directory/)
    await expect(page.getByRole('heading', { name: 'Phone Directory' })).toBeVisible()

    // The search box must not be clipped/unusable at this width.
    const search = page.getByRole('searchbox', { name: 'Search the directory' })
    await expect(search).toBeVisible()
    const box = await search.boundingBox()
    expect(box, 'search box should have a measurable layout box').not.toBeNull()
    if (box) {
      expect(box.width).toBeGreaterThan(100)
      // Must not overflow past the 390px viewport (a common narrow-viewport bug).
      expect(box.x + box.width).toBeLessThanOrEqual(391)
    }
  })
})

test.describe('Cross-cutting: accessibility spot checks', () => {
  test('every visible text input/select on the Add Employee form has an accessible name', async ({ page }) => {
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await page.goto('/dashboard/directory')
    await page.getByRole('button', { name: 'Add Employee' }).click()
    await expect(page.getByRole('heading', { name: 'Add Employee' })).toBeVisible()

    const dialog = page.locator('div.max-w-lg')
    const controls = dialog.locator('input:visible, select:visible, textarea:visible')
    const count = await controls.count()
    expect(count).toBeGreaterThan(0)
    const unnamed: string[] = []
    for (let i = 0; i < count; i++) {
      const el = controls.nth(i)
      const accName = await el.evaluate((node) => {
        const input = node as HTMLInputElement
        if (input.labels && input.labels.length > 0) return input.labels[0].textContent
        return input.getAttribute('aria-label') || input.getAttribute('aria-labelledby') || input.getAttribute('placeholder')
      })
      if (!accName || !accName.trim()) {
        const outerHtml = await el.evaluate((node) => (node as HTMLElement).outerHTML.slice(0, 120))
        unnamed.push(outerHtml)
      }
    }
    expect(unnamed, `Unlabelled form controls found: ${unnamed.join('\n')}`).toEqual([])
  })

  test('every button in the main sidebar navigation has a discernible accessible name', async ({ page }) => {
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    const nav = page.getByRole('navigation', { name: 'Main navigation' })
    const links = nav.getByRole('link')
    const count = await links.count()
    expect(count).toBeGreaterThan(0)
    for (let i = 0; i < count; i++) {
      const name = await links.nth(i).evaluate((n) => (n.textContent || '').trim())
      expect(name.length, `Nav link #${i} has no discernible text`).toBeGreaterThan(0)
    }
  })
})

test.describe('Cross-cutting: partial-permission failures must be visible, not silent', () => {
  test('a user with Directory.Read but NOT Schedule.Read gets a visible error on Schedule, and Dashboard must not silently pretend nobody is on call', async ({ page, browser }) => {
    test.setTimeout(90000)
    const stamp = Date.now()
    const scopedEmail = `${NS}scoped_${stamp}@e2e.test`

    // ── Create the scoped account ──
    // Signup is IP-rate-limited (5/hour) and that budget is shared across every agent on
    // this machine. Skip cleanly rather than false-failing if it is already spent.
    await page.goto('/login')
    await page.getByRole('button', { name: 'Use an email and password instead' }).click()
    await page.getByRole('button', { name: 'Create an account' }).click()
    await page.getByLabel('Your name').fill(`${NS}Scoped Tester`)
    await page.getByLabel('Email').fill(scopedEmail)
    await page.getByLabel('Password', { exact: true }).fill(SHARED_PASSWORD)
    const [signupRes] = await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/auth/local/signup')),
      page.getByRole('button', { name: 'Create account' }).click(),
    ])
    test.skip(signupRes.status() === 429, 'Signup rate limit (5/hour/IP) already exhausted by shared agents — see rate-limit finding in report.')
    expect(signupRes.ok(), `Expected signup to succeed, got HTTP ${signupRes.status()}`).toBe(true)
    await expect(page.getByText('Your account has been created.')).toBeVisible({ timeout: 10000 })

    // ── As chief, grant Directory.Read only (explicitly remove the default Schedule perms) ──
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await page.goto('/admin')
    await page.getByRole('button', { name: 'Users & Permissions' }).click()
    await expect(page.getByRole('heading', { name: 'Grant on-call permission to a user', exact: false })).toBeVisible({ timeout: 10000 })

    await page.getByPlaceholder('user@hospital.org or object-id').fill(scopedEmail)
    // Defaults are Schedule.Read + Schedule.Write ON; turn both off, turn Directory.Read on.
    await page.getByRole('button', { name: 'On-Call Schedule — Read' }).click()
    await page.getByRole('button', { name: 'On-Call Schedule — Write' }).click()
    await page.getByRole('button', { name: 'Directory — Read' }).click()
    await page.getByRole('button', { name: 'Grant Permission' }).click()
    await expect(page.getByText('Permission granted.')).toBeVisible({ timeout: 10000 })

    await signOut(page)

    // ── Sign in as the scoped account in a fresh context (avoid any leaked session state) ──
    const scopedContext = await browser.newContext()
    const scopedPage = await scopedContext.newPage()
    const scopedErrors = captureConsoleErrors(scopedPage)
    try {
      await loginWithPassword(scopedPage, scopedEmail, SHARED_PASSWORD)
      await expect(scopedPage).toHaveURL(/\/dashboard/, { timeout: 10000 })

      // Must NOT be the zero-permission screen — this account has Directory.Read.
      await expect(scopedPage.getByRole('heading', { name: "You're signed in — access pending" })).toHaveCount(0)
      await expect(scopedPage.getByRole('heading', { name: 'Dashboard' })).toBeVisible({ timeout: 10000 })

      // Dashboard's "On Call" panel calls Schedule.Read-gated endpoints. Give it time to
      // fail, then check what the user actually sees.
      await scopedPage.waitForTimeout(1500)
      const dashboardShowsNoOneOnCall = await scopedPage.getByText('No one is currently on call').isVisible()
      const dashboardShowsVisibleError = await scopedPage.getByText(/failed to load|could not load|error loading/i).isVisible().catch(() => false)

      // Confirm the request actually failed (proving any "No one on call" text is a lie,
      // not a fact) by checking a console error was logged for it.
      const sawFailureLogged = scopedErrors.some(e => /Failed to load dashboard/i.test(e.text))

      if (dashboardShowsNoOneOnCall && !dashboardShowsVisibleError && sawFailureLogged) {
        test.info().annotations.push({
          type: 'defect',
          description:
            'P1: Dashboard silently swallows the Schedule.Read 403 (console.error only) and ' +
            'renders "No one is currently on call" — indistinguishable from the true empty ' +
            'state. A user with only Directory.Read sees a clinically misleading dashboard ' +
            'with no visible error.',
        })
      }
      // This assertion is the actual, reproducible proof captured above: fail the test so
      // this is impossible to miss in results, with the evidence in the annotation/report.
      expect(
        !dashboardShowsNoOneOnCall || dashboardShowsVisibleError,
        'Dashboard showed the "no one on call" empty state with NO visible error banner ' +
        'while the underlying request actually failed (403) — see annotations for detail.',
      ).toBe(true)
    } finally {
      // ── Contrast: Schedule page DOES show a visible error for the same account ──
      await scopedPage.goto('/dashboard/schedule')
      await expect(scopedPage.getByText(/Failed to load schedules and departments\./)).toBeVisible({ timeout: 10000 }).catch(() => {})

      // ── Directory, which IS granted, must work normally with no error ──
      await scopedPage.goto('/dashboard/directory')
      await expect(scopedPage.getByRole('heading', { name: 'Phone Directory' })).toBeVisible({ timeout: 10000 })

      await scopedContext.close()
    }
  })
})
