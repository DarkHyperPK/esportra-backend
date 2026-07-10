---
name: clean-architecture
description: Patterns for maintainable, testable code organization
---

# Clean Architecture Skill

Structure code so it's easy to understand, test, and change.

## When to Use

- Creating new features or modules
- Deciding where code should live
- Designing interfaces between components
- Refactoring tangled code

## Core Principles

### Dependency Direction
Dependencies point inward — outer layers depend on inner, never reverse.

```
[API/Endpoints] → [Core/Domain] ← [Infrastructure]
```

- **Core** has no external dependencies
- **Infrastructure** implements Core interfaces
- **API** orchestrates and exposes

### Single Responsibility
Each unit (function, class, module) does one thing.

```csharp
// Good: clear responsibilities
public class TournamentService { /* tournament logic */ }
public class TournamentRepository { /* data access */ }
public class TournamentValidator { /* validation rules */ }

// Bad: god class
public class TournamentManager { /* everything */ }
```

### Explicit Dependencies
Dependencies are injected, not created or discovered.

```csharp
// Good: explicit
public class TournamentService(ITournamentRepository repo, ITimeProvider time)

// Bad: hidden
public class TournamentService()
{
    private readonly repo = ServiceLocator.Get<ITournamentRepository>();
}
```

## File Organization

```
src/
  Esportra.Core/           # Domain logic, no external deps
    Tournaments/
      TournamentService.cs
      BracketCalculator.cs
  Esportra.Infrastructure/ # External concerns
    Database/
    Email/
  Esportra.Api/            # Entry points
    Endpoints/
    Middleware/
```

## Patterns

### Request → Handler → Response
```csharp
// Endpoint receives request
app.MapPost("/api/tournaments", async (CreateTournamentRequest request, ...) =>
{
    // Handler processes
    var result = await service.CreateTournament(request);
    
    // Response returned
    return result.Match(
        success => Results.Created($"/api/tournaments/{success.Id}", success),
        error => Results.BadRequest(error));
});
```

### Result Types Over Exceptions
```csharp
// Return success or failure explicitly
public async Task<Result<Tournament, Error>> CreateTournament(...)
{
    if (!IsValid(request))
        return Error.Validation("Invalid tournament data");
    
    var tournament = new Tournament(...);
    await repo.Save(tournament);
    return tournament;
}
```

### Small, Focused Functions
```csharp
// Each function does one thing
public int CalculateMatchCount(int participantCount) =>
    participantCount - 1; // Single elimination

public List<Match> GenerateBracket(int matchCount) =>
    Enumerable.Range(1, matchCount)
        .Select(i => new Match(i))
        .ToList();
```

## Anti-Patterns to Avoid

- **Service locator** — hides dependencies
- **God classes** — too many responsibilities
- **Circular dependencies** — tangled structure
- **Anemic domain** — logic outside domain objects
- **Leaky abstractions** — infrastructure details in core
