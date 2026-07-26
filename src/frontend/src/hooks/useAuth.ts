import { useState, useEffect, useCallback } from 'react'
import type { AccountInfo } from '@azure/msal-browser'
import { authApi } from '@/services/api'

const DEV_AUTH = import.meta.env.VITE_DEV_AUTH === 'true'
import {
  initAuth,
  signIn as msalSignIn,
  signOut as msalSignOut,
  isAuthenticated,
  getCurrentUser,
} from '@/services/auth'

interface AuthState {
  isLoading: boolean
  isAuthenticated: boolean
  user: AccountInfo | null
  signIn: () => Promise<void>
  signOut: () => Promise<void>
  userRoles: string[]
  isAdmin: boolean
  canSchedule: boolean

  // Granular permissions
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
  const [user, setUser] = useState<AccountInfo | null>(DEV_AUTH ? ({ username: 'dev@local' } as AccountInfo) : null)
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
      const fake = { username: 'dev@local' } as AccountInfo
      setUser(fake)
      setIsLoading(false)
      return
    }

    initAuth()
      .then(() => {
        setUser(getCurrentUser())
        // In production, fetch permissions from the /api/auth/me endpoint
        authApi.me()
          .then(res => handleAuthResponse(res))
          .catch(() => setPermissions([]))
        setIsLoading(false)
      })
      .catch((err) => {
        console.error('[useAuth] Auth init failed:', err)
        setIsLoading(false)
      })
  }, [handleAuthResponse])

  const signIn = useCallback(async () => {
    if (DEV_AUTH) {
      setIsLoading(true)
      const fake = { username: 'dev@local' } as AccountInfo
      setUser(fake)
      // Re-fetch permissions after sign-in (role may have changed)
      authApi.me()
        .then(res => handleAuthResponse(res))
        .catch(() => setPermissions(ALL_PERMISSIONS))
      setIsLoading(false)
      return
    }

    setIsLoading(true)
    const account = await msalSignIn()
    setUser(account)
    authApi.me()
      .then(res => handleAuthResponse(res))
      .catch(() => setPermissions([]))
    setIsLoading(false)
  }, [handleAuthResponse])

  const signOut = useCallback(async () => {
    await msalSignOut()
    setUser(null)
    setPermissions([])
    setTenantIds([])
    setTenantRoles({})
    setActiveTenantId(null)
    sessionStorage.removeItem('activeTenantId')
  }, [])

  // Extract roles from the user's ID token claims
  const devRoles = ['OnCall.Viewer', 'OnCall.Scheduler', 'OnCall.Admin']
  const rawRoles = DEV_AUTH
    ? devRoles
    : ((user?.idTokenClaims as Record<string, unknown>)?.roles as string[] | undefined) || []

  const perms = permissions
  return {
    isLoading,
    isAuthenticated: DEV_AUTH ? true : isAuthenticated(),
    user,
    signIn,
    signOut,
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
