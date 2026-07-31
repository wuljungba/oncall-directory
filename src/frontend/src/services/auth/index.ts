/**
 * Authentication module — multi-provider auth abstraction.
 *
 * Supports Microsoft Entra ID (MSAL), Google OAuth, and local accounts.
 * Use getAuthProvider() to get the currently active provider, or
 * call the convenience functions below which delegate automatically.
 *
 * import { signIn, signOut, getAccessToken } from '@/services/auth'
 */

export { getAuthProvider, getAllProviders, clearProviders, getActiveProviderType } from './authFactory'
export { MicrosoftAuthProvider } from './microsoftAuthProvider'
export { GoogleAuthProvider } from './googleAuthProvider'
export { LocalAuthProvider } from './localAuthProvider'
export type { IAuthProvider } from './authProvider'
export type { AuthUser, AuthResult, AuthProviderType } from './types'
