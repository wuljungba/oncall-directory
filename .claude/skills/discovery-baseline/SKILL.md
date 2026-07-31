---
name: discovery-baseline
description: Mandatory first-pass workflow for mapping existing code/config before any fix, upgrade, or suggestion. Required for every subagent's first task in an area.
---

# Discovery Baseline Workflow

Used before any change is proposed in an area with no existing
`.claude/specs/baseline-<area>.md`.

## Steps

1. **Read, don't guess.** Open every file in your declared scope (see your
   agent's own `description`/scope section) — controllers, services,
   models, config, tests. Do not infer behavior from filenames alone.
2. **Trace one real flow end to end.** Pick the most safety-critical flow in
   your area (e.g., a code-call dispatch, a token validation, a production
   deploy swap) and trace it from entry point to completion, noting every
   file and config value it touches.
3. **Note the gaps, not just the happy path.** Record: TODOs, dead code,
   inconsistent patterns, dev-vs-prod divergence, anything that looks
   unfinished or fragile.
4. **Write the baseline file** to `.claude/specs/baseline-<area>.md` with:
   - Summary of current behavior (plain language first, technical detail
     after).
   - File/component map for the area.
   - Open questions you can't resolve from the code alone.
   - Explicit "not yet reviewed" list for anything adjacent but out of your
     scope, so nothing gets silently skipped.
5. **Stop.** Do not propose fixes in the same pass unless the orchestrator
   explicitly combined discovery and implementation into one task.

## When this is skipped

Only skip discovery if a baseline file for the exact area already exists
and is not stale (no relevant files have changed since). If in doubt,
re-run it — a stale baseline is worse than a repeated discovery pass.
