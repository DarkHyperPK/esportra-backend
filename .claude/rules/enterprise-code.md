# Enterprise Code Rules

Code must be **robust by design**, not robust by patch. When behavior is wrong, fix the root cause in the data path. Never add a compensating helper, resolver, or workaround that papers over fragile logic — those decay the moment the underlying code changes.

## Banned: patchy patterns

### 1. Compensating helpers / resolvers
A function created solely to paper over a defect in existing logic — e.g., a re-parse that hides a lossy conversion, or a resolver that re-derives state another layer already owns.

**Ban test:** if the underlying code were corrected, would this function disappear? If yes — it is a patch. Fix the underlying code instead.

### 2. Duplicated persisted state
Mirroring stored values into memory or DTOs and manually keeping them in sync instead of reading from the source of truth (repository/DB).

### 3. String round-trip comparisons
Comparing a raw input against a value that was reformatted (serialized, normalized, or timezone-shifted). Formatting is lossy — compare canonical forms or the underlying value, never two different representations.

### 4. Symptom patches
Fixing where the bug is visible instead of where the data first diverges (e.g., compensating at the endpoint for bad data that should be rejected at the boundary or fixed in storage).

### 5. Workarounds that assume other-layer behavior
Logic that compensates for what the client or DB *might* do. Verify the other layers; if behavior is confirmed, code against it directly. If a layer is unreachable, label the assumption — do not patch for it.

### 6. Architecture bypasses
Business logic leaking into endpoints, raw SQL string interpolation, skipping the repository/service boundary, or bypassing validation at the entry point.

## Required: enterprise patterns

### Single source of truth
- Persisted data: read and write through repositories — one owner per aggregate.
- Derived values: computed from the source, never copied and manually synced.

### Normalize at the boundary
Validate and convert input to its canonical form at the entry point (endpoint/controller), before business logic. Store, return, and compare canonical values.

### Atomicity with truthful outcomes
Each operation that can partially fail reports the true outcome (e.g., "2 of 5 saved") instead of a blanket error. Transactions where all-or-nothing is required; explicit per-item handling where it is not.

### Root-cause discipline
Locate the first hop where data diverges from the intended workflow and fix it there. Trace the full path — endpoint → service → repository → schema — before concluding. (See the `root-cause-diagnosis` skill.)

### Real functions are not helpers
A named, single-purpose, tested method that models domain logic and lives in the established structure (`Esportra.Core`, `Esportra.Infrastructure`) is a **domain function** — required, not banned. The ban targets ad-hoc methods that exist only to compensate for defects.

### Deterministic and idempotent
Same inputs → same outcomes; repeating an operation (retry, double-submit) is safe and converges to the same state.

### Security stays blocking
Enterprise-grade does not override `security.md` and `secure-development`: validate at boundaries, parameterize SQL, enforce authorization server-side, never weaken RLS or triggers to unblock a fix.

## Self-audit before commit

- [ ] Is this the root cause, or a patch over it? (Apply the ban test.)
- [ ] Is persisted state duplicated anywhere instead of read from the repository?
- [ ] Are we comparing canonical-to-canonical? (No string round-trips.)
- [ ] Does the fix survive a re-query, a restart, and a repeated operation?
- [ ] Are error paths truthful (partial failure reported as partial)?
- [ ] Does the logic follow the layer boundaries (Core / Infrastructure / Api)?
- [ ] Is the operation idempotent / deterministic?
- [ ] Would this break if the client or DB stopped being forgiving?
- [ ] Do tests cover the edge that motivated the change?
- [ ] Build, tests, and the security checklist pass?

## Consequence

A patchy fix caught in review is rejected and redone at the root cause. This is not negotiable.
