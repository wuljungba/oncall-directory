import { expect, type Page, type ConsoleMessage } from '@playwright/test'

/** Shared test accounts, per the E2E briefing. Never used for lockout testing. */
export const CHIEF_EMAIL = 'chief@e2e.test'
export const NOBODY_EMAIL = 'nobody@e2e.test'
export const SHARED_PASSWORD = 'correct-horse-battery-staple'

/** This agent's data namespace. Every record created through the UI is prefixed with this. */
export const NS = 'UI_'

/** Switches the login page from the SSO front door into the email+password form. */
export async function openPasswordForm(page: Page) {
  await page.goto('/login')
  await page.getByRole('button', { name: 'Use an email and password instead' }).click()
}

/**
 * The first-run "Welcome to OnCall" wizard (src/components/OnboardingWizard.tsx, mounted
 * app-wide in Layout.tsx) is a full-screen blocking modal (fixed inset-0 z-50) shown to any
 * Admin.Full/Admin.Scoped user whose active tenant has no `onboarding.completed[:tenantId]`
 * setting on the server. It is NOT scoped to /dashboard — it renders over every route the
 * shell wraps, so it silently eats clicks on Directory/Schedule/Admin buttons underneath it
 * if not dismissed first.
 *
 * In this shared instance chief@e2e.test's onboarding has never been completed for tenant
 * 1, 2, or 3 (verified: GET /api/settings/onboarding.completed[:N] all 404), so it appears
 * on every fresh chief session unless another agent's test happened to leave it dismissed.
 * nobody@e2e.test and iso_user_a@e2e.test never see it (no Admin.Full/Admin.Scoped), so this
 * only needs to run for admin-capable accounts, but it is harmless to call for anyone.
 */
export async function dismissOnboardingIfPresent(page: Page) {
  const skip = page.getByRole('button', { name: 'Skip setup' })
  try {
    await skip.waitFor({ state: 'visible', timeout: 3000 })
  } catch {
    return
  }
  await skip.click()
  await expect(page.getByRole('heading', { name: 'Welcome to OnCall' })).toHaveCount(0, { timeout: 5000 })
}

/**
 * Full sign-in flow through the real login page (no dev-auth bypass). Waits for the actual
 * post-login navigation to /dashboard: the "Sign in" click only dispatches the click event,
 * it does not wait for the async signIn() call inside LoginPage's handler, so a caller that
 * immediately navigates elsewhere can race the still-in-flight login request.
 *
 * Also dismisses the blocking first-run onboarding wizard if the account sees it (see
 * dismissOnboardingIfPresent above) — otherwise every subsequent button click in the test
 * silently times out fighting a modal the test never asked for and has no reason to expect.
 */
export async function loginWithPassword(page: Page, email: string, password: string) {
  await openPasswordForm(page)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: 'Sign in', exact: true }).click()
  await page.waitForURL('**/dashboard', { timeout: 15000 })
  await dismissOnboardingIfPresent(page)
}

export async function signOut(page: Page) {
  await page.getByRole('button', { name: 'Sign Out' }).click()
  await page.waitForURL('**/login')
}

/** Creates a local account through the real signup form (not the API directly). */
export async function signUpViaUi(page: Page, email: string, password: string, displayName?: string) {
  await openPasswordForm(page)
  await page.getByRole('button', { name: 'Create an account' }).click()
  if (displayName) await page.getByLabel('Your name').fill(displayName)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
}

/**
 * Attaches console/page-error listeners and returns a live array of captured messages.
 * MSAL noise is expected per the briefing ("Ignore MSAL console noise as long as the page
 * is usable") and is tagged so callers can filter it out rather than losing it entirely.
 */
export interface CapturedError {
  text: string
  benign: boolean
}

/**
 * SignalR logs a disconnect error whenever the page navigates away mid-connection, which
 * happens on essentially every test that changes route. It reports the teardown, not a
 * fault: the hub connection is asserted separately where it matters.
 */
const BENIGN_PATTERN = /msal|monitor_window_timeout|popup_window_error|BrowserAuthError|interaction_in_progress|uninitialized_public_client_application|no_account_error|Connection disconnected with error|Server returned an error on close|Failed to complete negotiation|The connection was stopped during negotiation/i

/**
 * Chromium logs a generic "Failed to load resource: the server responded with a status of
 * NNN" console error for EVERY non-2xx fetch/XHR response, regardless of whether the app
 * handles it (a deliberately-tested 401/403/404/409/429 included). It carries no URL in
 * msg.text(), so it cannot be told apart from a genuinely broken endpoint by text alone.
 * Treated as routine network-layer noise here; the app's own handling of that failure
 * (a visible error banner, a caught rejection, etc.) is asserted explicitly by each test
 * instead. What is NOT filtered: application console.error(...) calls with real messages
 * (e.g. "Failed to load dashboard:") and uncaught pageerrors — those indicate the app
 * itself hit an error branch and are exactly the silent-failure signal worth catching.
 */
const GENERIC_RESOURCE_FAILURE = /^Failed to load resource: the server responded with a status of \d+/

export function captureConsoleErrors(page: Page): CapturedError[] {
  const errors: CapturedError[] = []
  page.on('console', (msg: ConsoleMessage) => {
    if (msg.type() === 'error') {
      const text = msg.text()
      errors.push({ text, benign: BENIGN_PATTERN.test(text) || GENERIC_RESOURCE_FAILURE.test(text) })
    }
  })
  page.on('pageerror', (err) => {
    errors.push({ text: `pageerror: ${err.message}`, benign: BENIGN_PATTERN.test(err.message) })
  })
  return errors
}

export function nonBenign(errors: CapturedError[]): string[] {
  return errors.filter(e => !e.benign).map(e => e.text)
}
