import type { IAuthProvider } from './authProvider'
import type { AuthUser, AuthResult, AuthProviderType } from './types'
import {
  PublicClientApplication,
  type InteractionRequiredAuthError,
} from '@azure/msal-browser'

const MSAL_CONFIG = {
  auth: {
    clientId: import.meta.env.VITE_AZURE_CLIENT_ID || 'your-api-client-id',
    authority: 'https://login.microsoftonline.com/organizations',
    redirectUri: window.location.origin,
  },
  cache: {
    cacheLocation: 'sessionStorage',
    storeAuthStateInCookie: false,
  },
}

const LOGIN_REQUEST = {
  scopes: [
    'User.Read',
    'User.ReadBasic.All',
    'Calendars.ReadWrite',
    'Presence.Read.All',
    'OnlineMeetings.ReadWrite',
  ],
}

const TOKEN_REQUEST = {
  scopes: [
    `api://${MSAL_CONFIG.auth.clientId}/access_as_user`,
    'https://graph.microsoft.com/User.Read',
  ],
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

  /**
   * Expose the MSAL instance for use by MsalProvider in main.tsx.
   */
  getMsalInstance(): PublicClientApplication {
    return this.msalInstance
  }

  getProviderType(): AuthProviderType {
    return 'microsoft'
  }

  async init(): Promise<void> {
    if (this.initialized) return

    await this.msalInstance.initialize()
    const accounts = this.msalInstance.getAllAccounts()
    if (accounts.length > 0) {
      this.msalInstance.setActiveAccount(accounts[0])
    }

    // Check for existing token in sessionStorage
    const storedProvider = sessionStorage.getItem('authProvider')
    if (storedProvider === 'microsoft' && accounts.length > 0) {
      // Try refreshing the token silently
      try {
        const tokenResponse = await this.msalInstance.acquireTokenSilent({
          ...TOKEN_REQUEST,
          account: accounts[0],
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
      const response = await this.msalInstance.loginPopup(LOGIN_REQUEST)
      this.msalInstance.setActiveAccount(response.account)

      // Get access token for backend API
      const tokenResponse = await this.msalInstance.acquireTokenSilent({
        ...TOKEN_REQUEST,
        account: response.account,
      })
      sessionStorage.setItem('accessToken', tokenResponse.accessToken)
      sessionStorage.setItem('authProvider', 'microsoft')

      const user: AuthUser = {
        id: response.account.localAccountId || response.account.homeAccountId,
        name: response.account.name || response.account.username || '',
        email: response.account.username || '',
        provider: 'microsoft',
        raw: response.account as unknown as Record<string, unknown>,
      }

      return {
        provider: 'microsoft',
        accessToken: tokenResponse.accessToken,
        account: user,
        idToken: response.idToken,
      }
    } catch (error) {
      console.error('[MicrosoftAuth] Login failed:', error)
      return null
    }
  }

  async signOut(): Promise<void> {
    sessionStorage.removeItem('accessToken')
    sessionStorage.removeItem('authProvider')
    try {
      await this.msalInstance.logoutPopup()
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
        return this.signInPopup()
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

  private async signInPopup(): Promise<string | null> {
    try {
      const response = await this.msalInstance.loginPopup(TOKEN_REQUEST)
      // Track the account so subsequent silent renewals find an active account
      // instead of falling back to repeated popups.
      this.msalInstance.setActiveAccount(response.account)
      sessionStorage.setItem('accessToken', response.accessToken)
      return response.accessToken
    } catch {
      return null
    }
  }
}
