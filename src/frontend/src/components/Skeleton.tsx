interface SkeletonProps {
  className?: string
}

/** Pulsing placeholder used during content loading. */
export function Skeleton({ className }: SkeletonProps) {
  return <div className={`animate-pulse bg-gray-800 rounded ${className ?? ''}`} />
}

export function StatCardSkeleton() {
  return (
    <div className="bg-gray-900 border border-gray-800 rounded-xl p-5">
      <div className="flex items-center justify-between">
        <div className="space-y-2">
          <Skeleton className="h-3 w-24" />
          <Skeleton className="h-8 w-16" />
        </div>
        <Skeleton className="h-10 w-10 rounded-lg" />
      </div>
    </div>
  )
}

export function ListItemSkeleton({ lines = 2 }: { lines?: number }) {
  return (
    <div className="flex items-center gap-3 p-4">
      <Skeleton className="h-10 w-10 rounded-full" />
      <div className="flex-1 space-y-1.5">
        <Skeleton className="h-4 w-32" />
        {lines > 1 && <Skeleton className="h-3 w-48" />}
      </div>
    </div>
  )
}

export function TableRowSkeleton({ cols = 7 }: { cols?: number }) {
  return (
    <div className="grid grid-cols-[60px_repeat(7,1fr)] border-b border-gray-800/50">
      <Skeleton className="h-12 mx-2 my-1" />
      {Array.from({ length: cols }).map((_, i) => (
        <Skeleton key={i} className="h-12 mx-1 my-1" />
      ))}
    </div>
  )
}
