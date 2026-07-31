---
name: spec-workflow
description: Plan -> build -> review loop for any requested change, with explicit user approval before code is touched.
---

# Spec-Driven Workflow

1. **Plan.** Orchestrator drafts `.claude/specs/<task>/spec.md` (what's
   changing and why, based on the relevant baseline file(s)) and
   `.claude/specs/<task>/tasks.md` (task list broken down per subagent,
   each independently completable).
2. **Approve.** Present the spec to the user in plain language before any
   code changes begin. Wait for explicit go-ahead — don't treat silence or
   a related follow-up question as approval.
3. **Build.** Each subagent claims its assigned task(s) from `tasks.md`,
   implements against the spec, and runs its own build/test/lint before
   marking a task complete.
4. **Compliance & review.** Any task matching `hipaa-compliance`'s trigger
   list routes through it first; every task then goes through
   `code-review` before being marked done.
5. **Report.** Orchestrator summarizes what shipped, what's still open, and
   any new questions that came up during build — against the original
   spec, not a moving target.
