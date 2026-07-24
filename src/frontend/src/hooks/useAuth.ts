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

  useEffect(() => {
    if (DEV_AUTH) {
      // Fetch permissions from backend to respect role-switching
      authApi.me()
        .then(res => setPermissions(res.permissions))
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
          .then(res => setPermissions(res.permissions))
          .catch(() => setPermissions([]))
        setIsLoading(false)
      })
      .catch((err) => {
        console.error('[useAuth] Auth init failed:', err)
        setIsLoading(false)
      })
  }, [])

  const signIn = useCallback(async () => {
    if (DEV_AUTH) {
      setIsLoading(true)
      const fake = { username: 'dev@local' } as AccountInfo
      setUser(fake)
      // Re-fetch permissions after sign-in (role may have changed)
      authApi.me()
        .then(res => setPermissions(res.permissions))
        .catch(() => setPermissions(ALL_PERMISSIONS))
      setIsLoading(false)
      return
    }

    setIsLoading(true)
    const account = await msalSignIn()
    setUser(account)
    authApi.me()
      .then(res => setPermissions(res.permissions))
      .catch(() => setPermissions([]))
    setIsLoading(false)
  }, [])

  const signOut = useCallback(async () => {
    await msalSignOut()
    setUser(null)
    setPermissions([])
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
  }
}
