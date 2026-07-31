import type { AuthUser, AuthResult, AuthProviderType } from './types'

/**
 * Abstract interface for authentication providers.
 * Implemented by Microsoft, Google, and Local auth strategies.
 */
export interface IAuthProvider {
  /** Initialize the provider (load persisted sessions, etc.) */
  init(): Promise<void>

  /** Sign in — may show a popup, redirect, or prompt for credentials */
  signIn(email?: string, password?: string): Promise<AuthResult | null>

  /** Sign out — clear tokens and sessions */
  signOut(): Promise<void>

  /** Get the current access token (refresh if needed) */
  getAccessToken(): Promise<string | null>

  /**
   * Obtain a fresh access token silently, without user interaction.
   * Returns the current token if it is still valid, or null if a fresh
   * token cannot be obtained silently (callers should re-authenticate).
   */
  refreshToken(): Promise<string | null>

  /** Whether the user is currently authenticated */
  isAuthenticated(): boolean

  /** Get the current user, or null if not signed in */
  getCurrentUser(): AuthUser | null

  /** Get the provider type identifier */
  getProviderType(): AuthProviderType
}
