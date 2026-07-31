import type { IAuthProvider } from './authProvider'
import type { AuthUser, AuthResult, AuthProviderType } from './types'

const API_BASE = '/api'

/** Treat tokens as expired this many seconds before their actual expiry. */
const EXPIRY_LEEWAY_SECONDS = 60

interface LoginResponse {
  accessToken: string
  id: string
  name: string
  email: string
}

/**
 * Auth provider for local database accounts.
 *
 * Users authenticate with email + password against the backend's
 * LocalAuthController. The backend issues a self-contained JWT
 * signed with a symmetric key, which is then used as the bearer
 * token for subsequent API calls.
 *
 * Local accounts are created by administrators (requires Admin.Full).
 */
export class LocalAuthProvider implements IAuthProvider {
  private user: AuthUser | null = null
  private accessToken: string | null = null

  getProviderType(): AuthProviderType {
    return 'local'
  }

  async init(): Promise<void> {
    // Restore session from storage
    const storedToken = sessionStorage.getItem('accessToken')
    const storedUser = sessionStorage.getItem('localUser')
    const storedProvider = sessionStorage.getItem('authProvider')

    if (storedToken && storedUser && storedProvider === 'local') {
      this.accessToken = storedToken
      try {
        this.user = JSON.parse(storedUser) as AuthUser
      } catch {
        sessionStorage.removeItem('localUser')
        sessionStorage.removeItem('accessToken')
        sessionStorage.removeItem('authProvider')
      }
    }
  }

  async signIn(email?: string, password?: string): Promise<AuthResult | null> {
    // For local auth, signIn requires credentials
    if (!email || !password) {
      console.error('[LocalAuth] signIn requires email and password')
      return null
    }

    try {
      const res = await fetch(`${API_BASE}/auth/local/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password }),
      })

      if (!res.ok) {
        const errorText = await res.text()
        throw new Error(errorText || `Login failed: ${res.status}`)
      }

      const data = (await res.json()) as LoginResponse

      this.accessToken = data.accessToken

      const user: AuthUser = {
        id: data.id,
        name: data.name,
        email: data.email,
        provider: 'local',
        raw: {},
      }

      this.user = user

      // Persist
      sessionStorage.setItem('accessToken', data.accessToken)
      sessionStorage.setItem('localUser', JSON.stringify(user))
      sessionStorage.setItem('authProvider', 'local')

      return {
        provider: 'local',
        accessToken: data.accessToken,
        account: user,
      }
    } catch (error) {
      console.error('[LocalAuth] Login failed:', error)
      throw error
    }
  }

  async signOut(): Promise<void> {
    this.user = null
    this.accessToken = null
    sessionStorage.removeItem('accessToken')
    sessionStorage.removeItem('localUser')
    sessionStorage.removeItem('authProvider')
  }

  async getAccessToken(): Promise<string | null> {
    if (this.accessToken && !this.isTokenExpired(this.accessToken)) {
      return this.accessToken
    }
    // Token missing or near expiry — try a refresh (returns null if impossible)
    return this.refreshToken()
  }

  /**
   * Get a fresh token silently. Local JWTs are self-contained and issued by
   * the backend with no refresh endpoint, so the best we can do is reuse the
   * current token while valid and return null once it has expired (caller
   * must re-authenticate).
   */
  async refreshToken(): Promise<string | null> {
    if (this.accessToken && !this.isTokenExpired(this.accessToken)) {
      return this.accessToken
    }
    return null
  }

  isAuthenticated(): boolean {
    return this.user !== null && this.accessToken !== null
  }

  getCurrentUser(): AuthUser | null {
    return this.user
  }

  /**
   * Whether a token is at or near expiry. Non-JWT tokens or tokens without
   * a usable `exp` claim are treated as expired so callers attempt a refresh.
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
      const decoded = atob(payload.replace(/-/g, '+').replace(/_/g, '/'))
      return JSON.parse(decoded) as Record<string, unknown>
    } catch {
      return null
    }
  }

  /** Register a new local account (admin-only operation) */
  async register(
    email: string,
    password: string,
    name: string,
  ): Promise<void> {
    const accessToken = sessionStorage.getItem('accessToken')
    const res = await fetch(`${API_BASE}/auth/local/register`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
      },
      body: JSON.stringify({ email, password, name }),
    })

    if (!res.ok) {
      const errorText = await res.text()
      throw new Error(errorText || `Registration failed: ${res.status}`)
    }
  }
}
