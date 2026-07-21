import { useState, useEffect, createContext, useContext, useRef } from 'react'
import { connectSignalR, disconnectSignalR, subscribe, type NotificationEvent } from '@/services/signalr'
import { getAccessToken } from '@/services/auth'
import { useToast, getToastForSignalEvent } from '@/components/Toast'

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
  const { addToast } = useToast()
  const lastEventRef = useRef<string | null>(null)

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
      if (cancelled) return
      setLastEvent(event)

      // Show a toast notification for the event
      // Deduplicate identical events within 2 seconds to avoid spam
      const eventKey = `${event.type}-${JSON.stringify(event.payload)}`
      if (eventKey !== lastEventRef.current) {
        lastEventRef.current = eventKey
        const toast = getToastForSignalEvent(event)
        addToast(toast)
        // Clear the dedup reference after 2 seconds
        setTimeout(() => {
          lastEventRef.current = null
        }, 2000)
      }
    })

    return () => {
      cancelled = true
      unsub()
      disconnectSignalR()
    }
  }, [addToast])

  return (
    <SignalRContext.Provider value={{ isConnected, lastEvent }}>
      {children}
    </SignalRContext.Provider>
  )
}
