import { useState, useEffect } from 'react'
import { CheckCircle, ArrowRight, RefreshCw, Sparkles } from 'lucide-react'
import { settingsApi, integrationsApi, scheduleApi } from '@/services/api'
import { useToast } from '@/components/Toast'

interface OnboardingProps {
  onComplete: () => void
}

type StepStatus = 'pending' | 'in_progress' | 'done'

export default function OnboardingWizard({ onComplete }: OnboardingProps) {
  const [step, setStep] = useState(0)
  const [step1Status, setStep1Status] = useState<StepStatus>('in_progress')
  const [step2Status, setStep2Status] = useState<StepStatus>('pending')
  const [step3Status, setStep3Status] = useState<StepStatus>('pending')
  const [syncing, setSyncing] = useState(false)
  const [scheduleName, setScheduleName] = useState('')
  const [scheduleCreating, setScheduleCreating] = useState(false)
  const { addToast } = useToast()

  const steps = [
    {
      title: 'Connect Microsoft 365',
      description: 'Your organization is already connected via Entra ID. Verify the connection.',
      status: step1Status,
    },
    {
      title: 'Sync Your Directory',
      description: 'Import users from Azure Active Directory to populate the phone directory.',
      status: step2Status,
    },
    {
      title: 'Create Your First Schedule',
      description: 'Set up an initial on-call rotation to get started.',
      status: step3Status,
    },
  ]

  async function handleStep1() {
    setStep1Status('in_progress')
    // M365 connection check — try a Graph API call to verify
    try {
      await integrationsApi.syncAd()
      setStep1Status('done')
      addToast({ type: 'success', title: 'Connected', description: 'Microsoft 365 connection verified.' })
      setStep(1)
      setStep2Status('in_progress')
    } catch {
      addToast({ type: 'error', title: 'Connection Issue', description: 'Could not verify M365 connection. Check your configuration.' })
    }
  }

  async function handleStep2() {
    setStep2Status('in_progress')
    setSyncing(true)
    try {
      const result = await integrationsApi.syncAd()
      setStep2Status('done')
      addToast({ type: 'success', title: 'Directory Synced', description: `${result.synced} users imported from Active Directory.` })
      setStep(2)
      setStep3Status('in_progress')
    } catch {
      addToast({ type: 'error', title: 'Sync Failed', description: 'Could not sync directory. You can use CSV import instead.' })
    } finally {
      setSyncing(false)
    }
  }

  async function handleStep3() {
    if (!scheduleName.trim()) return
    setScheduleCreating(true)
    try {
      await scheduleApi.create({
        name: scheduleName.trim(),
        rotationType: 'weekly',
        startDate: new Date().toISOString(),
        endDate: new Date(Date.now() + 90 * 24 * 60 * 60 * 1000).toISOString(),
      } as any)
      setStep3Status('done')
      // Mark onboarding as complete
      await settingsApi.upsert('onboarding.completed', 'true', 'Onboarding wizard completed')
      addToast({ type: 'success', title: 'All Set!', description: 'Your first schedule is ready. Welcome to OnCall!' })
      onComplete()
    } catch {
      addToast({ type: 'error', title: 'Failed', description: 'Could not create schedule. You can do this later.' })
    } finally {
      setScheduleCreating(false)
    }
  }

  async function handleSkip() {
    try {
      await settingsApi.upsert('onboarding.completed', 'true', 'Onboarding wizard skipped')
    } catch {
      // Skip works even if settings API is unavailable or user isn't an admin
    }
    onComplete()
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70">
      <div className="bg-gray-900 border border-gray-800 rounded-2xl w-full max-w-2xl mx-4 shadow-2xl">
        {/* Header */}
        <div className="px-8 py-6 border-b border-gray-800">
          <h1 className="text-2xl font-bold text-amber-500">Welcome to OnCall</h1>
          <p className="text-sm text-gray-400 mt-1">Let's get your on-call scheduling up and running in 3 quick steps.</p>
        </div>

        {/* Steps */}
        <div className="px-8 py-6 space-y-4">
          {steps.map((s, i) => (
            <div
              key={i}
              className={`flex items-start gap-4 p-4 rounded-xl transition-colors ${
                step === i ? 'bg-amber-600/5 border border-amber-600/20' : 'bg-gray-800/30'
              }`}
            >
              {/* Step indicator */}
              <div className={`flex-shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-sm font-medium ${
                s.status === 'done'
                  ? 'bg-green-600 text-white'
                  : step === i
                  ? 'bg-amber-600 text-white'
                  : 'bg-gray-700 text-gray-400'
              }`}>
                {s.status === 'done' ? <CheckCircle className="w-5 h-5" /> : i + 1}
              </div>

              <div className="flex-1">
                <div className="flex items-center justify-between">
                  <h3 className={`font-medium ${s.status === 'done' ? 'text-green-400' : step === i ? 'text-amber-400' : 'text-gray-400'}`}>
                    {s.title}
                  </h3>
                  {s.status === 'done' && (
                    <span className="text-xs text-green-500">Complete</span>
                  )}
                </div>
                <p className="text-sm text-gray-500 mt-1">{s.description}</p>

                {/* Step actions */}
                {step === i && s.status === 'in_progress' && (
                  <div className="mt-4">
                    {i === 0 && (
                      <button
                        onClick={handleStep1}
                        className="flex items-center gap-2 px-5 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
                      >
                        Verify Connection
                        <ArrowRight className="w-4 h-4" />
                      </button>
                    )}
                    {i === 1 && (
                      <button
                        onClick={handleStep2}
                        disabled={syncing}
                        className="flex items-center gap-2 px-5 py-2 bg-amber-600 hover:bg-amber-700 disabled:opacity-50 rounded-lg text-sm font-medium transition-colors"
                      >
                        <RefreshCw className={`w-4 h-4 ${syncing ? 'animate-spin' : ''}`} />
                        {syncing ? 'Syncing...' : 'Sync Directory Now'}
                      </button>
                    )}
                    {i === 2 && (
                      <div className="space-y-3">
                        <input
                          type="text"
                          value={scheduleName}
                          onChange={(e) => setScheduleName(e.target.value)}
                          placeholder="e.g., Weekly Department Rotation"
                          className="w-full bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
                        />
                        <div className="flex gap-2">
                          <button
                            onClick={handleStep3}
                            disabled={scheduleCreating || !scheduleName.trim()}
                            className="flex items-center gap-2 px-5 py-2 bg-amber-600 hover:bg-amber-700 disabled:opacity-50 rounded-lg text-sm font-medium transition-colors"
                          >
                            <Sparkles className="w-4 h-4" />
                            {scheduleCreating ? 'Creating...' : 'Create Schedule & Finish'}
                          </button>
                        </div>
                      </div>
                    )}
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>

        {/* Footer */}
        <div className="px-8 py-4 border-t border-gray-800 flex items-center justify-between">
          <p className="text-xs text-gray-600">You can always change these settings later.</p>
          {step < 2 && (
            <button
              onClick={handleSkip}
              className="text-sm text-gray-500 hover:text-gray-300 transition-colors"
            >
              Skip setup
            </button>
          )}
        </div>
      </div>
    </div>
  )
}

/**
 * Hook to check if onboarding has been completed.
 */
export function useOnboarding() {
  const [showOnboarding, setShowOnboarding] = useState(false)
  const [checking, setChecking] = useState(true)

  useEffect(() => {
    settingsApi.get('onboarding.completed')
      .then(() => setShowOnboarding(false))
      .catch(() => setShowOnboarding(true))
      .finally(() => setChecking(false))
  }, [])

  return { showOnboarding, checking, dismiss: () => setShowOnboarding(false) }
}
