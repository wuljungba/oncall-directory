import { test, expect, type Page } from '@playwright/test'
import {
  CHIEF_EMAIL,
  SHARED_PASSWORD,
  NS,
  loginWithPassword,
  dismissOnboardingIfPresent,
  captureConsoleErrors,
  nonBenign,
} from './ui-helpers'

/**
 * Admin > Subscriptions: "Copy sign-in consent link" / "Copy directory consent link".
 *
 * copyConsentLink() in src/pages/AdminPage.tsx used to copy a single numbered, labeled
 * block holding BOTH admin-consent URLs, which a human had to pick apart by hand before
 * either link was usable (see the comment directly above copyConsentLink in that file).
 * The fix copies exactly one bare URL per button. These tests exist to catch a
 * regression back toward the labeled-block shape, not merely to confirm "a URL gets
 * copied" — several assertions below would still pass for a prose block that happens to
 * contain a valid URL, which is why the no-second-URL / no-list-marker / no-label checks
 * are asserted explicitly alongside the plain equality check against the API.
 */

/** The old block's tells: numbered markers and the two links' prose labels. */
const PROSE_OR_LIST_MARKER = /Let your staff sign in|Let OnCall read|^\s*\d+\./

interface ApiTenant {
  id: number
  name: string
  azureAdTenantId?: string | null
}

interface ConsentLinkResponse {
  directoryTenantId: string
  redirectUri: string
  signInConsentUrl: string
  directoryConsentUrl: string
}

/**
 * Lands on Admin > Subscriptions. Works either way: where dev auth is on, `/admin` is
 * reachable directly with no sign-in; where it's off, `/admin` bounces to `/login` and
 * the `loginWithPassword` fallback covers that. The onboarding wizard is a full-screen
 * modal mounted app-wide (see dismissOnboardingIfPresent's own doc comment) that eats
 * clicks on the tab bar underneath it if not dismissed first, so it is called here
 * regardless of which path got us to the page.
 */
async function gotoAdminSubscriptions(page: Page) {
  await page.goto('/admin')
  if (page.url().includes('/login')) {
    await loginWithPassword(page, CHIEF_EMAIL, SHARED_PASSWORD)
    await page.goto('/admin')
  }
  await expect(page.getByRole('heading', { name: 'Admin' })).toBeVisible({ timeout: 15000 })
  await dismissOnboardingIfPresent(page)

  await page.getByRole('button', { name: 'Subscriptions' }).click()
  // TenantsSection loads its list asynchronously; every row (linked or not) renders a
  // "+ Admin" button, so its presence means the list has finished loading.
  await expect(page.getByRole('button', { name: '+ Admin' }).first()).toBeVisible({ timeout: 15000 })
}

/**
 * Scopes to one subscription's row. `div.filter({ has: getByText(name) })` alone
 * resolves to an inner div that stops short of the row's action buttons (found by hand
 * during manual verification of this same UI) — the name paragraph and the buttons sit
 * in sibling subtrees under a shared row wrapper. Anchoring the filter on a control every
 * row wrapper also holds ("+ Admin", present whether or not the directory is connected)
 * lands on that wrapper instead.
 */
function subscriptionRow(page: Page, name: string) {
  return page.locator('div')
    .filter({ has: page.getByText(name, { exact: true }) })
    .filter({ has: page.getByRole('button', { name: '+ Admin' }) })
    .last()
}

async function fetchTenants(page: Page): Promise<ApiTenant[]> {
  const res = await page.request.get('/api/tenants?includeInactive=true')
  expect(res.ok(), `GET /api/tenants?includeInactive=true returned ${res.status()}`).toBeTruthy()
  return res.json()
}

