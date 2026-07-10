---
name: refactor
description: Guided refactoring with safety checks
allowed_tools: ["Bash", "Read", "Write", "Edit", "Grep", "Glob"]
---

# /refactor

Safe, incremental refactoring workflow.

## Before Starting

1. Verify tests exist for the code being refactored
2. Run tests — they must pass before refactoring
3. Commit current state (or stash) — clean starting point

## Process

1. **Identify** — what specific smell or issue are we addressing?
2. **Plan** — what's the smallest step that improves it?
3. **Execute** — make ONE change
4. **Verify** — run tests
5. **Commit** — small commit with clear message
6. **Repeat** — next step or done

## Refactoring Types

- `extract` — pull code into a named function/class
- `inline` — remove unnecessary indirection
- `rename` — improve naming
- `move` — relocate to better home
- `simplify` — reduce complexity

## Rules

- No behavior changes — refactoring preserves behavior
- Small steps — one thing at a time
- Tests green — always
- Commit often — easy rollback

## Output

After each step:
- What changed
- Tests status
- Next step (or done)
