---
name: code-health
description: Assess code quality and identify improvement areas
allowed_tools: ["Bash", "Read", "Grep", "Glob"]
---

# /code-health

Assess overall code health and identify areas needing attention.

## Metrics to Check

### Complexity
- Files over 800 lines
- Functions over 50 lines
- Deep nesting (3+ levels)
- Long parameter lists (5+)

### Duplication
- Copy-pasted code blocks
- Similar patterns that could be unified

### Naming
- Single-letter variables (except loops)
- Unclear abbreviations
- Names that don't match behavior

### Structure
- God classes doing too much
- Feature envy (methods using other class's data)
- Circular dependencies

## Commands

```bash
# Large files
find src -name "*.cs" -exec wc -l {} + | sort -rn | head -20

# Long functions (rough heuristic)
grep -rn "public\|private\|protected\|internal" src --include="*.cs" | head -50

# TODO/HACK comments
grep -rn "TODO\|HACK\|FIXME\|XXX" src --include="*.cs"
```

## Output

Prioritized list:
1. **Critical** — blocking issues
2. **Important** — should address soon
3. **Minor** — nice to have
4. **Tech debt** — track for future
