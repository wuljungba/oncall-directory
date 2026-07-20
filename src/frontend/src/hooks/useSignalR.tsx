import { useState, useEffect, createContext, useContext } from 'react'
import { connectSignalR, disconnectSignalR, subscribe, type NotificationEvent } from '@/services/signalr'
import { getAccessToken } from '@/services/auth'

interface SignalRContextValue {
  isConnected: boolean
  lastEvent: NotificationEvent | null
}

const SignalRContext = createContext<SignalRContextValue>({
  isConnected: false,
  lastEvent: null,
})

export function useSignalR() {
  return useContext(SignalRContext)
}

export function SignalRProvider({ children }: { children: React.ReactNode }) {
  const [isConnected, setIsConnected] = useState(false)
  const [lastEvent, setLastEvent] = useState<NotificationEvent | null>(null)

  useEffect(() => {
    let cancelled = false

    async function init() {
      try {
        const token = await getAccessToken()
        if (!token || cancelled) return
        await connectSignalR(token)
        if (!cancelled) setIsConnected(true)
      } catch (err) {
        console.warn('[SignalR] Failed to connect (expected if backend is offline):', err)
      }
    }

    init()

    const unsub = subscribe((event) => {
      if (!cancelled) setLastEvent(event)
    })

    return () => {
      cancelled = true
      unsub()
      disconnectSignalR()
    }
  }, [])

  return (
    <SignalRContext.Provider value={{ isConnected, lastEvent }}>
      {children}
    </SignalRContext.Provider>
  )
}
