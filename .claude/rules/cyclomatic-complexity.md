# Cyclomatic Complexity Rules

These rules are **blocking** — violations must be refactored before proceeding.

## What cyclomatic complexity is

Cyclomatic complexity (CC) measures the number of independent paths through a function. Every decision point adds a path. High CC means the function is hard to read, hard to test, and likely doing too much.

**How to count:** Start at 1. Add 1 for each:
- `if` / `else if`
- `case` in a `switch`
- `for` / `foreach` / `while` / `do`
- `catch` block
- `&&` / `||` (each logical operator)
- `?:` ternary / `??` null-coalescing

## Limits

| CC | Status |
|----|--------|
| 1–7 | Good — preferred range |
| 8–10 | Acceptable — review if adding more logic |
| 11–15 | **Violation** — must refactor before merging |
| 16+ | **Hard block** — do not merge under any circumstances |

The threshold is per function/method body, not per file. A file can have many simple methods.

## Before committing

Run the `cyclomatic-complexity` skill audit against every method you modified or added. A method that was already over the limit when you touched it becomes your responsibility — fix it or explicitly scope it to a follow-up task with a TODO comment.

## How to reduce CC

**Extract method** — the most reliable fix. If a block of code can be named, it belongs in its own function.

**Guard clauses / early returns** — instead of nesting `if (valid) { ... }`, return early on the invalid path. Each early return eliminates a nesting level.

**Replace switch/if-chain with dictionary or polymorphism** — a `switch` on 8 cases is CC 8. A dictionary lookup is CC 1.

**Break apart compound boolean conditions** — `if (a && b && c || d)` is CC 4. Extract to a named predicate method.

**Separate validation from execution** — validation conditionals belong in a dedicated method; the main method should assume valid input.

## What NOT to do

- Don't suppress the issue by inlining helper lambdas inside the function — that moves lines but not complexity.
- Don't split a function at an arbitrary line boundary — split at a *logical* boundary where each piece has its own name and responsibility.
- Don't write a CC-compliant function that calls five unnamed helpers whose combined CC is still 30 — the goal is understandability, not metric gaming.
