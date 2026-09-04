import { test, expect } from '@playwright/test'
import {
  CHIEF_EMAIL, NOBODY_EMAIL, SHARED_PASSWORD, NS,
  openPasswordForm, loginWithPassword, captureConsoleErrors, nonBenign,
} from './ui-helpers'

/**
 * Signup is IP-rate-limited server-side (5/hour, see Program.cs "Signup" policy) and that
 * budget is shared across every agent testing this instance from the same machine, plus
 * this suite's own signup tests. Rather than hard-failing the whole suite when the shared
 * quota is already spent, these tests submit the form, read the real HTTP status of the
 * POST, and skip (with a clear reason, not silently) when they observe a 429 they did not
 * cause. A 429 with an unreadable/blank UI response is still asserted as a real defect.
 */
async function submitSignup(page: import('@playwright/test').Page) {
  const [res] = await Promise.all([
    page.waitForResponse((r) => r.url().includes('/api/auth/local/signup')),
    page.getByRole('button', { name: 'Create account' }).click(),
  ])
  return res
}

test.describe('Sign in / sign up', () => {
  test('chief@e2e.test can sign in through the real email+password form', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await expect(page).toHaveURL(/\/dashboard/)
    await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
    const bad = nonBenign(errors)
    expect(bad, `Unexpected console errors on dashboard load: ${bad.join(' | ')}`).toEqual([])
  })

  test('client-side password minimum matches the documented server minimum (12 chars)', async ({ page }) => {
    await openPasswordForm(page)
    await page.getByRole('button', { name: 'Create an account' }).click()

    const email = `${NS}minlen_${Date.now()}@e2e.test`
    await page.getByLabel('Your name').fill(`${NS}MinLen Tester`)
    await page.getByLabel('Email').fill(email)
    // 11 chars: one under the minimum both client and server enforce.
    await page.getByLabel('Password', { exact: true }).fill('short11pwd')

    // No network request should fire at all for a client-rejected password.
    let signupRequested = false
    page.on('request', (r) => { if (r.url().includes('/api/auth/local/signup')) signupRequested = true })
    await page.getByRole('button', { name: 'Create account' }).click()

    const inlineError = page.locator('p.text-sm.text-red-400', { hasText: 'Choose a password of at least 12 characters.' })
    await expect(inlineError).toBeVisible()
    await expect(page.getByText('Your account has been created.')).toHaveCount(0)
    expect(signupRequested, 'A sub-minimum password must be rejected client-side with no network call').toBe(false)
  })

  test('signup with a full-length password succeeds and explains access is pending', async ({ page }) => {
    await openPasswordForm(page)
    await page.getByRole('button', { name: 'Create an account' }).click()

    const email = `${NS}signup_${Date.now()}@e2e.test`
    await page.getByLabel('Your name').fill(`${NS}Signup Tester`)
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password', { exact: true }).fill(SHARED_PASSWORD)

    const res = await submitSignup(page)
    test.skip(res.status() === 429, 'Signup rate limit (5/hour/IP) already exhausted by shared agents — see rate-limit finding in report.')

    expect(res.ok(), `Expected signup to succeed, got HTTP ${res.status()}`).toBe(true)
    await expect(page.getByText('Your account has been created.')).toBeVisible()
    await expect(page.getByText(/administrator has to give you access/i)).toBeVisible()
  })

  test('a refused signup (duplicate email) shows a readable error, not a stack trace', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    await openPasswordForm(page)
    await page.getByRole('button', { name: 'Create an account' }).click()

    // chief@e2e.test already exists — this must be refused by the server.
    await page.getByLabel('Your name').fill(`${NS}Duplicate Tester`)
    await page.getByLabel('Email').fill(CHIEF_EMAIL)
    await page.getByLabel('Password', { exact: true }).fill(SHARED_PASSWORD)

    const res = await submitSignup(page)

    // Whether refused for being a duplicate (expected: 409/400) or for hitting the shared
    // rate limit first (429), the UI contract under test is the same either way: no false
    // success, and a readable (non-crashing) message. Only the specific wording differs.
    expect(res.ok(), 'A duplicate-email signup must never report success').toBe(false)
    await expect(page.getByText('Your account has been created.')).toHaveCount(0)

    const errorBox = page.locator('p.text-sm.text-red-400').first()
    await expect(errorBox).toBeVisible({ timeout: 10000 })
    const text = (await errorBox.textContent()) ?? ''
    expect(text.length).toBeGreaterThan(0)
    expect(text).not.toMatch(/at\s+\S+\.(cs|ts|tsx):\d+|StackTrace|System\.Exception|Unhandled exception/i)

    if (res.status() === 429) {
      test.info().annotations.push({
        type: 'note',
        description: `Hit the shared signup rate limit (429) before the duplicate-email check could be exercised specifically. UI error shown: "${text}". This is generic ("Could not create the account.") and does not distinguish "too many attempts, try later" from any other refusal — see rate-limit finding in report.`,
      })
    }

    const bad = nonBenign(errors)
    expect(bad, `Unexpected console errors on refused signup: ${bad.join(' | ')}`).toEqual([])
  })
})

test.describe('Zero-permission experience', () => {
  test('nobody@e2e.test sees an explicit "access pending" state, not a broken dashboard', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    await loginWithPassword(page, NOBODY_EMAIL, SHARED_PASSWORD)

    // Must land inside the authenticated shell (sidebar visible) rather than bouncing
    // back to /login, and must not hang on a spinner forever.
    await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible({ timeout: 10000 })

    // The explicit "awaiting provisioning" message, not a silently empty dashboard.
    await expect(page.getByRole('heading', { name: "You're signed in — access pending" })).toBeVisible({ timeout: 10000 })
    await expect(page.getByRole('main').getByText(NOBODY_EMAIL)).toBeVisible()

    // Confirm this is NOT the "server unreachable" or "session rejected" message —
    // those are meaningfully different states and must not be conflated.
    await expect(page.getByText("Can't reach the server")).toHaveCount(0)
    await expect(page.getByText("Your session wasn't accepted")).toHaveCount(0)

    // No redirect loop: staying on /dashboard, not bouncing to /dashboard repeatedly
    // or back to /login.
    await page.waitForTimeout(1500)
    await expect(page).toHaveURL(/\/dashboard/)

    const bad = nonBenign(errors)
    expect(bad, `Unexpected console errors for zero-permission user: ${bad.join(' | ')}`).toEqual([])
  })

  test('nobody@e2e.test hitting /admin directly is bounced to /dashboard, not left on a broken admin shell', async ({ page }) => {
    await loginWithPassword(page, NOBODY_EMAIL, SHARED_PASSWORD)
    await expect(page.getByRole('heading', { name: "You're signed in — access pending" })).toBeVisible({ timeout: 10000 })

    await page.goto('/admin')
    // AdminRoute should redirect to /dashboard (guard race check: must not render admin
    // content even transiently before redirecting).
    await expect(page).toHaveURL(/\/dashboard/, { timeout: 10000 })
    await expect(page.getByRole('heading', { name: "You're signed in — access pending" })).toBeVisible()
  })
})
