---
name: company-status
description: CEO dashboard — shows all active projects, current task states, blockers, escalations, and pending approvals. Read-only. No agents dispatched.
---

# /company-status — CEO Dashboard

You display a concise, actionable dashboard of the AI company's current state. You read files — you do not dispatch agents, modify state, or take any action.

## Data Sources

Read from `.claude/company/projects/`:
- Each `PROJ-XXX/proposal.md` — project name, status, CEO objective
- Each `PROJ-XXX/tasks.md` — task states, owners, blockers
- Each `PROJ-XXX/escalations.md` — open escalations
- `.claude/company/memory/INDEX.md` — recently completed projects (if exists)

## Output Format

```
# AI Company Status
[date and time]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
## ACTIVE PROJECTS
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

### PROJ-001 — [Feature Name]
Status: [pipeline stage, e.g. "STAGE 6: Implementation"]
Approved: [date]

Tasks:
  ✅ TASK-001  Architecture         (Senior Software Architect)   COMPLETED
  ✅ TASK-002  Migration            (Senior Database Engineer)    COMPLETED
  🔄 TASK-003  Backend API          (Senior Backend Engineer)     IN_PROGRESS
  ⏳ TASK-004  Frontend             (Senior Frontend Engineer)    BLOCKED → waiting on TASK-003
  ⏳ TASK-005  QA                   (QA Lead)                     PLANNED

Escalations: None
Pending CEO decisions: None

---

### PROJ-002 — [Feature Name]
Status: AWAITING_CEO_APPROVAL (proposal ready)
[Proposal is ready — respond with /company to continue or see proposal.md]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
## BLOCKERS & ESCALATIONS
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

[None] / [List any open blockers or escalations with project reference]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
## RECENTLY COMPLETED
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

[From .claude/company/memory/INDEX.md if it exists, else "None yet"]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

## Task State Icons

| Icon | State |
|------|-------|
| ✅ | COMPLETED or APPROVED |
| 🔄 | IN_PROGRESS or IN_REVIEW or QA |
| ⏳ | PLANNED or ASSIGNED |
| 🔴 | BLOCKED or ESCALATED |
| ❌ | FAILED or REJECTED |
| ⚠️ | CHANGES_REQUESTED |

## If No Active Projects

```
# AI Company Status

No active projects.

To start one: /company "your objective here"
```

## If .claude/company/ Does Not Exist

```
# AI Company Status

Company runtime directory not yet initialized.

Start your first project with: /company "your objective here"
The directory will be created automatically.
```
