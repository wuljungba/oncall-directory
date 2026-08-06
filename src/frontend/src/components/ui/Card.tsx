import type { ReactNode } from 'react'

/** Shared panel container — keeps card styling consistent across the app. */
export function Card({
  children,
  className = '',
  title,
  action,
}: {
  children: ReactNode
  className?: string
  title?: string
  action?: ReactNode
}) {
  return (
    <div className={`bg-gray-900 border border-gray-800 rounded-xl ${className}`}>
      {(title || action) && (
        <div className="flex items-center justify-between border-b border-gray-800 px-5 py-4">
          {title && <h2 className="font-semibold">{title}</h2>}
          {action}
        </div>
      )}
      <div className={(title || action) ? 'p-5' : ''}>{children}</div>
    </div>
  )
}