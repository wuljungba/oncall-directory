import { Shield, Users, Phone, Clock, CalendarDays, CheckCircle, ArrowRight, Menu, X } from 'lucide-react'
import { Link } from 'react-router-dom'
import { useState } from 'react'

const features = [
  {
    icon: CalendarDays,
    title: 'On-Call Scheduling',
    description: 'Automated rotation scheduling with tiered escalation. Create weekly, bi-weekly, or monthly schedules with ease.',
    accent: 'text-amber-500',
    bgAccent: 'bg-amber-600/10',
    borderAccent: 'border-amber-600/20',
  },
  {
    icon: Phone,
    title: 'Phone Directory',
    description: 'Searchable employee directory synced with Azure Active Directory. Find anyone by name, title, or department.',
    accent: 'text-blue-500',
    bgAccent: 'bg-blue-600/10',
    borderAccent: 'border-blue-600/20',
  },
  {
    icon: Clock,
    title: 'Real-Time Presence',
    description: 'See who\'s available instantly via Microsoft Teams presence integration. Know who to call at a glance.',
    accent: 'text-green-500',
    bgAccent: 'bg-green-600/10',
    borderAccent: 'border-green-600/20',
  },
  {
    icon: Shield,
    title: 'HIPAA Compliance',
    description: 'Duty-hour tracking, automatic compliance checks, and comprehensive audit logging for regulatory requirements.',
    accent: 'text-purple-500',
    bgAccent: 'bg-purple-600/10',
    borderAccent: 'border-purple-600/20',
  },
]

const policies = [
  {
    title: 'HIPAA Compliance',
    description: 'All PHI fields are column-encrypted at rest. Access is audited via immutable audit logs. Sessions auto-expire after inactivity.',
    icon: Shield,
  },
  {
    title: 'Data Security',
    description: 'All traffic is TLS-encrypted. Authentication uses Microsoft Entra ID with JWT bearer tokens. No credentials are stored in the application.',
    icon: CheckCircle,
  },
  {
    title: 'Access Control',
    description: 'Role-based access with Viewer, Scheduler, and Admin tiers. Every API call is authorized against Azure AD app roles.',
    icon: Users,
  },
]

