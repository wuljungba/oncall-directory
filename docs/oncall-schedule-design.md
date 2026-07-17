# On-Call Schedule Design

## Rotation Types

### 1. Department-Based Rotations
Each department/unit has its own rotation with:
- Role slots (e.g., 1 attending, 2 residents, 1 nurse coordinator)
- Custom cycle lengths (weekly, biweekly, monthly)
- Blackout dates (PTO, CME, holidays)
- Minimum rest periods between shifts (duty-hour compliance)

### 2. Escalation Tiers
```
Tier 1 (Primary) ──▶ Tier 2 (Secondary) ──▶ Tier 3 (Tertiary)
    Resolves             Escalates              Escalates
    80% calls            if unresolved          if unresolved
```

### 3. Shift Patterns
- **24/7 Coverage**: Night, weekend, holiday rotations
- **Business Hours**: Admin/office coverage
- **Partial Day**: Evening, overnight, weekend-only
- **On-Call + In-House**: Overlapping schedules for hospitals

## Schedule Management

### Creating Schedules
- Define rotation groups and members
- Set cycle length and start dates
- Auto-generate schedules from templates
- Manual override for exceptions

### Managing Changes
- **Shift Swaps**: Request → Approve → Reassign workflow
- **Time Off**: Submit → Coverage found → Approve
- **Emergency**: Instant swap with notifications
- **Sick Call**: Auto-escalate to next tier + notify manager

### Calendar Views
- **Who's On Call Now**: Dashboard widget, always visible
- **Daily View**: Today's full schedule by department
- **Weekly View**: Rotation overview with coverage gaps highlighted
- **Monthly View**: Full cycle view for planning
- **Personal View**: My upcoming on-call shifts across all groups

## Data Model (Core Entities)

```
Schedule
├── Id
├── Department / Unit
├── RotationType (weekly, biweekly, monthly)
├── StartDate / EndDate
└── Shifts[]

Shift
├── Id
├── ScheduleId
├── Date / TimeRange
├── Tier (primary, secondary, tertiary)
├── AssignedUserId
├── Status (scheduled, swapped, covered, gap)
└── Notes

ShiftSwap
├── Id
├── OriginalShiftId
├── RequestedById
├── ReplacementUserId
├── Status (pending, approved, rejected)
├── Reason
└── ApprovedBy

TimeOff
├── Id
├── UserId
├── StartDate / EndDate
├── Type (pto, cme, holiday, sick)
├── Status (pending, approved, denied)
└── Notes

EscalationPolicy
├── Id
├── DepartmentId
├── TierOrder[]
├── MaxResponseTime
├── NotificationChannels
└── OverridePath[]
```
