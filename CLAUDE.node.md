# On-Call Schedule & Phone Directory App

## 1. Execution Guardrails (Strict Discovery First)
- **Context First**: Before modifying code or making recommendations, you MUST read existing schemas, route files, and configuration files.
- **Proposal Rule**: For any structural changes, output a "Current State vs. Proposed Change" summary and wait for user confirmation.
- **No Assumptions**: If an environment variable or third-party service is unlisted, ask the user instead of guessing.

## 2. Production Readiness Criteria
- **Security**: Encrypt phone numbers at rest, mask them in transit, and require JWT authentication for all API routes.
- **Availability**: Implement a robust escalation loop (e.g., fallback to secondary engineer if primary fails to acknowledge).
- **Validation**: Enforce E.164 phone number formatting (e.g., +1234567890) on all inputs before database write.

## 3. Project Tech Stack
- **Frontend**: React / Next.js
- **Backend**: Node.js / Next.js API Routes
- **Database**: PostgreSQL / Prisma ORM