export default function LandingPage() {
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)

  return (
    <div className="min-h-screen bg-gradient-to-br from-gray-950 via-gray-900 to-amber-950/20 text-gray-100">
      {/* Navigation Bar */}
      <nav className="border-b border-gray-800/50 backdrop-blur-sm bg-gray-950/50 sticky top-0 z-50">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="flex items-center justify-between h-16">
            <div className="flex items-center gap-2">
              <div className="w-8 h-8 rounded-lg bg-amber-600 flex items-center justify-center">
                <Clock className="w-5 h-5 text-white" />
              </div>
              <span className="text-lg font-bold text-amber-500">OnCall</span>
            </div>

            {/* Desktop nav */}
            <div className="hidden md:flex items-center gap-6">
              <a href="#features" className="text-sm text-gray-400 hover:text-gray-200 transition-colors">Features</a>
              <a href="#policies" className="text-sm text-gray-400 hover:text-gray-200 transition-colors">Policies</a>
              <Link
                to="/login"
                className="flex items-center gap-2 px-5 py-2 bg-amber-600 hover:bg-amber-700 rounded-lg text-sm font-medium transition-colors"
              >
                Sign In with Microsoft
                <ArrowRight className="w-4 h-4" />
              </Link>
            </div>

            {/* Mobile menu button */}
            <button
              className="md:hidden p-2 hover:bg-gray-800 rounded-lg transition-colors"
              onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
            >
              {mobileMenuOpen ? <X className="w-5 h-5" /> : <Menu className="w-5 h-5" />}
            </button>
          </div>

          {/* Mobile nav */}
          {mobileMenuOpen && (
            <div className="md:hidden pb-4 space-y-3">
              <a href="#features" onClick={() => setMobileMenuOpen(false)}
                className="block px-3 py-2 text-sm text-gray-400 hover:text-gray-200 hover:bg-gray-800 rounded-lg transition-colors">
                Features
              </a>
              <a href="#policies" onClick={() => setMobileMenuOpen(false)}
                className="block px-3 py-2 text-sm text-gray-400 hover:text-gray-200 hover:bg-gray-800 rounded-lg transition-colors">
                Policies
              </a>
              <Link to="/login" onClick={() => setMobileMenuOpen(false)}
                className="block px-3 py-2 text-sm text-amber-500 hover:bg-gray-800 rounded-lg transition-colors">
                Sign In
              </Link>
            </div>
          )}
        </div>
      </nav>

      {/* Hero Section */}
      <section className="relative overflow-hidden">
        {/* Subtle background decoration */}
        <div className="absolute inset-0 pointer-events-none">
          <div className="absolute -top-40 -right-40 w-80 h-80 rounded-full bg-amber-600/5 blur-3xl" />
          <div className="absolute -bottom-40 -left-40 w-80 h-80 rounded-full bg-blue-600/5 blur-3xl" />
        </div>

        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-24 sm:py-32 lg:py-40">
          <div className="text-center max-w-4xl mx-auto">
            <div className="inline-flex items-center gap-2 px-4 py-1.5 rounded-full bg-amber-600/10 border border-amber-600/20 text-amber-500 text-sm font-medium mb-8">
              <Clock className="w-4 h-4" />
              Healthcare On-Call Management
            </div>

            <h1 className="text-4xl sm:text-5xl lg:text-6xl font-bold tracking-tight">
              On-Call Scheduling{' '}
              <span className="text-transparent bg-clip-text bg-gradient-to-r from-amber-500 to-amber-300">
                Made Simple
              </span>
            </h1>

            <p className="mt-6 text-lg sm:text-xl text-gray-400 max-w-2xl mx-auto leading-relaxed">
              Automate your healthcare organization's on-call rotations, manage a searchable phone directory,
              track duty-hour compliance, and integrate seamlessly with Microsoft 365.
            </p>

            <div className="mt-10 flex flex-col sm:flex-row items-center justify-center gap-4">
              <Link
                to="/login"
                className="flex items-center gap-2 px-8 py-3 bg-amber-600 hover:bg-amber-700 rounded-xl text-base font-medium transition-all hover:shadow-lg hover:shadow-amber-600/25"
              >
                Sign In with Microsoft
                <ArrowRight className="w-5 h-5" />
              </Link>
              <a
                href="#features"
                className="flex items-center gap-2 px-8 py-3 bg-gray-800 hover:bg-gray-700 rounded-xl text-base font-medium transition-colors"
              >
                Learn More
              </a>
            </div>
          </div>
        </div>
      </section>

      {/* Features Section */}
      <section id="features" className="py-20 sm:py-28">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold">
              Everything you need to{' '}
              <span className="text-amber-500">manage on-call</span>
            </h2>
            <p className="mt-4 text-gray-400 max-w-2xl mx-auto">
              A comprehensive suite of tools built for healthcare organizations, integrated with your existing Microsoft 365 environment.
            </p>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
            {features.map((feature) => (
              <div
                key={feature.title}
                className={`group p-6 sm:p-8 rounded-xl border ${feature.borderAccent} ${feature.bgAccent} hover:bg-opacity-20 transition-all hover:-translate-y-0.5`}
              >
                <div className={`w-12 h-12 rounded-lg ${feature.bgAccent} flex items-center justify-center mb-4`}>
                  <feature.icon className={`w-6 h-6 ${feature.accent}`} />
                </div>
                <h3 className="text-lg font-medium mb-2">{feature.title}</h3>
                <p className="text-sm text-gray-400 leading-relaxed">{feature.description}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Policies Section */}
      <section id="policies" className="py-20 sm:py-28 border-t border-gray-800/50">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="text-center mb-16">
            <h2 className="text-3xl sm:text-4xl font-bold">
              Enterprise-Grade{' '}
              <span className="text-amber-500">Security & Compliance</span>
            </h2>
            <p className="mt-4 text-gray-400 max-w-2xl mx-auto">
              Built from the ground up for healthcare compliance requirements, with no compromises on security.
            </p>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
            {policies.map((policy) => (
              <div
                key={policy.title}
                className="p-6 rounded-xl bg-gray-900/50 border border-gray-800 hover:border-gray-700 transition-colors"
              >
                <policy.icon className="w-10 h-10 text-amber-500 mb-4" />
                <h3 className="font-medium mb-2">{policy.title}</h3>
                <p className="text-sm text-gray-400 leading-relaxed">{policy.description}</p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* CTA Section */}
      <section className="py-20 border-t border-gray-800/50">
        <div className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
          <h2 className="text-3xl sm:text-4xl font-bold mb-4">
            Ready to get started?
          </h2>
          <p className="text-gray-400 mb-8 max-w-xl mx-auto">
            Sign in with your Microsoft 365 account to begin managing on-call schedules and your phone directory.
          </p>
          <Link
            to="/login"
            className="inline-flex items-center gap-2 px-8 py-3 bg-amber-600 hover:bg-amber-700 rounded-xl text-base font-medium transition-all hover:shadow-lg hover:shadow-amber-600/25"
          >
            Sign In with Microsoft
            <ArrowRight className="w-5 h-5" />
          </Link>
        </div>
      </section>

      {/* Footer */}
      <footer className="border-t border-gray-800/50 py-8">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 flex flex-col sm:flex-row items-center justify-between gap-4">
          <div className="flex items-center gap-2">
            <Clock className="w-4 h-4 text-amber-500" />
            <span className="text-sm font-medium text-amber-500">OnCall</span>
          </div>
          <p className="text-xs text-gray-600">
            &copy; {new Date().getFullYear()} OnCall Schedule & Directory. All rights reserved.
          </p>
          <p className="text-xs text-gray-700">
            Healthcare on-call management platform
          </p>
        </div>
      </footer>
    </div>
  )
}
