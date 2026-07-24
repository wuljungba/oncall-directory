# Hospital Emergency Codes — Call Procedures

## Overview

Hospital emergency codes are standardized alerts used to notify staff of critical events. Each code triggers a specific response protocol with defined escalation paths.

This document defines the emergency codes configurable in the OnCall system and their associated call/escalation procedures.

---

## Standard Hospital Codes

### Code Blue — Cardiac Arrest
| Property | Value |
|----------|-------|
| **Type** | Medical Emergency |
| **Color** | Blue |
| **Description** | Patient in cardiac or respiratory arrest |

**Call Procedure:**
1. Dial emergency extension or overhead page "Code Blue, [Location]"
2. Notify: Code Blue Team (ICU, Anesthesia, Respiratory Therapy)
3. Respond within: 2 minutes maximum
4. Bring: Crash cart, defibrillator, airway equipment

**Escalation:**
- If no response within 2 minutes → escalate to attending physician
- If attending unavailable → escalate to department chief

---

### Code Red — Fire
| Property | Value |
|----------|-------|
| **Type** | Environmental Emergency |
| **Color** | Red |
| **Description** | Fire or smoke detected |

**Call Procedure:**
1. Activate nearest fire alarm pull station
2. Dial emergency extension with location details
3. Recall: R.A.C.E. — Rescue, Alarm, Contain, Evacuate
4. Notify: Security, Facilities, Floor Manager

**Escalation:**
- If fire spreads beyond initial area → evacuate floor, notify fire department
- If evacuation needed → switch to Code Green protocol

---

### Code Green — Evacuation
| Property | Value |
|----------|-------|
| **Type** | Environmental Emergency |
| **Color** | Green |
| **Description** | Full or partial facility evacuation |

**Call Procedure:**
1. Initiate via overhead page "Code Green, [Zone]"
2. Notify: All staff in affected zone
3. Mobilize: Evacuation team, security, transport
4. Assembly point: Designated outdoor gathering area

**Escalation:**
- Partial evacuation → full evacuation if hazard spreads
- Notify receiving facilities when transferring patients

---

### Code Silver — Active Threat
| Property | Value |
|----------|-------|
| **Type** | Security Emergency |
| **Color** | Silver |
| **Description** | Active shooter or armed threat |

**Call Procedure:**
1. Dial emergency extension — do NOT use overhead page
2. Notify: Security, local law enforcement
3. Announce: Code Silver via secure paging system
4. Lockdown: Secure all doors, shelter in place

**Escalation:**
- Law enforcement assumes command on arrival
- Follow L.E. instructions for evacuation or neutralization

---

### Code Grey — Severe Weather
| Property | Value |
|----------|-------|
| **Type** | Environmental Emergency |
| **Color** | Grey |
| **Description** | Tornado, hurricane, or severe storm |

**Call Procedure:**
1. Monitor weather alerts via National Weather Service
2. Initiate "Code Grey, [Level]" when warning issued
3. Notify: Facilities, Security, Nursing Supervisor
4. Secure: Move patients away from windows, close blinds

**Escalation:**
- Level 1 (Watch) → prepare supplies and staffing
- Level 2 (Warning) → activate emergency operations center
- Level 3 (Impact) → full emergency response

---

### Code Pink — Infant/Child Abduction
| Property | Value |
|----------|-------|
| **Type** | Security Emergency |
| **Color** | Pink |
| **Description** | Missing infant or pediatric patient |

**Call Procedure:**
1. Verify absence — check unit, parent area, exits
2. Initiate "Code Pink" via overhead page with description
3. Notify: Security, all exits, local law enforcement
4. Lockdown facility — no one leaves without screening

**Escalation:**
- Within 5 minutes → activate amber alert
- Within 15 minutes → notify all surrounding hospitals
- Activate reunification protocol when child recovered

---

## Custom Code Configuration

The OnCall system allows defining custom codes beyond the standard set above. Each code can have:

| Field | Description |
|-------|-------------|
| **Name** | Display name (e.g., "Code Orange — Hazmat") |
| **Code Type** | Identifier used in the system (e.g., "code-orange") |
| **Procedure** | Full response protocol documentation |
| **Fallback** | Instructions if escalation chain is exhausted |
| **Escalation Nodes** | Ordered list of contacts to notify, with timeout intervals |

## Escalation Node Configuration

Each code's escalation path is built from ordered nodes:

1. **Position 1**: Primary responder (first contact)
   - Timeout: 30 seconds
   - If no response → moves to position 2
2. **Position 2**: Secondary responder / backup
   - Timeout: 30 seconds
   - If no response → moves to position 3
3. **Position N**: Escalates through the chain

Nodes can be:
- **Specific employee**: A named individual
- **Role-based**: E.g., "Charge Nurse on Duty", "Attending Physician"
- **Conditional**: Applied only under certain conditions (after hours, weekends)
