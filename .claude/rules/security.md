# Security Rules

These rules are **blocking** — violations must be fixed before proceeding.

## Input Validation

- Validate all external input at system boundaries (API endpoints, message handlers, file uploads)
- Use allowlists over denylists for input validation
- Sanitize data before use in SQL, HTML, shell commands, or file paths
- Reject unexpected input types early — don't coerce or assume

## SQL & Database

- **Always** use parameterized queries — never concatenate user input into SQL
- Use `= ANY(@ids)` with `Guid[]` for array parameters, not string interpolation
- Apply principle of least privilege — connections should have minimal required permissions
- Every table must have RLS policies — default deny, explicit allow

## Authentication & Authorization

- Use framework auth handlers — no custom token parsing or JWT validation
- Check authorization at the handler/endpoint boundary, not deep in business logic
- Never trust client-provided identity claims without server-side verification
- Fail closed — deny access if authorization state is uncertain

## Secrets & Credentials

- Never hardcode secrets — environment variables only
- Never log tokens, passwords, API keys, or PII
- Never commit secrets to version control (even in tests)
- Rotate credentials immediately if exposed

## Error Handling

- Return safe, generic errors to clients — no stack traces, SQL errors, or internal paths
- Log detailed errors server-side with correlation IDs
- Don't leak information through error message differences (timing, content)

## Dependencies

- Pin dependency versions
- Review dependency changes for supply chain risk
- Prefer well-maintained packages with security track records

## Before Committing Security-Sensitive Code

1. Grep for hardcoded secrets: `grep -rE "(password|secret|key|token)\s*[:=]" --include="*.cs"`
2. Check for SQL concatenation: `grep -rE "\+.*sql|sql.*\+" --include="*.cs"`
3. Verify RLS policies exist for new tables
4. Confirm no PII in logs
