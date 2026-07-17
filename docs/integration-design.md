# Microsoft 365 Integration Design

## Architecture

All Microsoft 365 integrations go through the **Microsoft Graph API** via the backend service, never from the frontend directly (protects credentials and enables server-side audit logging).

```
React SPA ──▶ ASP.NET Core API ──▶ Microsoft Graph API
                                        │
                          ┌─────────────┼─────────────┐
                          ▼             ▼             ▼
                      SharePoint    Outlook       Teams / AD
```

## Authentication Flow

1. User signs in via **Microsoft Entra ID** (OAuth 2.0 + OpenID Connect)
2. Backend receives auth token, validates, then uses **application permissions** (delegated) for Graph calls
3. Frontend gets a separate token scoped to the backend API only (never has direct Graph access)
4. Backend handles token refresh transparently

## Integration Details

### 1. Active Directory
| Capability | Implementation |
|------------|---------------|
| User Sync | Graph `GET /users/delta` — incremental sync every 15 min |
| Group Sync | Graph `GET /groups` + `/groups/{id}/members` |
| Auth | Entra ID v2 endpoint, MSAL.js (frontend) + Microsoft.Identity.Web (backend) |
| Role Mapping | Azure AD group → application role mapping |
| Profile Updates | Webhook subscription to `users/{id}` changes |

### 2. Outlook Calendar
| Capability | Implementation |
|------------|---------------|
| Schedule Publishing | Create calendar events for on-call shifts via Graph `/me/calendar/events` |
| Shift Sync | Two-way sync: app→Outlook (push on-call schedules) and Outlook→app (user blocks time off) |
| Meeting Creation | Schedule shift handoff meetings |
| Free/Busy Check | Check availability via Graph `/users/{id}/calendar/getSchedule` |

### 3. Teams
| Capability | Implementation |
|------------|---------------|
| Notifications | Send adaptive cards to users via Graph `/chats/{id}/messages` |
| On-Call Alerts | "You're on call in 1 hour" reminder with accept/snooze buttons |
| Escalation | Auto-create Teams group chat for escalation conferences |
| Shift Handoff | Post shift summary to new on-call member |
| Bot Integration | Custom Teams bot for directory lookup, schedule check, swap requests |

### 4. SharePoint
| Capability | Implementation |
|------------|---------------|
| Document Storage | Store published schedules, policies, compliance reports |
| List Integration | Optional: use SharePoint Lists as data source for directory |
| Site Publishing | Auto-generate department schedule pages |
| Compliance Records | Archive historical schedules with audit trail |

## Notification Channels

| Event | Channel | Priority |
|-------|---------|----------|
| Shift starting soon (1h) | Teams + Email | Normal |
| Shift starting now | Teams + SMS | High |
| Escalation triggered | Teams + SMS + Phone | Critical |
| Swap request | Teams + Email | Normal |
| Swap approved/denied | Teams | Normal |
| Schedule published | Email | Low |
| Gap detected (uncovered shift) | Teams + Email + SMS | High |
| Duty-hour limit approaching | Teams + Email | High |

## Data Sync Strategy

```
Service Bus (for real-time events)
  ├── Graph Webhook ──▶ Schedule Update
  ├── Graph Webhook ──▶ User Profile Change
  ├── Graph Webhook ──▶ Calendar Change
  └── Schedule Change ──▶ Teams Notification

Background Jobs (for periodic sync)
  ├── AD Delta Sync (every 15 min)
  ├── Calendar Sync (every 5 min)
  ├── Presence Sync (every 2 min)
  └── Compliance Check (daily)
```
