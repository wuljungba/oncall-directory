# OnCall Schedule & Phone Directory

## Project Overview
Healthcare-focused on-call scheduling and phone directory app with Microsoft 365 integration (SharePoint, Outlook, Teams, Active Directory).

## Tech Stack
- **Backend**: ASP.NET Core 8 Web API
- **Frontend**: React 18 + TypeScript + Vite + shadcn/ui + Tailwind CSS
- **Database**: Azure SQL
- **Auth**: Microsoft Entra ID (Azure AD)
- **Integrations**: Microsoft Graph API
- **Hosting**: Azure App Service
- **CI/CD**: GitHub Actions
- **Infrastructure**: Bicep (IaC)

## Project Structure
- `src/backend/OnCallApi/` — ASP.NET Core Web API
- `src/backend/OnCallFunctions/` — Azure Functions (integration triggers)
- `src/frontend/` — React SPA
- `infrastructure/bicep/` — Azure Bicep templates
- `infrastructure/pipelines/` — CI/CD pipeline definitions
- `docs/` — Design documents

## Key Conventions
- All Microsoft Graph calls go through the backend, never the frontend
- HIPAA: PHI fields are column-encrypted, all access is audited
- Follow existing patterns from orbit/forge-ai projects when applicable

## Design Documents
See `docs/architecture.md`, `docs/oncall-schedule-design.md`, `docs/phone-directory-design.md`, `docs/integration-design.md`, `docs/hipaa-compliance.md`

## Status
Design phase — ready for implementation.
