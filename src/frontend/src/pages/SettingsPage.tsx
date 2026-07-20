import { useState, useEffect } from 'react'
import { Save, AlertTriangle } from 'lucide-react'
import { settingsApi } from '@/services/api'

interface AppSettings {
  adSyncInterval: number
  calendarSyncInterval: number
  sessionTimeout: number
  defaultRotation: string
  teamsNotifications: boolean
  emailNotifications: boolean
  smsForEscalation: boolean
}

const DEFAULTS: AppSettings = {
  adSyncInterval: 15,
  calendarSyncInterval: 5,
  sessionTimeout: 15,
  defaultRotation: 'weekly',
  teamsNotifications: true,
  emailNotifications: true,
  smsForEscalation: true,
}

const SETTING_KEYS: Record<keyof AppSettings, string> = {
  adSyncInterval: 'sync.ad_interval_minutes',
  calendarSyncInterval: 'sync.calendar_interval_minutes',
  sessionTimeout: 'hipaa.session_timeout_minutes',
  defaultRotation: 'schedule.default_rotation',
  teamsNotifications: 'notifications.teams_enabled',
  emailNotifications: 'notifications.email_enabled',
  smsForEscalation: 'notifications.sms_escalation_enabled',
}

export default function SettingsPage() {
  const [settings, setSettings] = useState<AppSettings>(DEFAULTS)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    loadSettings()
  }, [])

  async function loadSettings() {
    try {
      setLoading(true)
      const all = await settingsApi.getAll()
      const parsed: Partial<AppSettings> = {}

      for (const s of all) {
        const value = s.value
        switch (s.key) {
          case SETTING_KEYS.adSyncInterval:
            parsed.adSyncInterval = Number(value) || DEFAULTS.adSyncInterval
            break
          case SETTING_KEYS.calendarSyncInterval:
            parsed.calendarSyncInterval = Number(value) || DEFAULTS.calendarSyncInterval
            break
          case SETTING_KEYS.sessionTimeout:
            parsed.sessionTimeout = Number(value) || DEFAULTS.sessionTimeout
            break
          case SETTING_KEYS.defaultRotation:
            parsed.defaultRotation = value || DEFAULTS.defaultRotation
            break
          case SETTING_KEYS.teamsNotifications:
            parsed.teamsNotifications = value === 'true'
            break
          case SETTING_KEYS.emailNotifications:
            parsed.emailNotifications = value === 'true'
            break
          case SETTING_KEYS.smsForEscalation:
            parsed.smsForEscalation = value === 'true'
            break
        }
      }

      setSettings((prev) => ({ ...prev, ...parsed }))
    } catch (err) {
      console.error('Failed to load settings:', err)
      setError('Could not load settings from server. Using defaults.')
    } finally {
      setLoading(false)
    }
  }

  async function handleSave() {
    setSaving(true)
    setError(null)
    setSaved(false)

    try {
      const promises: Promise<unknown>[] = []

      promises.push(
        settingsApi.upsert(SETTING_KEYS.adSyncInterval, String(settings.adSyncInterval), 'AD sync interval in minutes'),
        settingsApi.upsert(SETTING_KEYS.calendarSyncInterval, String(settings.calendarSyncInterval), 'Calendar sync interval in minutes'),
        settingsApi.upsert(SETTING_KEYS.sessionTimeout, String(settings.sessionTimeout), 'HIPAA session timeout in minutes'),
        settingsApi.upsert(SETTING_KEYS.defaultRotation, settings.defaultRotation, 'Default schedule rotation type'),
        settingsApi.upsert(SETTING_KEYS.teamsNotifications, String(settings.teamsNotifications), 'Enable Teams notifications'),
        settingsApi.upsert(SETTING_KEYS.emailNotifications, String(settings.emailNotifications), 'Enable email notifications'),
        settingsApi.upsert(SETTING_KEYS.smsForEscalation, String(settings.smsForEscalation), 'Enable SMS for escalations'),
      )

      await Promise.all(promises)
      setSaved(true)
      setTimeout(() => setSaved(false), 3000)
    } catch (err) {
      console.error('Failed to save settings:', err)
      setError('Failed to save settings. Please try again.')
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
      </div>
    )
  }

  return (
    <div className="space-y-6 max-w-2xl">
      <h1 className="text-2xl font-bold">Settings</h1>

      {/* Error banner */}
      {error && (
        <div className="flex items-center gap-3 bg-red-600/10 border border-red-600/30 rounded-xl px-5 py-3 text-sm text-red-400">
          <AlertTriangle className="w-5 h-5 flex-shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {/* Saved confirmation */}
      {saved && (
        <div className="flex items-center gap-3 bg-green-600/10 border border-green-600/30 rounded-xl px-5 py-3 text-sm text-green-400">
          <span>Settings saved successfully.</span>
        </div>
      )}

      {/* Integrations */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium">Microsoft 365 Integrations</h2>

        <div className="space-y-3">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm font-medium">Active Directory Sync</p>
              <p className="text-xs text-gray-500">
                Automatically sync users every {settings.adSyncInterval} minutes
              </p>
            </div>
            <input
              type="range"
              min="5"
              max="60"
              value={settings.adSyncInterval}
              onChange={(e) =>
                setSettings({ ...settings, adSyncInterval: Number(e.target.value) })
              }
              className="w-24"
            />
          </div>

          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm font-medium">Calendar Sync</p>
              <p className="text-xs text-gray-500">
                Push on-call shifts to Outlook calendars
              </p>
            </div>
            <input
              type="range"
              min="1"
              max="30"
              value={settings.calendarSyncInterval}
              onChange={(e) =>
                setSettings({
                  ...settings,
                  calendarSyncInterval: Number(e.target.value),
                })
              }
              className="w-24"
            />
          </div>

          <div className="border-t border-gray-800 pt-3">
            <p className="text-sm text-gray-500 mb-2">Connection Status</p>
            <div className="space-y-2">
              <div className="flex items-center gap-2 text-sm">
                <span className="w-2 h-2 rounded-full bg-green-500" />
                Microsoft Entra ID
              </div>
              <div className="flex items-center gap-2 text-sm">
                <span className="w-2 h-2 rounded-full bg-yellow-500" />
                Microsoft Graph API — requires configuration
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Notifications */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium">Notifications</h2>

        {[
          { key: 'teamsNotifications' as const, label: 'Teams Notifications', desc: 'Shift reminders and alerts via Microsoft Teams' },
          { key: 'emailNotifications' as const, label: 'Email Notifications', desc: 'Schedule changes and swap approvals via email' },
          { key: 'smsForEscalation' as const, label: 'SMS for Escalations', desc: 'Critical escalation alerts via text message' },
        ].map(({ key, label, desc }) => (
          <div key={key} className="flex items-center justify-between">
            <div>
              <p className="text-sm font-medium">{label}</p>
              <p className="text-xs text-gray-500">{desc}</p>
            </div>
            <button
              onClick={() =>
                setSettings({
                  ...settings,
                  [key]: !settings[key],
                })
              }
              className={`relative w-10 h-6 rounded-full transition-colors ${
                settings[key] ? 'bg-amber-600' : 'bg-gray-700'
              }`}
            >
              <div
                className={`absolute top-0.5 left-0.5 w-5 h-5 rounded-full bg-white transition-transform ${
                  settings[key] ? 'translate-x-4' : 'translate-x-0'
                }`}
              />
            </button>
          </div>
        ))}
      </section>

      {/* Schedule Defaults */}
      <section className="bg-gray-900 border border-gray-800 rounded-xl p-5 space-y-4">
        <h2 className="font-medium">Schedule Defaults</h2>

        <div>
          <label className="block text-sm text-gray-500 mb-1">
            Default Rotation Type
          </label>
          <select
            value={settings.defaultRotation}
            onChange={(e) =>
              setSettings({ ...settings, defaultRotation: e.target.value })
            }
            className="bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600"
          >
            <option value="weekly">Weekly</option>
            <option value="biweekly">Bi-Weekly</option>
            <option value="monthly">Monthly</option>
          </select>
        </div>

        <div>
          <label className="block text-sm text-gray-500 mb-1">
            Session Timeout (minutes)
          </label>
          <input
            type="number"
            value={settings.sessionTimeout}
            onChange={(e) =>
              setSettings({ ...settings, sessionTimeout: Number(e.target.value) })
            }
            min={5}
            max={60}
            className="bg-gray-800 border border-gray-700 rounded-lg px-4 py-2 text-sm focus:outline-none focus:border-amber-600 w-24"
          />
          <p className="text-xs text-gray-600 mt-1">
            HIPAA requires automatic logoff after inactivity
          </p>
        </div>
      </section>

      <button
        onClick={handleSave}
        disabled={saving}
        className="flex items-center gap-2 px-6 py-2.5 bg-amber-600 hover:bg-amber-700 disabled:opacity-50 rounded-lg text-sm font-medium transition-colors"
      >
        {saving ? (
          <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
        ) : (
          <Save className="w-4 h-4" />
        )}
        {saving ? 'Saving...' : 'Save Settings'}
      </button>
    </div>
  )
}
