import { useCallback, useEffect, useRef, useState } from 'react'

/** Activity that counts as the user still being present. */
const ACTIVITY_EVENTS = ['mousedown', 'keydown', 'touchstart', 'scroll'] as const

/** How long the "you are about to be signed out" warning is shown, in ms. */
const WARNING_MS = 60_000

interface IdleTimeoutState {
  /** True while the warning window is open. */
  isWarning: boolean
  /** Whole seconds left before sign-out, or null when not warning. */
  secondsRemaining: number | null
  /** Dismisses the warning and restarts the idle countdown. */
  staySignedIn: () => void
}

/**
 * Signs the user out after a period of inactivity.
 *
 * HIPAA requires automatic logoff, and the configured Hipaa:SessionTimeoutMinutes was read
 * by nothing — the settings page wrote it and no code ever consulted it, so sessions ran
 * until the raw token expired. This is the client half; the server independently caps token
 * lifetime to the same value, because a timer in the browser is advice, not enforcement.
 *
 * @param timeoutMinutes Minutes of inactivity before sign-out. 0 or undefined disables it.
 * @param onTimeout Called once when the idle period elapses.
 */
export function useIdleTimeout(
  timeoutMinutes: number | undefined,
  onTimeout: () => void
): IdleTimeoutState {
  const [isWarning, setIsWarning] = useState(false)
  const [secondsRemaining, setSecondsRemaining] = useState<number | null>(null)

  // Refs so the listener and timers can read current values without being re-bound.
  const onTimeoutRef = useRef(onTimeout)
  onTimeoutRef.current = onTimeout
  const isWarningRef = useRef(false)
  const restartRef = useRef<() => void>(() => {})

  const enabled = !!timeoutMinutes && timeoutMinutes > 0

  useEffect(() => {
    if (!enabled) {
      isWarningRef.current = false
      setIsWarning(false)
      setSecondsRemaining(null)
      return
    }

    const totalMs = timeoutMinutes! * 60_000
    const warningMs = Math.min(WARNING_MS, totalMs)
    // A timeout shorter than the warning window is all warning, no silent period.
    const warnAfterMs = Math.max(0, totalMs - warningMs)

    let warnTimer: ReturnType<typeof setTimeout>
    let signOutTimer: ReturnType<typeof setTimeout>
    let tick: ReturnType<typeof setInterval> | undefined

    const clearAll = () => {
      clearTimeout(warnTimer)
      clearTimeout(signOutTimer)
      if (tick) clearInterval(tick)
    }

    const start = () => {
      clearAll()
      isWarningRef.current = false
      setIsWarning(false)
      setSecondsRemaining(null)

      warnTimer = setTimeout(() => {
        isWarningRef.current = true
        setIsWarning(true)
        const deadline = Date.now() + warningMs
        setSecondsRemaining(Math.ceil(warningMs / 1000))
        tick = setInterval(() => {
          setSecondsRemaining(Math.max(0, Math.ceil((deadline - Date.now()) / 1000)))
        }, 1000)
      }, warnAfterMs)

      signOutTimer = setTimeout(() => {
        clearAll()
        isWarningRef.current = false
        setIsWarning(false)
        setSecondsRemaining(null)
        onTimeoutRef.current()
      }, totalMs)
    }

    restartRef.current = start

    // Activity restarts the countdown, but not once the warning is up: at that point the
    // user must say so deliberately, otherwise a stray scroll on an unattended screen
    // would keep a session alive indefinitely — the thing auto-logoff exists to prevent.
    const onActivity = () => {
      if (!isWarningRef.current) start()
    }

    start()
    ACTIVITY_EVENTS.forEach(e => window.addEventListener(e, onActivity, { passive: true }))

    return () => {
      clearAll()
      ACTIVITY_EVENTS.forEach(e => window.removeEventListener(e, onActivity))
    }
  }, [enabled, timeoutMinutes])

  const staySignedIn = useCallback(() => {
    restartRef.current()
  }, [])

  return { isWarning, secondsRemaining, staySignedIn }
}
