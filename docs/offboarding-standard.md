# OnCall — Employee Offboarding Standard

The reverse of [onboarding-standard.md](onboarding-standard.md). Every person who leaves
must be removed cleanly in the same order, so the directory, identities, permissions, and
compliance records stay coherent.

## Decision: Deactivate vs Delete

| Situation | Use |
|-----------|-----|
| Person left / no longer on-call but may return, or has **schedule/time-off/phone-tree history** | **Deactivate** (Admin → Accounts → status toggle) |
| Person was added by mistake / is a duplicate / has **no** schedule, time-off, or phone-tree history | **Permanently delete** (Admin → Accounts → ✕) |

The system protects you: permanent delete is **blocked** with a clear message when the
employee is still referenced by schedule, time-off, or phone-tree rows — deactivate instead.
That is intentional; deleting a referenced person would orphan their history.

## Ordered checklist

1. **Deactivate the account** (or permanent-delete if the delete guard allows it).
   - A `Local`/`CsvImport` record stays inactive and is **never** re-activated by the AD sync.
   - An `Ad` record: deactivating it is fine; the AD sync will keep it inactive if the user
     is gone from Entra, or it may be re-synced if the user is restored. That's expected.
2. **Revoke sign-in** so they can't keep using the app:
   - **Local account**: Admin → Users & Permissions → Local Accounts → **Deactivate** (or remove).
   - **Entra**: remove their membership/access in Entra; a removed AD user is no longer
     authorized (the tenant claim expansion won't find them).
3. **Revoke permissions** (defense-in-depth, even after revoking sign-in):
   - Admin → Users & Permissions → remove/revoke their **permission grants**.
4. **Reassign coverage**: their future shifts should be reassigned/covered before or with
   deactivation, so no gap appears on the on-call schedule.
5. **Verify**: the directory search no longer returns them as active; the Onboarding tab
   (Admin → Onboarding) no longer flags them; their active permission grants are gone.

## Audit

Employee deactivation/deletion and permission changes are recorded in the audit log (see
the audit middleware); keep that trail as the record of when and why offboarding happened.

## Relationship to onboarding

- A person deactivated and later **re-hired** is reactivated in Admin → Accounts
  (Admin → Accounts → status toggle); their `Source` and existing history are preserved.
- A person re-hired with a **new account** follows the normal [onboarding-standard](onboarding-standard.md)
  (which enforces one employee per email).
