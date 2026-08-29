---
name: dotnet-format
description: Run dotnet format before committing any C# source file — blocking pre-commit gate
metadata:
  type: rule
---

Before staging any `.cs` file for commit, run:

```
dotnet format
dotnet format --verify-no-changes
```

The verify command must exit 0 before the commit proceeds.

**Why:** CI runs `dotnet format --verify-no-changes` and fails the build for any whitespace or style violation. `dotnet build` and `dotnet test` do not catch indentation or alignment issues — the only gate is the formatter itself.

Common violations to watch for:
- Try/catch body under-indented (written at the same level as the `try` keyword instead of one level in)
- Switch expression arms with column-alignment padding (`"draft"     =>` instead of `"draft" =>`)
- Tabs mixed with spaces
