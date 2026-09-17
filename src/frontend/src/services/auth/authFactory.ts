import type { IAuthProvider } from './authProvider'
import type { AuthProviderType } from './types'
import { MicrosoftAuthProvider } from './microsoftAuthProvider'
import { GoogleAuthProvider } from './googleAuthProvider'
import { LocalAuthProvider } from './localAuthProvider'

const providers = new Map<string, IAuthProvider>()

/**
 * Get or create an auth provider by type.
 * Providers are cached once instantiated.
 */
export function getAuthProvider(type?: AuthProviderType): IAuthProvider {
  const providerType: AuthProviderType = type
    ?? (sessionStorage.getItem('authProvider') as AuthProviderType)
    ?? 'microsoft'

  // Check cache
  const cached = providers.get(providerType)
  if (cached) return cached

  // Create new instance
  let provider: IAuthProvider
  switch (providerType) {
    case 'microsoft':
      provider = new MicrosoftAuthProvider()
      break
    case 'google':
      provider = new GoogleAuthProvider()
      break
    case 'local':
      provider = new LocalAuthProvider()
      break
    default:
      throw new Error(`Unknown auth provider type: ${providerType}`)
  }

  providers.set(providerType, provider)
  return provider
}

/**
 * Clear cached providers (useful on sign-out).
 */
export function clearProviders(): void {
  providers.clear()
}

/**
 * Get the active provider (from session storage), fallback to default.
 */
export function getActiveProviderType(): AuthProviderType {
  return (sessionStorage.getItem('authProvider') as AuthProviderType) ?? 'microsoft'
}
