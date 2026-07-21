import { useState, useCallback, createContext, useContext, useRef, useEffect } from 'react'
import { X, CheckCircle, AlertTriangle, Info } from 'lucide-react'

export type ToastType = 'info' | 'success' | 'warning' | 'error'

export interface ToastMessage {
  id: string
  type: ToastType
  title: string
  description?: string
  duration?: number
}

interface ToastContextValue {
  addToast: (toast: Omit<ToastMessage, 'id'>) => void
  removeToast: (id: string) => void
}

const ToastContext = createContext<ToastContextValue>({
  addToast: () => {},
  removeToast: () => {},
})

export function useToast() {
  return useContext(ToastContext)
}

const iconMap: Record<ToastType, typeof Info> = {
  info: Info,
  success: CheckCircle,
  warning: AlertTriangle,
  error: AlertTriangle,
}

const colorMap: Record<ToastType, string> = {
  info: 'border-blue-600 bg-blue-600/10 text-blue-400',
  success: 'border-green-600 bg-green-600/10 text-green-400',
  warning: 'border-yellow-600 bg-yellow-600/10 text-yellow-400',
  error: 'border-red-600 bg-red-600/10 text-red-400',
}

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = useState<ToastMessage[]>([])
  const timersRef = useRef<Map<string, ReturnType<typeof setTimeout>>>(new Map())

  const removeToast = useCallback((id: string) => {
    setToasts((prev) => prev.filter((t) => t.id !== id))
    const timer = timersRef.current.get(id)
    if (timer) {
      clearTimeout(timer)
      timersRef.current.delete(id)
    }
  }, [])

  const addToast = useCallback(
    (toast: Omit<ToastMessage, 'id'>) => {
      const id = `toast-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
      const duration = toast.duration ?? 5000
      const newToast: ToastMessage = { ...toast, id }

      setToasts((prev) => [...prev, newToast])

      if (duration > 0) {
        const timer = setTimeout(() => {
          removeToast(id)
        }, duration)
        timersRef.current.set(id, timer)
      }

      return id
    },
    [removeToast]
  )

  // Clean up all timers on unmount
  useEffect(() => {
    return () => {
      timersRef.current.forEach((timer) => clearTimeout(timer))
      timersRef.current.clear()
    }
  }, [])

  return (
    <ToastContext.Provider value={{ addToast, removeToast }}>
      {children}

      {/* Toast Container — fixed bottom-right */}
      <div
        aria-live="polite"
        aria-atomic="false"
        className="fixed bottom-6 right-6 z-[100] flex flex-col gap-3 max-w-sm w-full pointer-events-none"
      >
        {toasts.map((toast) => {
          const Icon = iconMap[toast.type]
          return (
            <div
              key={toast.id}
              className={`pointer-events-auto flex items-start gap-3 rounded-xl border p-4 shadow-lg backdrop-blur-sm transition-all duration-300 animate-in slide-in-from-right-8 ${colorMap[toast.type]} bg-gray-900/95`}
              role="alert"
            >
              <Icon className="w-5 h-5 flex-shrink-0 mt-0.5" />
              <div className="flex-1 min-w-0">
                <p className="text-sm font-medium">{toast.title}</p>
                {toast.description && (
                  <p className="text-xs text-gray-400 mt-0.5">{toast.description}</p>
                )}
              </div>
              <button
                onClick={() => removeToast(toast.id)}
                className="p-0.5 hover:bg-gray-800 rounded transition-colors flex-shrink-0"
                aria-label="Dismiss"
              >
                <X className="w-4 h-4" />
              </button>
            </div>
          )
        })}
      </div>
    </ToastContext.Provider>
  )
}

/**
 * Maps SignalR event types to user-facing toast notifications.
 */
export function getToastForSignalEvent(
  event: { type: string; payload: unknown }
): { title: string; description?: string; type: ToastType } {
  switch (event.type) {
    case 'ScheduleCreated':
      return {
        type: 'success',
        title: 'Schedule Created',
        description: 'A new on-call schedule has been created.',
      }
    case 'ShiftAssigned':
      return {
        type: 'info',
        title: 'Shift Assigned',
        description: 'A shift has been assigned to a team member.',
      }
    case 'ShiftsGenerated':
      return {
        type: 'success',
        title: 'Shifts Generated',
        description: 'Shifts have been auto-generated for the schedule.',
      }
    case 'SwapRequested':
      return {
        type: 'warning',
        title: 'Swap Requested',
        description: 'A team member has requested a shift swap.',
      }
    case 'SwapApproved':
      return {
        type: 'success',
        title: 'Swap Approved',
        description: 'A shift swap request has been approved.',
      }
    case 'TimeOffUpdated':
      return {
        type: 'info',
        title: 'Time Off Updated',
        description: 'A time-off request has been created or updated.',
      }
    default:
      return {
        type: 'info',
        title: 'Notification',
      }
  }
}
