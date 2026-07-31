/**
 * Convenience facade for the active auth provider.
 *
 * All functions delegate to whichever provider is currently active
 * (Microsoft, Google, or Local). This maintains backward compatibility
 * for consumers that import from '@/services/auth'.
 *
 * New code should use getAuthProvider() from '@/services/auth/index' for
 * provider-specific operations.
 */
import { getAuthProvider, getActiveProviderType } from './auth/authFactory'
import { MicrosoftAuthProvider } from './auth/microsoftAuthProvider'
import type { AuthUser, AuthProviderType } from './auth/types'

export { getAuthProvider, getActiveProviderType, MicrosoftAuthProvider }
export type { AuthUser, AuthProviderType }

export async function initAuth(): Promise<void> {
  const provider = getAuthProvider()
  await provider.init()
}

export async function signIn(): Promise<AuthUser | null> {
  const provider = getAuthProvider()
  const result = await provider.signIn()
  return result?.account ?? null
}

export async function signOut(): Promise<void> {
  const provider = getAuthProvider()
  await provider.signOut()
}

export async function getAccessToken(): Promise<string | null> {
  const provider = getAuthProvider()
  return provider.getAccessToken()
}

export async function refreshToken(): Promise<string | null> {
  const provider = getAuthProvider()
  return provider.refreshToken()
}

export function isAuthenticated(): boolean {
  const provider = getAuthProvider()
  return provider.isAuthenticated()
}

export function getCurrentUser(): AuthUser | null {
  const provider = getAuthProvider()
  return provider.getCurrentUser()
}
