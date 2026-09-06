# Company Quality Standards

These are the quality bars for all AI company agents. They are **blocking** — work that does not meet these standards is not complete.

## Read Before You Write

Before writing a single line of code, read:
- How existing endpoints in the same domain are structured (check `src/Esportra.Api/Endpoints/`)
- How existing migrations in the same area are written (check `src/Esportra.Infrastructure/Migrations/Scripts/`)
- How existing tests for similar features are organized (check `src/Esportra.Api.Tests/` and `src/Esportra.Core.Tests/`)
- What naming conventions are used for the domain you are working in

Deviating from established patterns without explicit architectural justification written in your handoff is a quality violation.

## Test What You Build

- `dotnet build` must pass — minimum bar for any implementation handoff
- If you added business logic, you must add tests
- If you added an endpoint, you must add integration tests
- If you added a migration, you must verify idempotency
- "I didn't have time for tests" is not a valid handoff state — the task is IN_PROGRESS, not HANDOFF

## Security Is Everyone's Job

Every agent, regardless of role, must flag security concerns encountered during their work. You do not wait for the Security QA agent to catch it later.

If you are a Frontend Engineer and you notice an endpoint returning sensitive data: escalate it now, not later.

Refer to `.claude/rules/security.md` for the specific security rules — they apply to all company agents.

## One Task, One Concern

If you are working on TASK-003 (backend API), you do not:
- Refactor unrelated code in the same file
- Fix a bug you noticed in a different module
- Update documentation for a different feature
- Add "nice to have" improvements outside scope

Instead: write the observation to the project's `decisions.md` with your suggestion. Stay in your lane. Side work creates merge conflicts, unexpected behavior changes, and untested paths.

## Definition of Done — Per Role

### Senior Software Architect
- [ ] Architecture document written: components, interfaces, data flow, dependency directions
- [ ] Reviewed against existing patterns in the codebase
- [ ] Trade-offs documented for significant decisions
- [ ] Handoff includes enough detail for any engineer to implement without ambiguity

### Senior Backend Engineer
- [ ] `dotnet build` passes
- [ ] Unit/integration tests written and passing
- [ ] All SQL uses parameterized queries
- [ ] RLS policies exist for any new tables
- [ ] Structured handoff filed with API surface, files changed, assumptions

### Senior Frontend Engineer
- [ ] `dotnet build` passes (or frontend build passes)
- [ ] Components render without errors
- [ ] Existing UI patterns followed
- [ ] Structured handoff filed with component list, files changed, assumptions

### Senior Database Engineer
- [ ] Migration is idempotent (IF NOT EXISTS, ON CONFLICT DO NOTHING, ADD COLUMN IF NOT EXISTS)
- [ ] Migration tested against current schema
- [ ] Rollback approach documented in handoff
- [ ] Naming follows snake_case convention (not external schemas — check `.claude/rules/external-schema-migrations.md`)

### Senior DevOps Engineer
- [ ] Infrastructure changes pushed to staging and staging CI run passes — not just "looks correct on paper"
- [ ] For CI workflow or fixture changes specifically: confirm the affected CI job runs and exits green on staging before HANDOFF
- [ ] No secrets hardcoded
- [ ] Changes documented in handoff

### Senior UI/UX Designer
- [ ] Component specs are specific enough for a frontend engineer to implement without guessing
- [ ] Design decisions reference existing patterns where applicable
- [ ] Interaction flows documented for non-obvious UX

### QA Agent (any specialization)
- [ ] Every acceptance criterion verified independently — listed one by one with PASS/FAIL
- [ ] Edge cases tested (empty inputs, boundary values, unauthorized access, concurrent requests)
- [ ] Failure evidence is specific: test name, input, expected output, actual output
- [ ] Clean pass explains what was tested — not just "looks good"

### QA Lead
- [ ] All specialized QA agents ran and reported
- [ ] All MUST_FIX issues resolved before escalating upward
- [ ] Consolidated QA report filed with pass/fail per criterion
- [ ] For CI/infrastructure projects: static file verification alone is not sufficient — confirm staging CI run is green before reporting PASS. Reading files proves structure; a passing CI run proves it works.

### CTO (audit)
- [ ] Architecture adherence verified — implementation matches the architecture handoff
- [ ] Code quality reviewed — no CC violations, no god classes, no security holes
- [ ] No regression in existing features — checked related endpoints and services
- [ ] Implementation satisfies original requirements — verified against proposal acceptance criteria
- [ ] Technical debt noted in decisions.md if any was incurred (with justification)

## Dotnet-Specific Quality Rules

These inherit from `.claude/rules/dotnet-format.md` and `.claude/rules/clean-code.md`:
- Run `dotnet format` before any `.cs` handoff
- Run `dotnet format --verify-no-changes` — must exit 0
- Functions < 50 lines
- Cyclomatic complexity ≤ 10 (hard), ≤ 7 preferred
- No `dynamic`, no EF Core, no raw SQL string concatenation
