#!/usr/bin/env python3
"""
PreToolUse hook: warns (and can block) when an edit/write tool is invoked
against source files in an area that has no discovery baseline yet.

This is intentionally simple and conservative — it nudges rather than
silently blocking, since Claude Code hook APIs vary by version. Adjust the
AREA_MAP and enforcement behavior to match your installed hook contract.
"""
import json
import os
import sys

PROJECT_DIR = os.environ.get("CLAUDE_PROJECT_DIR", ".")
SPECS_DIR = os.path.join(PROJECT_DIR, ".claude", "specs")

AREA_MAP = {
    "src/backend/OnCallApi/Controllers": "backend",
    "src/backend/OnCallApi/Services": "backend",
    "src/backend/OnCallApi/Data": "backend",
    "src/backend/OnCallApi/Authentication": "auth",
    "src/backend/OnCallApi/Middleware": "auth",
    "src/frontend/src": "frontend",
    "infrastructure": "infra",
}


def area_for_path(path: str):
    for prefix, area in AREA_MAP.items():
        if prefix in path:
            return area
    return None


def has_baseline(area: str) -> bool:
    return os.path.exists(os.path.join(SPECS_DIR, f"baseline-{area}.md"))


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        # If we can't parse input, don't block — fail open.
        sys.exit(0)

    tool_name = payload.get("tool_name", "")
    tool_input = payload.get("tool_input", {})
    target_path = tool_input.get("path") or tool_input.get("file_path") or ""

    if tool_name not in ("str_replace", "create_file", "bash_tool"):
        sys.exit(0)

    area = area_for_path(target_path)
    if area is None:
        sys.exit(0)

    if not has_baseline(area):
        sys.stderr.write(
            f"[discovery_gate] No baseline found for area '{area}' "
            f"(.claude/specs/baseline-{area}.md missing). "
            "Run the discovery-baseline skill for this area before editing.\n"
        )
        # Exit code convention varies by hook version — nonzero signals
        # "block" in most Claude Code hook setups. Adjust if your version
        # expects a different signal.
        sys.exit(1)

    sys.exit(0)


if __name__ == "__main__":
    main()
