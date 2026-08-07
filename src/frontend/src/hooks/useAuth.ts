import { useState, useEffect, useCallback } from 'react'
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

export function useAuth(): AuthState {
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
  const [activeTenantId, setActiveTenantId] = useState<number | null>(() => {
    const stored = sessionStorage.getItem('activeTenantId')
    return stored ? Number(stored) : null
  })

  const handleSetActiveTenantId = useCallback((id: number | null) => {
    setActiveTenantId(id)
    if (id !== null) {
      sessionStorage.setItem('activeTenantId', String(id))
    } else {
      sessionStorage.removeItem('activeTenantId')
    }
  }, [])

  // Shared handler for processing auth/me response
  const handleAuthResponse = useCallback((res: { permissions: string[]; tenantIds?: number[]; tenantRoles?: Record<string, string> }) => {
    setPermissions(res.permissions)
    if (res.tenantIds) setTenantIds(res.tenantIds)
    if (res.tenantRoles) setTenantRoles(res.tenantRoles)

    // Auto-set activeTenantId for scoped admins with only one tenant
    const hasScoped = res.permissions.includes('Admin.Scoped')
    const hasFull = res.permissions.includes('Admin.Full')
    if (hasScoped && !hasFull && res.tenantIds?.length === 1 && activeTenantId === null) {
      setActiveTenantId(res.tenantIds[0])
      sessionStorage.setItem('activeTenantId', String(res.tenantIds[0]))
    }
  }, [activeTenantId])

  useEffect(() => {
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
    setActiveTenantId(null)
    sessionStorage.removeItem('activeTenantId')
  }, [])

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
    tenantIds,
    tenantRoles,
    activeTenantId,
    setActiveTenantId: handleSetActiveTenantId,
  }
}
