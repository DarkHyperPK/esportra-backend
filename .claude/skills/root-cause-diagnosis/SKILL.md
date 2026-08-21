---
name: root-cause-diagnosis
description: >-
  Full multi-angle root-cause diagnosis protocol. Never pattern-match a
  symptom to a fix: understand the feature's intended workflow first, trace
  the complete data path (frontend -> API -> database -> back), audit every
  assumption with evidence, distinguish real defects from intended workflow,
  and validate any fix across every layer before applying it.
  TRIGGER when: user reports a bug, regression, error, or unexpected
  behavior, or asks to find/diagnose/explain why something fails or works.
  DO NOT TRIGGER when: user asks a pure informational question, or the task
  is a feature build with no reported defect (but use the layer checklists
  while building to avoid introducing defects).
origin: community
---

# Root-Cause Diagnosis — Prove It, Then Fix It

Never pattern-match a symptom to a fix. Find the actual root cause and prove it from multiple angles before touching code. A fix that isn't validated against the real data path is a guess.

## When to Use

- User reports a bug, regression, error, or unexpected behavior
- User asks "why does X fail / behave this way?" or "which commit caused this?"
- Reviewing a change for correctness (adversarial review of your own or others' work)
- Investigating an incident or intermittent failure
- Before escalating a "fix" that only papers over the visible symptom

**Do not use** for pure informational questions or feature builds with no reported defect — but keep the layer checklists in mind while building to avoid introducing defects.

## Core Principles

1. **Feature intent first.** Know how the feature is *supposed* to work before judging how it works. Read docs, commit history, tests, and PRs for intent. If the behavior could be a deliberate guard or limit, ask the user instead of declaring a bug.
2. **Zero unverified assumptions.** Every hypothesis rests on assumptions. Enumerate them, then confirm or disprove each with code, a repro, or a query. Unverifiable assumptions make the finding conditional — label it as such.
3. **Trace the whole round trip.** A defect is often visible at a different layer than where the data first goes wrong. Trace frontend → API → database → response → re-render.
4. **Root cause ≠ symptom location.** Find where the data first diverges from what should happen. Fixing the symptom without the divergence point is a band-aid.
5. **Fix = validated from every angle.** A fix must hold for the frontend rendering path, the API contract, the DB schema, and the feature intent — and must not break edges the current code already handles.
6. **Scope discipline.** Fix only genuine correctness/security defects or gaps that block intended behavior. Report adjacent findings separately, without editing them.
7. **Ask before assuming.** When the symptom, environment, or expected behavior is ambiguous, ask the user — a wrong diagnosis costs more than a clarifying question.

## Protocol

### Phase 0 — Clarify the symptom (ask before deep-diving when needed)

- What exactly did you do? What did you expect? What did you see?
- Which environment (local / staging / prod), user role, and data?
- Every time or intermittent? Any error messages, network, or console output?
- Is the behavior perhaps intended (a guard, limit, or workflow the user forgot)?

### Phase 1 — Establish the intended workflow

- What is the feature for and who uses it? Walk the happy path end-to-end.
- Read the surrounding code, not just the reported lines: parent components, hooks, consumers of the same state.
- Check commit history and tests around the feature for stated intent.
- Write down the expected behavior explicitly — you'll compare against it.

### Phase 2 — Trace the full data path

Follow the actual transformation at each layer (see the layer checklist below). Capture the exact inputs and outputs at each hop. Note anything that can transform the value: parsing, validation, normalization, transactions, retries, column types, triggers, defaults, timezones.

### Phase 3 — Hypothesis ledger & assumption audit

- List every candidate root cause as a hypothesis.
- Under each, write the specific assumptions it rests on (e.g., "the backend stores the value unchanged", "the column is timestamptz", "this effect re-runs when X changes").
- Disprove/confirm each assumption with evidence. Strike hypotheses whose assumptions fail.
- If a layer is unreachable (e.g., no frontend locally), say so explicitly and mark the finding conditional — never assume what that layer does.

### Phase 4 — Reproduce with the smallest concrete steps

- Reproduce before fixing; capture exact inputs and observed outputs.
- Prefer an offline harness for logic you can isolate (date math, validators, formatters) rather than exercising live environments.
- Never run repros that mutate shared/live environments (staging/prod data) without explicit permission.

### Phase 5 — Locate the root cause

- Identify the first hop where the data diverges from the expected workflow — that is the root cause. Everything downstream is where the bug merely becomes visible.
- Check whether the divergence is a defect or intended workflow before committing to a fix.

### Phase 6 — Validate the fix from every angle

For each candidate fix, run it through the fix validation matrix below. Prefer the smallest change that restores the intended behavior everywhere.

### Phase 7 — Verify and close

- Re-run the original repro: symptom must be gone, no new ones introduced.
- Run the project's typecheck and relevant tests.
- For RLS/permission-sensitive behavior, verify as the affected (non-admin) role.
- State what you checked (per layer), the root cause with evidence, what you changed, and what you deliberately did not change.

## Layer-by-layer checklist

### API layer (.NET / endpoints)

- [ ] Parsing: strictness, invalid-input handling, nullable/empty semantics.
- [ ] Normalization: is the value stored exactly as sent, or transformed?
- [ ] Transactions: per-request atomicity — what happens on partial failure?
- [ ] Retries: does the client retry, on which status codes, with what side effects?
- [ ] Error responses: accurate? Do they overstate or understate what happened?
- [ ] Authorization/ownership checks: enforced server-side, never trusted from the client.

### Services & domain logic

- [ ] Pure functions: boundary cases, off-by-one, empty input, null vs zero-value.
- [ ] Time: timezone handling, DST gaps, precision (ticks/seconds/millis), UTC boundaries.
- [ ] Idempotency: what happens if the same operation runs twice (retries, double-submit)?

### Database (Supabase/Postgres)

- [ ] Column types: timestamptz vs timestamp, precision, defaults.
- [ ] Constraints/triggers: anything that transforms or rejects values.
- [ ] RLS: does the affected role actually have the access the feature assumes?
- [ ] Check both migrations and any runtime-managed schema.

### Cross-cutting

- [ ] Timezones: input → storage → display round-trips; DST gaps; seconds precision.
- [ ] Empty/null vs zero-value semantics at every boundary.
- [ ] Concurrency: two writes to the same row, optimistic vs pessimistic locking.

## Defect vs Intended Workflow

Before labeling anything a bug, ask:

- Is there a guard, limit, or constraint that is *supposed* to produce this behavior?
- Does a test, commit message, or PR description document this behavior as intended?
- Would "fixing" it break a documented or deliberate workflow?

When in doubt, present the behavior to the user with your analysis and ask — do not silently "fix" intended behavior.

## Fix Validation Matrix

For each candidate fix, answer:

| Angle | Question |
|---|---|
| API | Does it stay within the existing contract, or does it require a contract change (and is that authorized)? |
| Database | Compatible with the schema, constraints, triggers, and RLS? |
| Frontend | Does it hold for every UI path that reads this state? Any edge the current code handled that this breaks? |
| Feature intent | Does it make the feature behave the way it is supposed to — no more, no less? |
| Failure modes | What happens on partial failure, retry, double-submit, stale data, invalid input? |

## Final Report Template

1. **Symptom** — what the user reported, in their terms.
2. **What I checked** — per layer, with evidence (code paths read, repros run, queries).
3. **Root cause** — the first hop where data diverged, with the proof.
4. **Fix** — what changed, why it is correct from every angle, what it intentionally does not change.
5. **Out-of-scope findings** — adjacent issues, reported without editing.
6. **Open questions** — anything that remains unverified (e.g., a layer you could not reach).

## Anti-Patterns

- **Pattern-matching**: "I've seen this error before, so the fix is X." Symptoms repeat; root causes rarely do.
- **Blaming the last change**: the regression may be recent, but the defect may be much older and merely exposed.
- **Single-layer analysis**: concluding from the API alone when the value is transformed at the DB or client.
- **Unverified assumptions**: "the DB normalizes this", "this endpoint re-validates" — without reading the code.
- **Fixing the symptom**: patching where the bug is visible instead of where the data diverges.
- **Scope creep**: hardening unrelated code while fixing a bug.
- **Silently "fixing" intended behavior**: changing a deliberate guard because it looked wrong.
- **Skipping verification**: fixing without re-running the repro, typecheck, and tests.
