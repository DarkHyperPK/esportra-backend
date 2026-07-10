# Refactoring Rules

Refactor to improve structure without changing behavior.

## When to Refactor

- Before adding a feature — make the change easy, then make the easy change
- After getting tests passing — RED → GREEN → REFACTOR
- When you see duplication that obscures intent
- When a name no longer reflects what something does

## When NOT to Refactor

- Don't refactor while fixing a bug — fix first, refactor separately
- Don't refactor without tests covering the affected code
- Don't refactor code you don't need to change
- Don't "improve" working code just because you'd write it differently

## Safe Refactoring

1. **Verify tests exist** — if not, add characterization tests first
2. **Small steps** — one rename, one extract, one move at a time
3. **Run tests after each step** — catch breakage immediately
4. **Commit frequently** — easy to revert if something goes wrong

## Common Refactorings

### Extract Method
When a code block does one thing that can be named:
```csharp
// Before: inline validation
if (string.IsNullOrEmpty(email) || !email.Contains("@")) { ... }

// After: extracted with intent-revealing name
if (!IsValidEmail(email)) { ... }
```

### Rename
When a name doesn't match what something does — just rename it. Don't preserve old names for backwards compatibility unless truly needed.

### Inline
When an abstraction adds complexity without value — inline it and delete the indirection.

### Move
When code is in the wrong place — move it to where it belongs. Don't leave forwarding stubs.

### Replace Conditional with Polymorphism
When switch/if chains select behavior based on type — use polymorphism instead.

## What NOT to Do

- Don't rename to `_unused` — delete it
- Don't add `// removed` comments — just remove
- Don't re-export for backwards compatibility — update call sites
- Don't create abstractions "for testability" — test through public interfaces
- Don't refactor and change behavior in the same commit
