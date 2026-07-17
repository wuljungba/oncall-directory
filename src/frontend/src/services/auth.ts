import {
  PublicClientApplication,
  type AccountInfo,
  type InteractionRequiredAuthError,
  InteractionStatus,
} from '@azure/msal-browser'

const MSAL_CONFIG = {
  auth: {
    clientId: import.meta.env.VITE_AZURE_CLIENT_ID || 'your-api-client-id',
    authority: `https://login.microsoftonline.com/${
      import.meta.env.VITE_AZURE_TENANT_ID || 'your-tenant-id'
    }`,
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

export const msalInstance = new PublicClientApplication(MSAL_CONFIG)

// Initialize MSAL
export async function initAuth(): Promise<void> {
  await msalInstance.initialize()
  const accounts = msalInstance.getAllAccounts()
  if (accounts.length > 0) {
    msalInstance.setActiveAccount(accounts[0])
  }
}

export async function signIn(): Promise<AccountInfo | null> {
  try {
    const response = await msalInstance.loginPopup(LOGIN_REQUEST)
    msalInstance.setActiveAccount(response.account)

    // Get access token for backend API
    const tokenResponse = await msalInstance.acquireTokenSilent({
      ...TOKEN_REQUEST,
      account: response.account,
    })
    sessionStorage.setItem('accessToken', tokenResponse.accessToken)

    return response.account
  } catch (error) {
    console.error('Login failed:', error)
    return null
  }
}

export async function signOut(): Promise<void> {
  sessionStorage.removeItem('accessToken')
  await msalInstance.logoutPopup()
}

export async function getAccessToken(): Promise<string | null> {
  try {
    const account = msalInstance.getActiveAccount()
    if (!account) return null

    const response = await msalInstance.acquireTokenSilent({
      ...TOKEN_REQUEST,
      account,
    })
    sessionStorage.setItem('accessToken', response.accessToken)
    return response.accessToken
  } catch (error) {
    // Fall back to popup if silent fails
    const errorObj = error as InteractionRequiredAuthError
    if (errorObj.name === 'InteractionRequiredAuthError') {
      return signInPopup()
    }
    return null
  }
}

async function signInPopup(): Promise<string | null> {
  try {
    const response = await msalInstance.loginPopup(TOKEN_REQUEST)
    sessionStorage.setItem('accessToken', response.accessToken)
    return response.accessToken
  } catch {
    return null
  }
}

export function isAuthenticated(): boolean {
  return msalInstance.getActiveAccount() !== null
}

export function getCurrentUser(): AccountInfo | null {
  return msalInstance.getActiveAccount()
}
