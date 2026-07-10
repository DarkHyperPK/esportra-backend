# Clean Code Rules

Write code that's easy to read, change, and delete.

## Simplicity

- Solve the problem at hand — don't design for hypothetical future requirements
- Prefer obvious code over clever code
- Three similar lines beats a premature abstraction
- Delete dead code — don't comment it out "just in case"

## Functions

- One responsibility per function
- Keep functions under 50 lines — if longer, it's doing too much
- Limit parameters to 4 or fewer — use objects for more
- Avoid boolean parameters that change behavior (split into two functions)

## Naming

- Names should reveal intent — avoid abbreviations except domain terms
- Functions: verb phrases (`calculateScore`, `validateInput`)
- Variables: noun phrases describing the value
- Booleans: `is`, `has`, `can`, `should` prefixes
- Don't encode type in name (`strName`, `listItems`)

## Comments

- Default to no comments — well-named code is self-documenting
- Only comment the WHY when non-obvious: constraints, workarounds, surprising behavior
- Never comment WHAT (the code shows that) or reference the current task

## Files & Structure

- Keep files under 800 lines
- One concept per file
- Group related code together
- Consistent file organization across the codebase

## Dependencies

- Depend on abstractions at boundaries, concrete types internally
- Keep dependency graphs shallow — avoid deep chains
- Make dependencies explicit (constructor injection), not hidden (service locator)

## Error Handling

- Handle errors at the appropriate level — not too early, not too late
- Use exceptions for exceptional cases, return values for expected outcomes
- Don't swallow exceptions silently
- Clean up resources in finally blocks or using statements

## Tests

- Test behavior, not implementation
- One assertion per test when possible
- Tests should be fast, isolated, and deterministic
- Name tests by behavior: `MethodName_ExpectedResult_WhenCondition`
