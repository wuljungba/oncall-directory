import { useState, useEffect, useCallback } from 'react'
import type { AccountInfo } from '@azure/msal-browser'
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
  const [isLoading, setIsLoading] = useState(true)
  const [user, setUser] = useState<AccountInfo | null>(null)

  useEffect(() => {
    initAuth()
      .then(() => {
        setUser(getCurrentUser())
        setIsLoading(false)
      })
      .catch((err) => {
        console.error('Auth init failed:', err)
        setIsLoading(false)
      })
  }, [])

  const signIn = useCallback(async () => {
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
    isAuthenticated: isAuthenticated(),
    user,
    signIn,
    signOut,
  }
}
