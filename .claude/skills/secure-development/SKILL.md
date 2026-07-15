---
name: secure-development
description: Security-first development practices for APIs and data handling
---

# Secure Development Skill

Apply security practices throughout development, not just at review time.

## When to Use

- Building new endpoints or handlers
- Working with user input
- Handling authentication/authorization
- Storing or transmitting sensitive data
- Integrating external services

## Input Handling

### Validate Early
```csharp
// At the boundary, not deep in business logic
app.MapPost("/api/resource", async (CreateRequest request, ...) =>
{
    // Validation happens here, at the entry point
    if (!IsValid(request)) return Results.BadRequest();
    
    // Business logic receives validated data
    return await service.Create(request);
});
```

### Use Strong Types
```csharp
// Prefer this
public record UserId(Guid Value);
public async Task<User> GetUser(UserId id)

// Over this
public async Task<User> GetUser(string id) // Could be anything
```

## Database Queries

### Always Parameterize
```csharp
// Correct
await connection.QueryAsync<User>(
    "SELECT * FROM users WHERE id = @Id",
    new { Id = userId });

// Never
await connection.QueryAsync<User>(
    $"SELECT * FROM users WHERE id = '{userId}'"); // SQL injection
```

### Array Parameters
```csharp
// Use ANY with Guid[]
await connection.QueryAsync<User>(
    "SELECT * FROM users WHERE id = ANY(@Ids)",
    new { Ids = userIds.ToArray() });
```

## Authorization

### Check at Boundaries
```csharp
app.MapGet("/api/admin/users", async (HttpContext ctx, ...) =>
{
    var userContext = ctx.Items["UserContext"] as UserContext;
    if (!userContext.HasPermission("admin:read"))
        return Results.Forbid();
    
    // Authorized code here
});
```

### Don't Trust Client Claims
```csharp
// Get identity from server-verified token, not request body
var userId = ctx.User.FindFirst("sub")?.Value;
// NOT: var userId = request.UserId;
```

## Error Handling

### Safe Errors to Clients
```csharp
// Return generic error
return Results.Problem(
    title: "An error occurred",
    statusCode: 500);

// Log details server-side
logger.LogError(ex, "Failed to process request {RequestId}", requestId);
```

## Secrets

### Environment Variables Only
```csharp
// Correct
var apiKey = Environment.GetEnvironmentVariable("API_KEY");

// Never in code
var apiKey = "sk-abc123..."; // Hardcoded secret
```

## Checklist Before Commit

- [ ] All user input is validated
- [ ] SQL queries are parameterized
- [ ] Authorization is checked at entry points
- [ ] Errors don't leak internal details
- [ ] No secrets in code
- [ ] No PII in logs
