import { AlertTriangle, Plus } from 'lucide-react'

export default function EscalationPage() {
  const policies: never[] = []

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Escalation Policies</h1>
        <button
          className="flex items-center gap-2 px-4 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium"
        >
          <Plus className="w-4 h-4" /> New Policy
        </button>
      </div>

      <div className="bg-gray-900 border border-gray-800 rounded-xl">
        <div className="px-5 py-4 border-b border-gray-800">
          <p className="text-sm text-gray-500">Configure automatic escalation rules for your departments</p>
        </div>
        {policies.length === 0 ? (
          <div className="flex flex-col items-center py-16 text-gray-500">
            <AlertTriangle className="w-12 h-12 mb-4 text-gray-700" />
            <p>No escalation policies configured</p>
            <p className="text-sm mt-1">Create one to automatically escalate when on-call staff don't respond</p>
          </div>
        ) : (
          <div className="divide-y divide-gray-800">
            {policies.map((p: any) => (
              <div key={p.id} className="px-5 py-4 flex items-center justify-between">
                <div>
                  <p className="font-medium">{p.name}</p>
                  <p className="text-xs text-gray-500 mt-0.5">
                    Tier {p.escalationTierCount} · {p.maxResponseMinutes}min response · {p.notificationChannels}
                  </p>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
