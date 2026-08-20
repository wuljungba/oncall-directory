import { useState, useEffect, useCallback, useRef, createContext, useContext } from 'react'
import { authApi } from '@/services/api'
import { getAuthProvider, getActiveProviderType } from '@/services/auth'
import type { AuthUser, AuthProviderType } from '@/services/auth'

const DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true'

interface AuthState {
  isLoading: boolean
  isAuthenticated: boolean
  user: AuthUser | null
  authProvider: AuthProviderType | null
  signIn: (provider?: AuthProviderType, email?: string, password?: string) => Promise<void>
  signOut: () => Promise<void>
  refreshToken: () => Promise<string | null>
  userRoles: string[]

  // Granular permissions (from backend /api/auth/me)
  isAdmin: boolean
  canSchedule: boolean
  permissions: string[]
  canScheduleRead: boolean
  canScheduleWrite: boolean
  canDirectoryRead: boolean
  canDirectoryWrite: boolean
  canAdminFull: boolean
  canAdminScoped: boolean
  canTenantManage: boolean

  // Linked profile
  employeeId: string | null

  // Tenant context
  tenantIds: number[]
  tenantRoles: Record<string, string>
  activeTenantId: number | null
  setActiveTenantId: (id: number | null) => void
}

const ALL_PERMISSIONS = [
  'Schedule.Read', 'Schedule.Write',
  'Directory.Read', 'Directory.Write',
  'Admin.Full',
]

/**
 * Owns the single authentication state for the app.
 *
 * This is deliberately NOT exported: sign-in state is created once by
 * <AuthProvider> and read through useAuth(). When every component ran this hook
 * for itself, a page load fired a dozen parallel /api/auth/me calls and
 * components could transiently disagree about isLoading and permissions.
 */
