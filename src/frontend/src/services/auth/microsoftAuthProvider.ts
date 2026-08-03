import type { IAuthProvider } from './authProvider'
import type { AuthUser, AuthResult, AuthProviderType } from './types'
import {
  PublicClientApplication,
  type InteractionRequiredAuthError,
} from '@azure/msal-browser'

const MSAL_CONFIG = {
  auth: {
    clientId: import.meta.env.VITE_AZURE_CLIENT_ID || 'your-spa-client-id',
    // Tenant-specific authority. The "OnCall API" app is single-tenant
    // (AzureADMyOrg), and its admins are B2B guests of the hosting tenant.
    // The multi-tenant "organizations" authority fails for guest (B2B)
    // sign-in (routes to login.microsoftonline.com/login.srf), so scope the
    // authority to the app's own tenant where the guest account lives.
    authority:
      import.meta.env.VITE_AZURE_AUTHORITY
      || 'https://login.microsoftonline.com/24b3700e-7053-4498-a4e6-b8ebf85dc38c',
    redirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: 'sessionStorage',
    storeAuthStateInCookie: false,
  },
}

// The SPA only talks to the OnCall API — every Microsoft Graph call happens on
// the backend with app-only credentials. So both the interactive login and the
// silent token requests use ONLY the API audience scope. Mixing in Graph scopes
// would produce a token with a single (mismatched) audience and fail validation,
// and omitting the API scope from the interactive request would force an
// InteractionRequiredAuthError on the follow-up silent call.
const API_SCOPE = `api://${MSAL_CONFIG.auth.clientId}/access_as_user`

const LOGIN_REQUEST = {
  scopes: [API_SCOPE],
}

const TOKEN_REQUEST = {
  scopes: [API_SCOPE],
}

/**
 * Auth provider for Microsoft Entra ID (Azure AD) via MSAL.js.
 *
 * Uses the OAuth 2.0 authorization code flow with PKCE.
 * Tokens are validated by the backend using Microsoft.Identity.Web.
 */
export class MicrosoftAuthProvider implements IAuthProvider {
  private msalInstance: PublicClientApplication
  private initialized = false

  constructor() {
    this.msalInstance = new PublicClientApplication(MSAL_CONFIG)
  }

  getProviderType(): AuthProviderType {
    return 'microsoft'
  }

  async init(): Promise<void> {
    if (this.initialized) return

    await this.msalInstance.initialize()

    // Process an in-flight redirect (login/logout redirect returns here). The
    // redirect flow is used instead of popup because popup sign-in is fragile on
    // hosted sites (popup blockers, Cross-Origin-Opener-Policy, cookie policies).
    try {
      const redirectResp = await this.msalInstance.handleRedirectPromise()
      if (redirectResp?.account) {
        this.msalInstance.setActiveAccount(redirectResp.account)
        if (redirectResp.accessToken) sessionStorage.setItem('accessToken', redirectResp.accessToken)
        sessionStorage.setItem('authProvider', 'microsoft')
      }
    } catch (err) {
      console.error('[MicrosoftAuth] handleRedirectPromise failed:', err)
    }

    const accounts = this.msalInstance.getAllAccounts()
    if (accounts.length > 0 && !this.msalInstance.getActiveAccount()) {
      this.msalInstance.setActiveAccount(accounts[0])
    }
    const account = this.msalInstance.getActiveAccount()

    // Restore/refresh a session from the cache (readiness from a redirect or reload)
    const storedProvider = sessionStorage.getItem('authProvider')
    if (storedProvider === 'microsoft' && account) {
      try {
        const tokenResponse = await this.msalInstance.acquireTokenSilent({
          ...TOKEN_REQUEST,
          account,
        })
        sessionStorage.setItem('accessToken', tokenResponse.accessToken)
      } catch {
        // Silent refresh failed — user will need to re-auth
        sessionStorage.removeItem('accessToken')
      }
    }

    this.initialized = true
  }

  async signIn(): Promise<AuthResult | null> {
    try {
      // Redirect flow: send the user to Microsoft full-page, returning to the
      // current page. On return, init()/handleRedirectPromise restores the
      // account and useAuth flips to authenticated.
      await this.msalInstance.loginRedirect({
        ...LOGIN_REQUEST,
        // Return to wherever the user was (e.g. /login), which then routes to /dashboard.
        redirectStartPage: window.location.href,
      })
    } catch (error) {
      console.error('[MicrosoftAuth] Login failed:', error)
      return null
    }
    // Account is restored by handleRedirectPromise on the return trip.
    return null
  }

  async signOut(): Promise<void> {
    sessionStorage.removeItem('accessToken')
    sessionStorage.removeItem('authProvider')
    try {
      await this.msalInstance.logoutRedirect({
        postLogoutRedirectUri: window.location.origin,
      })
    } catch {
      // Ignore logout errors
    }
  }

  async getAccessToken(): Promise<string | null> {
    const account = this.msalInstance.getActiveAccount()
    if (!account) return null

    try {
      const response = await this.msalInstance.acquireTokenSilent({
        ...TOKEN_REQUEST,
        account,
      })
      sessionStorage.setItem('accessToken', response.accessToken)
      return response.accessToken
    } catch (error) {
      // Only prompt interactively when the silent attempt specifically requires
      // interaction. Any other failure (no account, network, etc.) returns null
      // so callers do not get an unexpected login popup from a background request.
      const err = error as InteractionRequiredAuthError
      if (err.name === 'InteractionRequiredAuthError') {
        // Interactive re-auth is required — trigger a redirect login.
        this.msalInstance
          .loginRedirect({ ...TOKEN_REQUEST, redirectStartPage: window.location.href })
          .catch(() => {})
      }
      return null
    }
  }

  /**
   * Obtain a fresh access token silently via MSAL. Returns null if the
   * silent request fails (e.g. interaction required) so the caller can
   * decide whether to prompt the user. Never opens a popup.
   */
  async refreshToken(): Promise<string | null> {
    const account = this.msalInstance.getActiveAccount()
    if (!account) return null

    try {
      const response = await this.msalInstance.acquireTokenSilent({
        ...TOKEN_REQUEST,
        account,
      })
      sessionStorage.setItem('accessToken', response.accessToken)
      return response.accessToken
    } catch {
      return null
    }
  }

  isAuthenticated(): boolean {
    return this.msalInstance.getActiveAccount() !== null
  }

  getCurrentUser(): AuthUser | null {
    const account = this.msalInstance.getActiveAccount()
    if (!account) return null

    return {
      id: account.localAccountId || account.homeAccountId,
      name: account.name || account.username || '',
      email: account.username || '',
      provider: 'microsoft',
      raw: account as unknown as Record<string, unknown>,
    }
  }

  }
