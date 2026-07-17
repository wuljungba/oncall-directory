# OnCall Schedule & Phone Directory — Architecture

## Overview

A healthcare-focused on-call scheduling and phone directory application with deep Microsoft 365 integration. Built for any organization but designed with healthcare workflows (role-based scheduling, HIPAA readiness, department/unit organization) as first-class concerns.

## Tech Stack

| Layer | Technology | Rationale |
|-------|-----------|-----------|
| **Backend** | ASP.NET Core 8 Web API | Native Microsoft ecosystem support, Entra ID auth, Graph SDK |
| **Frontend** | React 18 + TypeScript + Vite | Modern SPA, rapid development |
| **UI Library** | shadcn/ui + Tailwind CSS | Consistent design system (same as your Orbit project) |
| **Database** | SQL Server (Azure SQL) | Native .NET integration, rich scheduling queries |
| **Auth** | Microsoft Entra ID (Azure AD) | Single sign-on, role-based access control |
| **API Layer** | Microsoft Graph API | Unified access to SharePoint, Outlook, Teams, AD |
| **Hosting** | Azure App Service + Azure SQL | Managed, scalable, HIPAA-eligible |
| **Infra** | Bicep (IaC) | Infrastructure as Code for Azure |
| **CI/CD** | GitHub Actions | Automated build, test, deploy |

## Core Features

### On-Call Schedule
- Role-based rotation management (attendings, residents, nurses, admin)
- Calendar views (daily, weekly, monthly, who's-on-call-now)
- Shift templates and rotation patterns
- Escalation tiers (primary → secondary → tertiary)
- Shift swap requests and approval workflow
- Time-off / blackout day management
- Duty-hour compliance tracking (healthcare-specific)
- Real-time notifications via Teams + Email

### Phone Directory
- Enterprise employee directory synced from Active Directory
- Department/unit/specialty-based organization
- Role-based phone trees (who to call for what)
- Emergency contact escalation paths
- Search by name, role, department, location
- Presence indicators (via Teams Graph API)
- Click-to-call / click-to-email
- Mobile-friendly responsive design

### Microsoft 365 Integrations
- **Active Directory**: User/group sync, authentication, role mapping
- **Outlook**: Calendar sync for on-call shifts, meeting scheduling
- **Teams**: Notifications for shift start/end, escalation alerts, approval requests
- **SharePoint**: Document storage for schedules, policies, compliance records

## HIPAA Considerations
- All data encrypted at rest (Azure SQL TDE) and in transit (TLS 1.3)
- Role-based access control via Entra ID groups
- Audit logging for all PHI access
- BAA with Microsoft (Azure + Microsoft 365)
- No PHI stored in logs or analytics
- Session timeout and re-authentication
- Export/deletion capabilities for data subject requests

## API Architecture

```
┌─────────────┐     ┌──────────────┐     ┌──────────────┐
│  React SPA  │────▶│ ASP.NET Core │────▶│  Azure SQL   │
│  (Frontend) │     │  Web API     │     │  Database    │
└─────────────┘     └──────┬───────┘     └──────────────┘
                           │
                    ┌──────▼───────┐
                    │ Microsoft    │
                    │ Graph API    │
                    └──────┬───────┘
                           │
              ┌────────────┼────────────┐
              ▼            ▼            ▼
          SharePoint    Outlook      Teams / AD
```

## Directory Structure

```
oncall-directory/
├── src/
│   ├── backend/
│   │   ├── OnCallApi/              # ASP.NET Core Web API
│   │   │   ├── Controllers/        # API endpoints
│   │   │   ├── Models/             # Domain models + DTOs
│   │   │   ├── Services/           # Business logic
│   │   │   ├── Data/               # EF Core context + migrations
│   │   │   ├── Middleware/         # Auth, logging, HIPAA audit
│   │   │   └── Hubs/               # SignalR real-time notifications
│   │   └── OnCallFunctions/        # Azure Functions (integration triggers)
│   └── frontend/
│       ├── src/
│       │   ├── components/         # Reusable UI components
│       │   ├── pages/              # Route pages
│       │   ├── hooks/              # Custom React hooks
│       │   ├── services/           # API client + Graph helpers
│       │   ├── utils/              # Utilities
│       │   └── types/              # TypeScript types
│       └── package.json
├── infrastructure/
│   ├── bicep/                      # Azure Bicep templates
│   └── pipelines/                  # CI/CD pipeline definitions
├── docs/                           # Design documents
└── tests/
    ├── backend-tests/              # .NET unit + integration tests
    └── frontend-tests/             # Vitest + Playwright tests
```
