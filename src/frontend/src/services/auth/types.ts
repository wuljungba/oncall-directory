/** Supported authentication provider types */
export type AuthProviderType = 'microsoft' | 'google' | 'local'

/** Normalized user account from any auth provider */
export interface AuthUser {
  id: string
  name: string
  email: string
  provider: AuthProviderType
  /** Provider-specific raw user data */
  raw: Record<string, unknown>
}

/** Result returned by signIn() across all providers */
export interface AuthResult {
  provider: AuthProviderType
  /** The primary access token to send to the backend API */
  accessToken: string
  /** The normalized user account */
  account: AuthUser
  /** Provider-specific ID token (id_token from OIDC) */
  idToken?: string
}
