import { useState } from 'react'
import { Save } from 'lucide-react'

export default function SettingsPage() {
  const [settings, setSettings] = useState({
    adSyncInterval: 15,
    calendarSyncInterval: 5,
    sessionTimeout: 15,
    defaultRotation: 'weekly',
    teamsNotifications: true,
    emailNotifications: true,
    smsForEscalation: true,
  })

  const handleSave = () => {
    // TODO: Persist settings via API
    console.log('Settings saved:', settings)
  }

  return (
    <div className="space-y-6 max-w-2xl">
      <h1 className="text-2xl font-bold">Settings</h1>

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
          { key: 'teamsNotifications', label: 'Teams Notifications', desc: 'Shift reminders and alerts via Microsoft Teams' },
          { key: 'emailNotifications', label: 'Email Notifications', desc: 'Schedule changes and swap approvals via email' },
          { key: 'smsForEscalation', label: 'SMS for Escalations', desc: 'Critical escalation alerts via text message' },
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
                  [key]: !settings[key as keyof typeof settings],
                })
              }
              className={`relative w-10 h-6 rounded-full transition-colors ${
                settings[key as keyof typeof settings]
                  ? 'bg-amber-600'
                  : 'bg-gray-700'
              }`}
            >
              <div
                className={`absolute top-0.5 left-0.5 w-5 h-5 rounded-full bg-white transition-transform ${
                  settings[key as keyof typeof settings]
                    ? 'translate-x-4'
                    : 'translate-x-0'
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
        className="flex items-center gap-2 px-6 py-2.5 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
      >
        <Save className="w-4 h-4" />
        Save Settings
      </button>
    </div>
  )
}
