# Company Communication Standards

All inter-agent communication in the AI company uses structured templates. Free-form chatter is not communication — it is noise. Use the templates or say nothing.

## Structured or Silent

Every message between agents uses one of the four templates below:
1. Task Assignment — manager to agent
2. Handoff — agent to downstream reviewer/agent
3. Change Request — reviewer to agent
4. Escalation — agent to manager

If your message does not fit one of these templates, ask yourself: is this a task, a handoff, a change request, or an escalation? Find the right template and use it. If it is none of these, it does not need to be communicated.

## Lead With the Verdict

The first line of every output states the conclusion:

```
STATUS: COMPLETE
STATUS: FAIL — 3 issues found
STATUS: BLOCKED — waiting on TASK-001
STATUS: CHANGES_REQUESTED — 2 MUST_FIX issues
```

Details follow. The reader knows the status before reading anything else.

## Specificity Over Volume

A 3-line change request that names the exact file, line, and fix is worth more than a 3-page analysis of general concerns.

Bad: "The error handling in this service could be improved. There are several places where exceptions might not be caught properly and this could lead to issues in production."

Good: "FeatureService.cs:87 — bare `catch (Exception e)` swallows the error and returns null. Rethrow as domain error: `throw new FeatureException("Operation failed", e)`"

If you cannot cite a file and line, collect more evidence before filing a finding.

## No Circular Escalation

If an issue has been escalated and a decision has been made by the appropriate authority, it is settled. You cannot re-escalate the same issue hoping for a different answer.

If **new evidence emerges** that changes the picture — file a new escalation with the new evidence explicitly noted: "New information since ESC-001: ..."

## Silence Means Working

Agents do not produce status updates unless something has changed. The task state in `tasks.md` is the single source of truth for current status. There is no need to announce "I am still working on this." Change the state only when state actually changes.

---

## Templates

### Template 1: Task Assignment

```
## TASK: [TASK-ID]

**Assigned to:** [Agent Role]
**From:** [Manager Role]
**Project:** [PROJ-ID]
**Priority:** HIGH / MEDIUM / LOW
**Depends on:** [TASK-IDs or "none"]

### Objective
[One sentence: what this task produces]

### Context
[Relevant upstream handoff summaries, architectural decisions, CEO constraints]

### Acceptance Criteria
- [ ] [Specific, testable criterion 1]
- [ ] [Specific, testable criterion 2]
- [ ] [...]

### Constraints
[Any non-obvious restrictions: follow pattern X, no library Y, must match existing Z]
```

### Template 2: Handoff

```
## HANDOFF: [TASK-ID]

**Status:** COMPLETE / PARTIAL (with explanation)
**Agent:** [Your Role]
**Project:** [PROJ-ID]

### Changes
- [file path] — [what changed and why]
- [file path] — [what changed and why]

### API Surface (backend tasks)
- [METHOD /route — description]

### Database Changes (if any)
- [table/column changes]

### Assumptions
- [Any assumption made where the spec was silent]

### Dependencies Satisfied
- [TASK-ID] ✓ — [brief note]

### Downstream Needs
- [What the next agent needs to know before starting their work]

### Security Considerations
- [Any security decisions made, or "None — no new attack surface"]

### Known Limitations
- [Anything intentionally left out of scope with justification]
```

### Template 3: Change Request

```
## CHANGE REQUEST: [TASK-ID]-CR-[N]

**From:** [Reviewer Role]
**To:** [Agent Role]
**Project:** [PROJ-ID]
**Severity:** MUST_FIX / SHOULD_FIX

### Issues

1. **[Issue title]**
   - Location: [file:line]
   - Problem: [what is wrong and why]
   - Fix: [exactly what to do]

2. **[Issue title]**
   - Location: [file:line]
   - Problem: [what is wrong and why]
   - Fix: [exactly what to do]

### Resubmit To
[Reviewer role] for re-review after all MUST_FIX items resolved.
```

### Template 4: Escalation

```
## ESCALATION: [ESC-ID]

**From:** [Your Role]
**To:** [Manager Role]
**Project:** [PROJ-ID]
**Task:** [TASK-ID]
**Priority:** BLOCKER / HIGH / MEDIUM

### Blocker
[One paragraph: what is blocking progress, why it cannot be resolved at your level]

### Options
A) [Option A — brief description and trade-offs]
B) [Option B — brief description and trade-offs]

### My Recommendation
[Which option you recommend and why]

### Urgency
[What downstream work is blocked until this is resolved]
```
