#!/usr/bin/env python3
"""
TaskCompleted hook: runs the relevant build/test/lint commands depending on
which part of the repo changed, and fails the task if any of them fail.
"""
import os
import subprocess
import sys

PROJECT_DIR = os.environ.get("CLAUDE_PROJECT_DIR", ".")

BACKEND_DIR = os.path.join(PROJECT_DIR, "src", "backend", "OnCallApi")
FRONTEND_DIR = os.path.join(PROJECT_DIR, "src", "frontend")


def run(cmd, cwd):
    print(f"[verify_gate] running: {' '.join(cmd)} (cwd={cwd})")
    result = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True)
    print(result.stdout[-4000:])
    if result.returncode != 0:
        print(result.stderr[-4000:], file=sys.stderr)
    return result.returncode == 0


def changed_paths():
    try:
        out = subprocess.run(
            ["git", "diff", "--name-only", "HEAD"],
            cwd=PROJECT_DIR, capture_output=True, text=True, check=True,
        )
        return out.stdout.splitlines()
    except Exception:
        return []


def main():
    paths = changed_paths()
    touched_backend = any("src/backend" in p for p in paths) or not paths
    touched_frontend = any("src/frontend" in p for p in paths) or not paths

    ok = True

    if touched_backend and os.path.isdir(BACKEND_DIR):
        ok &= run(["dotnet", "build"], BACKEND_DIR)
        ok &= run(["dotnet", "test"], BACKEND_DIR)

    if touched_frontend and os.path.isdir(FRONTEND_DIR):
        ok &= run(["npm", "run", "build"], FRONTEND_DIR)
        ok &= run(["npm", "run", "test"], FRONTEND_DIR)
        ok &= run(["npm", "run", "lint"], FRONTEND_DIR)

    if not ok:
        print("[verify_gate] one or more checks failed — task not clean.", file=sys.stderr)
        sys.exit(1)

    print("[verify_gate] all checks passed.")
    sys.exit(0)


if __name__ == "__main__":
    main()