test.describe('Admin > Subscriptions consent-link buttons', () => {
  test.beforeEach(async ({ page }) => {
    await gotoAdminSubscriptions(page)
  })

  test('a subscription row with a connected directory shows both consent-link buttons', async ({ page }) => {
    const errors = captureConsoleErrors(page)
    const tenants = await fetchTenants(page)
    // Scoped to this suite's own NS ('UI_') fixture namespace on purpose: the shared
    // instance can hold real customer subscriptions, and taking the first tenant the API
    // happens to return with a connected directory would fetch that real tenant's real
    // directory-consent link — its real directory tenant id, client ids, and name would
    // then land in the Playwright HTML report and any retry trace on a failing run.
    const linked = tenants.find(t => t.name.startsWith(NS) && !!t.azureAdTenantId)
    test.skip(
      !linked,
      `This suite needs a ${NS}-prefixed fixture subscription with a connected directory ` +
      `(expected the seeded ${NS}ConsentLinked subscription, id 5) but none was found. ` +
      'It will not test against an unnamespaced (real) tenant on purpose, so skipping here is the intended, safe outcome.',
    )

    const row = subscriptionRow(page, linked!.name)
    await expect(row.getByText('Directory connected', { exact: true })).toBeVisible()
    await expect(row.getByRole('button', { name: 'Copy sign-in consent link' })).toBeVisible()
    await expect(row.getByRole('button', { name: 'Copy directory consent link' })).toBeVisible()

    const bad = nonBenign(errors)
    expect(bad, `Console errors on Admin > Subscriptions: ${bad.join(' | ')}`).toEqual([])
  })

  test('a subscription row with no connected directory shows neither consent-link button', async ({ page }) => {
    const tenants = await fetchTenants(page)
    // Scoped to the NS ('UI_') fixture namespace for the same reason as the sibling test:
    // a real customer tenant without a connected directory is not this suite's business.
    const unlinked = tenants.find(t => t.name.startsWith(NS) && !t.azureAdTenantId)
    test.skip(
      !unlinked,
      `This suite needs a ${NS}-prefixed fixture subscription with no connected directory ` +
      `(expected the seeded ${NS}ConsentUnlinked subscription, id 6) but none was found. ` +
      'It will not test against an unnamespaced (real) tenant on purpose, so skipping here is the intended, safe outcome.',
    )

    const row = subscriptionRow(page, unlinked!.name)
    await expect(row.getByText('Directory connected', { exact: true })).toHaveCount(0)
    await expect(row.getByRole('button', { name: 'Copy sign-in consent link' })).toHaveCount(0)
    await expect(row.getByRole('button', { name: 'Copy directory consent link' })).toHaveCount(0)
  })

  test('each consent-link button copies exactly one bare URL matching the API, and flips its own label on click', async ({ page, context, browserName }) => {
    // clipboard-read is Chromium-only in Playwright; the DOM-only presence/absence checks
    // above already run on every project, so this is the sole place the copy behavior
    // itself is verified.
    test.skip(browserName !== 'chromium', 'navigator.clipboard.readText() permission grant is only supported on Chromium.')

    const tenants = await fetchTenants(page)
    // Scoped to the NS ('UI_') fixture namespace for the same reason as the sibling
    // tests: fetching a real customer tenant's real directory-consent link on a failing
    // run would leak its real directory tenant id and client ids into the HTML report.
    const linked = tenants.find(t => t.name.startsWith(NS) && !!t.azureAdTenantId)
    test.skip(
      !linked,
      `This suite needs a ${NS}-prefixed fixture subscription with a connected directory ` +
      `(expected the seeded ${NS}ConsentLinked subscription, id 5) but none was found. ` +
      'It will not test against an unnamespaced (real) tenant on purpose, so skipping here is the intended, safe outcome.',
    )

    // Ground the expected clipboard contents in the same endpoint the button calls
    // (rather than hardcoding fake ids), so this still passes if the seeded fixture's
    // ids or client-id env values ever change.
    const linkRes = await page.request.get(`/api/tenants/${linked!.id}/directory-consent-link`)
    if (linkRes.status() === 503 || linkRes.status() === 400) {
      const body = await linkRes.json().catch(() => ({}))
      test.skip(
        true,
        `GET /api/tenants/${linked!.id}/directory-consent-link returned ${linkRes.status()}` +
        (body?.error ? ` (${body.error})` : '') +
        ' — consent links are not configured in this environment.',
      )
    }
    expect(linkRes.ok(), `GET directory-consent-link returned ${linkRes.status()}`).toBeTruthy()
    const expected = await linkRes.json() as ConsentLinkResponse

    const expectedSignInClientId = new URL(expected.signInConsentUrl).searchParams.get('client_id')
    const expectedDirectoryClientId = new URL(expected.directoryConsentUrl).searchParams.get('client_id')
    // This is a check on backend config (AzureAd:ClientId vs GraphApi:ClientId), not on
    // the buttons under test, so an environment that happens to configure both the same
    // way skips here rather than failing the whole test — the UI's own client_id
    // inequality is still asserted as a hard check below, after the real clicks.
    test.skip(
      expectedSignInClientId === expectedDirectoryClientId,
      'AzureAd:ClientId and GraphApi:ClientId are configured to the same value in this environment, ' +
      'so the sign-in and directory links cannot be distinguished by client_id; not a UI defect.',
    )

    await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: new URL(page.url()).origin })

    const row = subscriptionRow(page, linked!.name)

    async function poisonClipboard() {
      // Written before every click so a no-op copy cannot pass by leaving a stale,
      // previously-correct value on the clipboard.
      await page.evaluate(() => navigator.clipboard.writeText('CLIPBOARD-NOT-WRITTEN'))
    }

    function assertBareConsentUrl(clip: string, expectedUrl: string) {
      // A single line: the old block joined two links (plus numbering and prose) with
      // newlines, so any '\n' means it regressed.
      expect(clip).not.toContain('\n')
      expect(clip.startsWith('https://login.microsoftonline.com/')).toBe(true)
      // Exactly one https:// occurrence. The encoded redirect_uri contributes
      // "http%3A%2F%2F", not a second "https://" — so a count of 1 here means there is
      // only one URL on the clipboard, not two joined together.
      expect(clip.match(/https:\/\//g)?.length).toBe(1)
      // No "1. "/"2. " list marker and none of the old label prose.
      expect(clip).not.toMatch(PROSE_OR_LIST_MARKER)

      const url = new URL(clip)
      expect(url.pathname).toBe(`/${expected.directoryTenantId}/adminconsent`)
      expect(clip).toContain('redirect_uri=')
      expect(clip).toMatch(/redirect_uri=[^&]*%3A%2F%2F/)
      expect(url.searchParams.get('redirect_uri')).toBe(expected.redirectUri)

      // Exact match against the API's own response is the strongest check: it fails if
      // the UI ever wraps this same URL in surrounding prose again, even though the API
      // field would still be present somewhere inside the clipboard string.
      expect(clip).toBe(expectedUrl)
    }

    await poisonClipboard()
    await row.getByRole('button', { name: 'Copy sign-in consent link' }).click()
    await expect(row.getByRole('button', { name: 'Sign-in link copied' })).toBeVisible()
    const signInClip = await page.evaluate(() => navigator.clipboard.readText())
    assertBareConsentUrl(signInClip, expected.signInConsentUrl)
    expect(new URL(signInClip).searchParams.get('client_id')).toBe(expectedSignInClientId)

    // The label reverts after ~2.5s; wait it out so the second click's own "copied"
    // label change isn't ambiguous with a leftover from the first.
    await expect(row.getByRole('button', { name: 'Copy sign-in consent link' })).toBeVisible({ timeout: 10000 })

    await poisonClipboard()
    await row.getByRole('button', { name: 'Copy directory consent link' }).click()
    await expect(row.getByRole('button', { name: 'Directory link copied' })).toBeVisible()
    const directoryClip = await page.evaluate(() => navigator.clipboard.readText())
    assertBareConsentUrl(directoryClip, expected.directoryConsentUrl)
    expect(new URL(directoryClip).searchParams.get('client_id')).toBe(expectedDirectoryClientId)

    // The two buttons must produce different client_ids — otherwise "both" links are
    // really the same link twice, which defeats the point of having two buttons.
    expect(new URL(directoryClip).searchParams.get('client_id'))
      .not.toBe(new URL(signInClip).searchParams.get('client_id'))
  })
})