function useAuthState(): AuthState {
  const [isLoading, setIsLoading] = useState(DEV_AUTH ? false : true)
  const [user, setUser] = useState<AuthUser | null>(
    DEV_AUTH ? { id: 'dev', name: 'dev@local', email: 'dev@local', provider: 'microsoft', raw: {} } as AuthUser : null,
  )
  const [authProvider, setAuthProvider] = useState<AuthProviderType | null>(
    DEV_AUTH ? 'microsoft' : null,
  )
  const [permissions, setPermissions] = useState<string[]>(DEV_AUTH ? ALL_PERMISSIONS : [])
  const [tenantIds, setTenantIds] = useState<number[]>([])
  const [tenantRoles, setTenantRoles] = useState<Record<string, string>>({})
  const [employeeId, setEmployeeId] = useState<string | null>(null)
  const [activeTenantId, setActiveTenantIdState] = useState<number | null>(() => {
    const stored = sessionStorage.getItem('activeTenantId')
    return stored ? Number(stored) : null
  })

  // Mirrors activeTenantId so handleAuthResponse can read the current value without
  // taking it as a dependency. Without this the bootstrap effect below re-ran (and
  // re-fetched the session) every time the active tenant changed.
  const activeTenantIdRef = useRef(activeTenantId)

  const handleSetActiveTenantId = useCallback((id: number | null) => {
    activeTenantIdRef.current = id
    setActiveTenantIdState(id)
    if (id !== null) {
      sessionStorage.setItem('activeTenantId', String(id))
    } else {
      sessionStorage.removeItem('activeTenantId')
    }
  }, [])

  // Shared handler for processing auth/me response
  const handleAuthResponse = useCallback((res: { permissions: string[]; tenantIds?: number[]; tenantRoles?: Record<string, string>; employeeId?: string | null }) => {
    setPermissions(res.permissions)
    if (res.tenantIds) setTenantIds(res.tenantIds)
    if (res.tenantRoles) setTenantRoles(res.tenantRoles)
    setEmployeeId(res.employeeId ?? null)

    // Auto-set activeTenantId for scoped admins with only one tenant
    const hasScoped = res.permissions.includes('Admin.Scoped')
    const hasFull = res.permissions.includes('Admin.Full')
    if (hasScoped && !hasFull && res.tenantIds?.length === 1 && activeTenantIdRef.current === null) {
      handleSetActiveTenantId(res.tenantIds[0])
    }
  }, [handleSetActiveTenantId])

  // Bootstrapping the session is a once-per-load job: initialize the auth provider,
  // restore any existing session, then load permissions. The guard keeps React
  // StrictMode's development double-invoke from running MSAL init and /api/auth/me
  // twice on every page load.
  const didBootstrap = useRef(false)

  useEffect(() => {
    if (didBootstrap.current) return
    didBootstrap.current = true

    if (DEV_AUTH) {
      // Fetch permissions from backend to respect role-switching
      authApi.me()
        .then(res => handleAuthResponse(res))
        .catch(() => {
          // Fallback to full access if backend not reachable
          setPermissions(ALL_PERMISSIONS)
        })
      setIsLoading(false)
      return
    }

    // Initialize the active provider and check for existing session
    const provider = getAuthProvider()
    const providerType = getActiveProviderType()
    setAuthProvider(providerType)

    provider.init()
      .then(async () => {
        const currentUser = provider.getCurrentUser()
        setUser(currentUser)

        // Keep isLoading true until BOTH identity and permissions are known.
        // AdminRoute/ProtectedRoute guards must never render (and possibly
        // redirect) on unloaded permissions — otherwise a cold load of /admin
        // bounces to /dashboard before /api/auth/me flips isAdmin to true.
        try {
          if (currentUser) {
            // Fetch permissions from the /api/auth/me endpoint
            const res = await authApi.me()
            handleAuthResponse(res)
          }
        } catch {
          setPermissions([])
        } finally {
          setIsLoading(false)
        }
      })
      .catch((err) => {
        console.error('[useAuth] Auth init failed:', err)
        setIsLoading(false)
      })
  }, [handleAuthResponse])

  const signIn = useCallback(async (providerType?: AuthProviderType, email?: string, password?: string) => {
    if (DEV_AUTH) {
      setIsLoading(true)
      setUser({ id: 'dev', name: 'dev@local', email: 'dev@local', provider: 'microsoft', raw: {} })
      setAuthProvider('microsoft')
      // Re-fetch permissions after sign-in (role may have changed)
      authApi.me()
        .then(res => handleAuthResponse(res))
        .catch(() => setPermissions(ALL_PERMISSIONS))
      setIsLoading(false)
      return
    }

    setIsLoading(true)
    try {
      // If a specific provider type is requested, get that one
      const provider = providerType
        ? getAuthProvider(providerType)
        : getAuthProvider()

      const result = await provider.signIn(email, password)
      if (result) {
        setUser(result.account)
        setAuthProvider(result.account.provider)
        // Fetch permissions from backend
        authApi.me()
          .then(res => handleAuthResponse(res))
          .catch(() => setPermissions([]))
      }
    } catch (error) {
      console.error('[useAuth] Sign in failed:', error)
    }
    setIsLoading(false)
  }, [handleAuthResponse])

  const signOut = useCallback(async () => {
    const provider = getAuthProvider()
    await provider.signOut()
    setUser(null)
    setAuthProvider(null)
    setPermissions([])
    setTenantIds([])
    setTenantRoles({})
    handleSetActiveTenantId(null)
  }, [handleSetActiveTenantId])

  const refreshToken = useCallback(async (): Promise<string | null> => {
    if (DEV_AUTH) return sessionStorage.getItem('accessToken')

    try {
      return await getAuthProvider().refreshToken()
    } catch (err) {
      console.error('[useAuth] Token refresh failed:', err)
      return null
    }
  }, [])

  // Extract roles from permissions
  const devRoles = ['OnCall.Viewer', 'OnCall.Scheduler', 'OnCall.Admin']
  const rawRoles = DEV_AUTH ? devRoles : permissions

  const perms = permissions
  return {
    isLoading,
    isAuthenticated: DEV_AUTH ? true : user !== null,
    user,
    authProvider,
    signIn,
    signOut,
    refreshToken,
    userRoles: rawRoles,
    isAdmin: perms.includes('Admin.Full'),
    canSchedule: perms.includes('Schedule.Write'),
    permissions: perms,
    canScheduleRead: perms.includes('Schedule.Read'),
    canScheduleWrite: perms.includes('Schedule.Write'),
    canDirectoryRead: perms.includes('Directory.Read'),
    canDirectoryWrite: perms.includes('Directory.Write'),
    canAdminFull: perms.includes('Admin.Full'),
    canAdminScoped: perms.includes('Admin.Scoped'),
    canTenantManage: perms.includes('Tenant.Manage'),
    employeeId,
    tenantIds,
    tenantRoles,
    activeTenantId,
    setActiveTenantId: handleSetActiveTenantId,
  }
}

const AuthContext = createContext<AuthState | null>(null)

/**
 * Provides the app's authentication state. Mount once, above the router's routes
 * (see main.tsx) — every useAuth() call reads from this instance.
 */
export function AuthProvider({ children }: { children: React.ReactNode }) {
  const auth = useAuthState()
  return <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>
}

/**
 * Reads the shared authentication state.
 *
 * Throws when used outside <AuthProvider> rather than silently spinning up a second,
 * divergent copy of the session.
 */
export function useAuth(): AuthState {
  const auth = useContext(AuthContext)
  if (auth === null) {
    throw new Error('useAuth must be used within an <AuthProvider>')
  }
  return auth
}
