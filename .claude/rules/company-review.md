# Company Review Standards

Reviews are gates, not ceremonies. A review that does not find real issues is only valuable if it explains what was checked. A review that approves everything without inspection is worse than no review.

## QA is Adversarial

QA agents do not verify that code works. They try to prove it does not.

The job of a QA agent is to find the conditions under which the implementation breaks. This means:

**What QA actively hunts:**
- Missing edge cases — what happens with empty input, null values, boundary values (0, -1, MAX_INT)?
- Broken error paths — does the error handler actually run? Does it return the right status code?
- Unvalidated input — can a user submit negative amounts, SQL fragments, or paths outside expected bounds?
- Missing authorization — does the endpoint check the right permission? What happens with a valid but wrong-role user?
- Race conditions — if two users hit this simultaneously, does state corrupt?
- State corruption — does repeated operation converge or accumulate?
- Inconsistency with acceptance criteria — does the implementation match what was accepted, criterion by criterion?

**QA output format — per criterion:**

```
Criterion: "POST /api/feature validates amount > 0"
Status: FAIL
Evidence: Submitted {"amount": -1}, received HTTP 200 with id. No validation exists.
Fix needed: Add guard clause in FeatureEndpoints.cs before calling service.
```

A blanket "no issues found" without criterion-by-criterion evidence is rejected. Rerun with evidence.

## CTO Audit is Architectural

The CTO does not re-run QA checks. By the time CTO audits, QA has already verified correctness. CTO focuses exclusively on:

**Architecture fit:**
- Does this implementation follow the established patterns in the codebase?
- Does it fight the existing architecture or extend it naturally?
- Are dependencies pointing in the right direction (API → Core ← Infrastructure)?

**Scalability and maintainability:**
- Will this be understandable in 6 months?
- Will this hold under 10× the expected load?
- Is it the simplest solution that satisfies the requirements, or has complexity been introduced unnecessarily?

**Technical debt:**
- Has debt been incurred? If yes, is it justified and documented in `decisions.md`?
- Are there CC violations that will make the code hard to maintain?

**Completeness:**
- Does the implementation actually satisfy the original requirements in the proposal?
- Are there gaps between "what was built" and "what was asked for"?

CTO change requests use the standard Change Request template with MUST_FIX / SHOULD_FIX severity.

## Every Review Produces Actionable Output

A review finding must contain three things:
1. **What** is wrong (specific claim)
2. **Where** it is (file:line or component)
3. **What** the fix looks like (concrete direction, not "consider improving")

A clean review must explain what was checked:
- "Reviewed: input validation on all request parameters, SQL parameterization, authorization check before sensitive operation, error response content. Result: no issues found."

This is acceptable. "Looks good" is not.

## No Rubber Stamps

A reviewer who approves everything is a liability, not an asset. If you reviewed the work and found nothing, the review is only credible if you explain what you looked at.

Rubber-stamp reviews will be treated as no-review and the work will be sent back for genuine inspection.

## Review Retry Limits

| Reviewer | Max retries before escalation |
|----------|------------------------------|
| QA Agent | 2 → QA Lead |
| QA Lead | 2 → CTO |
| CTO | 3 → CEO |
| CPO | 2 → CEO |

After max retries, the escalation must explain: what the recurring issue is, what was tried, and why it is not resolving.
