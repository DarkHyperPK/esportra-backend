---
name: cyclomatic-complexity
description: >
  Audit and enforce cyclomatic complexity (CC) limits on C# and TypeScript code.
  Use this skill whenever you are writing, modifying, or reviewing any method with
  branching logic (if/switch/loops/catch/boolean operators). Also use it when the
  user asks to audit, review, or check complexity, or when you are about to commit
  code that touches methods with conditional logic.
---

# Cyclomatic Complexity Audit Skill

## Goal

Catch methods whose branching logic is complex enough to be risky — hard to test, easy to break — before they reach production.

## Counting CC

CC = 1 (base) + 1 per decision point:

| Construct | +CC |
|-----------|-----|
| `if` / `else if` | 1 each |
| `case` in `switch` | 1 each (not the `switch` keyword itself) |
| `for` / `foreach` / `while` / `do` | 1 each |
| `catch` block | 1 each |
| `&&` logical AND | 1 each |
| `\|\|` logical OR | 1 each |
| `?:` ternary | 1 each |
| `??` null-coalescing | 1 each |

A bare `else` does NOT add to CC — it is the default path of an already-counted `if`.

**Example:**
```csharp
public string Classify(int score) {       // base = 1
    if (score >= 90) return "A";          // +1 → 2
    else if (score >= 80) return "B";     // +1 → 3
    else if (score >= 70) return "C";     // +1 → 4
    return score > 0 ? "D" : "F";        // +1 (?:) → 5
}
// CC = 5 — acceptable
```

## Thresholds

| CC | Action |
|----|--------|
| 1–7 | No action required |
| 8–10 | Acceptable; leave a note if you're about to add more branches |
| 11–15 | **Refactor required** before this change can merge |
| 16+ | **Hard block** — do not proceed |

## Audit Workflow

When auditing a file or set of files:

1. **Scan every method/function** — list each one with its calculated CC.
2. **Flag violations** — any method with CC ≥ 11 is a violation.
3. **Report borderline cases** — CC 8–10 gets a note but is not a block.
4. **For each violation, propose a concrete refactoring** — name the extract or guard-clause transformation, show the split point.

Output format:
```
FILE: src/Esportra.Api/Endpoints/BracketEndpoints.cs

  FinalizeScoreHandler         CC=22  VIOLATION  → extract ValidateScoreInput, ExtractWinnerFromGames, AdvanceBracketEdges
  SeedBracketHandler           CC=14  VIOLATION  → extract AssignByeSlots, BuildSeedAssignments
  GetBracketHandler            CC=6   OK
  ...

SUMMARY: 2 violations, 1 borderline, 8 OK
```

## Common Violation Patterns in This Codebase

### Large endpoint handlers
Minimal API handlers accumulate CC because they inline validation, auth checks, DB queries, and business logic in one lambda. Fix: extract a `HandleXxx(conn, req, userCtx)` method that contains the business logic, leaving the lambda as a thin dispatcher.

### Nested null guards
```csharp
// CC=5 just on guards
if (a != null) {
    if (a.B != null) {
        if (a.B.C != null) { ... }
    }
}
// Fix: early returns
if (a?.B?.C is null) return;
```

### Compound boolean conditions in WHERE clauses
SQL building with `if (filter1) sql += "..."` repeated 6 times. Fix: collect filters in a list, join at the end.

### Swiss/RR generator pair-building loops
Double loops with rematch prevention checks compound quickly. Fix: extract `IsPairValid(a, b, playedMap)` and `AssignByeTeam(teams)`.

## Refactoring Techniques

**Extract method** — give the block a name and move it. If you can name it, it's extractable.

**Guard clause / early return** — invert the condition and return immediately. Flattens nesting by one level per guard.

**Replace conditional with polymorphism** — when `switch (type)` selects behaviour, each arm becomes a subclass or dictionary entry. Eliminates the whole switch's CC contribution.

**Decompose compound boolean** — `if (IsExpired(token) || IsRevoked(token) && !IsAdmin(user))` → extract to `ShouldDenyAccess(token, user)`.

**Split validation from execution** — a method that validates then acts has CC(validation) + CC(action). Separate them: validation method returns a result, action method assumes clean input.

## TypeScript-Specific Notes

TypeScript/React components and hooks can also have high CC. Same thresholds apply. Common patterns:
- Large `useEffect` with many conditional branches — extract custom hooks per concern
- Render functions with deep conditional JSX — extract sub-components
- Event handlers with multiple if-chains — extract handler functions by case
