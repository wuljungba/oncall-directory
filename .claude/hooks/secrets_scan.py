#!/usr/bin/env python3
"""
PreCommit hook: blocks commits that look like they contain secrets or
connection strings — a cheap safety net on top of Key Vault usage.

This is a simple pattern scan, not a substitute for a real secrets scanner
(e.g. gitleaks/trufflehog) — wire one of those in for real coverage; this
hook is a fast local backstop.
"""
import re
import subprocess
import sys

PATTERNS = [
    r"ClientSecret\s*[:=]\s*['\"][^'\"]{8,}['\"]",
    r"(?i)password\s*[:=]\s*['\"][^'\"]{4,}['\"]",
    r"Server=.*;.*Password=",
    r"AccountKey=[A-Za-z0-9+/=]{20,}",
    r"-----BEGIN (RSA|EC|OPENSSH) PRIVATE KEY-----",
]


def staged_diff():
    result = subprocess.run(
        ["git", "diff", "--cached"], capture_output=True, text=True
    )
    return result.stdout


def main():
    diff = staged_diff()
    hits = []
    for pattern in PATTERNS:
        for match in re.finditer(pattern, diff):
            hits.append((pattern, match.group(0)[:60]))

    if hits:
        print("[secrets_scan] possible secret(s) found in staged changes:", file=sys.stderr)
        for pattern, snippet in hits:
            print(f"  pattern={pattern!r} snippet={snippet!r}", file=sys.stderr)
        print("[secrets_scan] blocking commit. Move values to Key Vault / env config.", file=sys.stderr)
        sys.exit(1)

    sys.exit(0)


if __name__ == "__main__":
    main()
