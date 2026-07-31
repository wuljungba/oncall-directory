import type { IAuthProvider } from './authProvider'
import type { AuthUser, AuthResult, AuthProviderType } from './types'
import type { CredentialResponse } from '@react-oauth/google'

const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID || ''

/** Treat tokens as expired this many seconds before their actual expiry. */
const EXPIRY_LEEWAY_SECONDS = 60

/**
 * Auth provider for Google (Gmail) OAuth via Google Identity Services.
 *
 * Uses the credential-based flow (Sign In With Google):
 * 1. User clicks "Sign in with Google"
 * 2. GIS shows the One Tap / sign-in popup
 * 3. Google returns a credential (JWT ID token)
 * 4. The credential is sent to the backend for validation
 * 5. Backend validates signature + audience + issuer and creates a session
 *
 * The backend accepts Google JWTs as valid bearer tokens, validated against
 * Google's public JWKS endpoint.
 */
export class GoogleAuthProvider implements IAuthProvider {
  private user: AuthUser | null = null
  private accessToken: string | null = null
  /** In-flight silent refresh, deduplicated so parallel API calls share one request. */
  private refreshPromise: Promise<string | null> | null = null

  getProviderType(): AuthProviderType {
    return 'google'
  }

  async init(): Promise<void> {
    if (!GOOGLE_CLIENT_ID) {
      console.warn('[GoogleAuth] VITE_GOOGLE_CLIENT_ID not configured')
      return
    }

    // Restore session from storage
    const storedCredential = sessionStorage.getItem('googleCredential')
    const storedUser = sessionStorage.getItem('googleUser')
    if (storedCredential && storedUser) {
      this.accessToken = storedCredential
      try {
        this.user = JSON.parse(storedUser) as AuthUser
      } catch {
        sessionStorage.removeItem('googleCredential')
        sessionStorage.removeItem('googleUser')
      }
    }
  }

  async signIn(): Promise<AuthResult | null> {
    if (!GOOGLE_CLIENT_ID) {
      console.error('[GoogleAuth] Google client ID not configured. Set VITE_GOOGLE_CLIENT_ID')
      return null
    }

    // GIS credential-based sign-in uses the popup/redirect flow
    return new Promise((resolve) => {
      try {
        // Use Google Identity Services credential flow
        google.accounts.id.initialize({
          client_id: GOOGLE_CLIENT_ID,
          callback: (response: CredentialResponse) => {
            if (!response.credential) {
              console.error('[GoogleAuth] No credential returned')
              resolve(null)
              return
            }

            // Decode the JWT to extract user info
            const payload = this.decodeJwt(response.credential)
            if (!payload) {
              resolve(null)
              return
            }

            this.accessToken = response.credential

            const user: AuthUser = {
              id: (payload.sub as string) || '',
              name: (payload.name as string) || (payload.email as string) || '',
              email: (payload.email as string) || '',
              provider: 'google',
              raw: payload as Record<string, unknown>,
            }

            this.user = user

            // Persist to sessionStorage
            sessionStorage.setItem('googleCredential', response.credential)
            sessionStorage.setItem('googleUser', JSON.stringify(user))
            sessionStorage.setItem('accessToken', response.credential)
            sessionStorage.setItem('authProvider', 'google')

            resolve({
              provider: 'google',
              accessToken: response.credential,
              idToken: response.credential,
              account: user,
            })
          },
        })

        // Trigger the sign-in prompt
        google.accounts.id.prompt((notification: any) => {
          if (notification.isNotDisplayed() || notification.isSkippedMoment()) {
            // If One Tap is not displayed (e.g., 3rd-party cookies blocked),
            // fall back to the standard button flow
            google.accounts.id.renderButton(
              document.createElement('div'),
              { type: 'standard', shape: 'rectangular' },
            )
          }
        })
      } catch (err) {
        console.error('[GoogleAuth] signIn failed:', err)
        resolve(null)
      }
    })
  }

  async signOut(): Promise<void> {
    this.user = null
    this.accessToken = null

    sessionStorage.removeItem('googleCredential')
    sessionStorage.removeItem('googleUser')
    sessionStorage.removeItem('accessToken')
    sessionStorage.removeItem('authProvider')

    // Sign out of Google
    try {
      google.accounts.id.disableAutoSelect()
    } catch {
      // Best-effort
    }
  }

  async getAccessToken(): Promise<string | null> {
    if (this.accessToken && !this.isTokenExpired(this.accessToken)) {
      return this.accessToken
    }
    // Token missing or near expiry — try a silent refresh
    return this.refreshToken()
  }

  /**
   * Get a fresh credential silently.
   *
   * Google Identity Services has no true client-side refresh token in the
   * credential flow, so "refresh" means asking Google for a new token without
   * showing a popup (prompt: ''). If the user's Google session is still
   * active this resolves silently; otherwise it returns null and the caller
   * should fall back to interactive sign-in.
   */
  async refreshToken(): Promise<string | null> {
    // Reuse the current token while it's still valid
    if (this.accessToken && !this.isTokenExpired(this.accessToken)) {
      return this.accessToken
    }
    if (!this.user || !GOOGLE_CLIENT_ID) return null

    // Single-flight: parallel API calls can all hit refresh around expiry;
    // dedupe them into one silent GIS request instead of several.
    if (!this.refreshPromise) {
      this.refreshPromise = this.performSilentRefresh().finally(() => {
        this.refreshPromise = null
      })
    }
    return this.refreshPromise
  }

  private performSilentRefresh(): Promise<string | null> {
    const hint = this.user?.email
    return new Promise((resolve) => {
      try {
        const client = google.accounts.oauth2.initTokenClient({
          client_id: GOOGLE_CLIENT_ID,
          scope: 'openid email profile',
          hint,
          include_granted_scopes: true,
          prompt: '', // empty prompt = silent attempt, no popup
          callback: (response) => {
            if (response.error || !response.id_token) {
              resolve(null)
              return
            }
            this.accessToken = response.id_token
            // Keep provider and shared storage in sync
            sessionStorage.setItem('googleCredential', response.id_token)
            sessionStorage.setItem('accessToken', response.id_token)
            resolve(response.id_token)
          },
          error_callback: () => resolve(null),
        })
        client.requestAccessToken({ prompt: '' })
      } catch (err) {
        console.error('[GoogleAuth] Silent refresh failed:', err)
        resolve(null)
      }
    })
  }

  isAuthenticated(): boolean {
    return this.user !== null && this.accessToken !== null
  }

  getCurrentUser(): AuthUser | null {
    return this.user
  }

  /**
   * Whether a token is at or near expiry. Tokens without a usable `exp`
   * claim are treated as expired so we attempt a silent refresh.
   */
  private isTokenExpired(token: string, leewaySeconds: number = EXPIRY_LEEWAY_SECONDS): boolean {
    const payload = this.decodeJwt(token)
    if (!payload || typeof payload.exp !== 'number') return true
    return payload.exp * 1000 - leewaySeconds * 1000 <= Date.now()
  }

  /**
   * Decode a JWT payload without verification (client-side).
   * Server-side validation happens on the backend.
   */
  private decodeJwt(token: string): Record<string, unknown> | null {
    try {
      const parts = token.split('.')
      if (parts.length !== 3) return null
      const payload = parts[1]
      // Base64url decode
      const decoded = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
      return JSON.parse(decoded) as Record<string, unknown>
    } catch {
      return null
    }
  }
}
