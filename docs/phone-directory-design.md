# Phone Directory Design

## Data Sources

### Primary: Active Directory Sync
- Full user directory synchronized via Microsoft Graph API
- Attributes: name, title, department, phone, email, office, manager
- Incremental sync via Graph delta queries
- Configurable sync interval (default: every 15 minutes)

### Supplemental: Local Overrides
- Extended fields not in AD (clinical specialties, certifications, languages)
- Custom groups/teams not mapped to AD groups
- Emergency contact information
- Personal contact preferences

## Directory Features

### Search & Browse
- **Quick Search**: Type-ahead search across name, department, role
- **Advanced Filters**: Department, specialty, location, role type
- **Department Tree**: Hierarchical browse by organizational structure
- **Favorites**: Frequently contacted people

### User Profile Card
```json
{
  "name": "Dr. Jane Smith",
  "title": "Attending Physician",
  "department": "Cardiology",
  "specialty": "Interventional Cardiology",
  "phone": {
    "office": "555-0142",
    "mobile": "555-0199",
    "pager": "555-0199-p"
  },
  "email": "jane.smith@hospital.org",
  "office": "Building A, Floor 3, Office 310",
  "manager": "Dr. Robert Chen",
  "onCall": true,
  "onCallUntil": "2026-07-18T07:00:00Z",
  "presence": "available",
  "reporting": ["Dr. Alice Lee", "Dr. Mark Taylor"]
}
```

### Phone Trees
- **Emergency Tree**: Who to call in escalating order for emergencies
- **Department Tree**: Primary → Backup → Department Head for each unit
- **On-Call Tree**: Current on-call personnel by role and tier
- **Admin Tree**: Administrative contacts hierarchy

### Call-to-Action
- Click-to-call (desktop/mobile)
- Click-to-email (opens default client)
- Click-to-Teams-Chat
- Click-to-Schedule (opens calendar)
- One-click escalation (trigger immediate escalation path)

## Integration Points

### Teams Presence
- Real-time presence indicators (Available, Busy, DND, Offline)
- Click to start Teams chat or call
- Status message if user is on-call

### SharePoint
- Export directory to SharePoint list
- Store org charts and department info
- Publish on-call schedules as SharePoint pages

## Data Model

```
Employee
├── Id (Azure AD ObjectId)
├── FirstName / LastName
├── Title
├── Department / Unit
├── Specialty / ClinicalRole
├── PhoneNumbers[] (type: office|mobile|pager|home)
├── Email
├── Office / Location
├── ManagerId
├── DirectReportIds[]
├── Certifications[]
├── Languages[]
├── OnCallStatus (boolean)
├── Presence (available|busy|dnd|offline|unknown)
└── EmergencyContact[]

PhoneTree
├── Id
├── Name
├── Type (emergency|department|oncall|admin)
├── DepartmentId (optional)
├── Nodes[] (ordered escalation path)
└── FallbackProcedure

PhoneTreeNode
├── Id
├── PhoneTreeId
├── Order
├── RoleId | UserId
├── Condition (optional routing rule)
└── Timeout (seconds before escalate)
```
