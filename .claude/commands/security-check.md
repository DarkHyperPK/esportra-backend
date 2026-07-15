---
name: security-check
description: Run security checks on recent changes or specified files
allowed_tools: ["Bash", "Read", "Grep", "Glob"]
---

# /security-check

Quick security audit for code changes.

## Checks

1. **Hardcoded secrets** — scan for passwords, keys, tokens in code
2. **SQL injection** — look for string concatenation in queries
3. **Missing parameterization** — verify queries use parameters
4. **RLS gaps** — check new tables have policies
5. **Logged PII** — scan for sensitive data in log statements
6. **Auth bypasses** — verify endpoints have authorization

## Usage

Run on staged changes:
```bash
git diff --cached --name-only | xargs grep -l "\.cs$"
```

Then apply checks from `.claude/rules/security.md` to each file.

## Output

Report findings as:
- **CRITICAL** — must fix before merge (secrets, SQL injection)
- **HIGH** — should fix before merge (missing auth, RLS gaps)
- **MEDIUM** — fix soon (error message leakage)
- **LOW** — consider fixing (minor improvements)
