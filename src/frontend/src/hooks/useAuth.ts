import { useState, useEffect, useCallback } from 'react'
import type { AccountInfo } from '@azure/msal-browser'

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
}

export function useAuth(): AuthState {
  const [isLoading, setIsLoading] = useState(DEV_AUTH ? false : true)
  const [user, setUser] = useState<AccountInfo | null>(DEV_AUTH ? ({ username: 'dev@local' } as AccountInfo) : null)

  useEffect(() => {
    console.debug('[useAuth] initAuth starting')
    if (DEV_AUTH) {
      console.debug('[useAuth] DEV_AUTH enabled — skipping MSAL init')
      const fake = { username: 'dev@local' } as AccountInfo
      setUser(fake)
      setIsLoading(false)
      return
    }

    initAuth()
      .then(() => {
        console.debug('[useAuth] initAuth resolved')
        setUser(getCurrentUser())
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
      setIsLoading(false)
      return
    }

    setIsLoading(true)
    const account = await msalSignIn()
    setUser(account)
    setIsLoading(false)
  }, [])

  const signOut = useCallback(async () => {
    await msalSignOut()
    setUser(null)
  }, [])

  return {
    isLoading,
    isAuthenticated: DEV_AUTH ? true : isAuthenticated(),
    user,
    signIn,
    signOut,
  }
}
