# OnCall Schedule & Phone Directory

A healthcare-focused on-call scheduling and employee phone directory application with deep Microsoft 365 integration.

## Features

### 📅 On-Call Schedule
- Role-based rotation management
- Multi-tier escalation (primary → secondary → tertiary)
- Shift swap requests and approval workflow
- Calendar views (daily, weekly, monthly)
- Real-time notifications via Teams + Email
- Duty-hour compliance tracking

### 📞 Phone Directory
- Enterprise directory synced from Active Directory
- Department/unit/specialty organization
- Emergency phone trees and escalation paths
- Real-time Teams presence indicators
- Click-to-call, click-to-email, click-to-chat

### 🔗 Microsoft 365 Integrations
- **Active Directory** — user/group sync + authentication
- **Outlook** — calendar sync for on-call shifts
- **Teams** — adaptive card notifications + bot
- **SharePoint** — schedule publishing + compliance records

## Tech Stack

| Layer | Stack |
|-------|-------|
| Backend | ASP.NET Core 8 Web API |
| Frontend | React 18 + TypeScript + Vite |
| UI | shadcn/ui + Tailwind CSS |
| Database | Azure SQL |
| Auth | Microsoft Entra ID |
| Hosting | Azure App Service |
| CI/CD | GitHub Actions |

## Getting Started

### Prerequisites
- Node.js 20+
- .NET 8 SDK
- Azure subscription (for deployment)
- Microsoft 365 tenant with Graph API access

### Local Development

```bash
# Clone and navigate
cd oncall-directory

# Backend
cd src/backend/OnCallApi
dotnet restore
dotnet run

# Frontend
cd src/frontend
npm install
npm run dev
```

## Documentation

- [Architecture](docs/architecture.md)
- [On-Call Schedule Design](docs/oncall-schedule-design.md)
- [Phone Directory Design](docs/phone-directory-design.md)
- [Integration Design](docs/integration-design.md)
- [HIPAA Compliance](docs/hipaa-compliance.md)

## Project Status

Currently in **design phase**. See `docs/` for the full plan.
