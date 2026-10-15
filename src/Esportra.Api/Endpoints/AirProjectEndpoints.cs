using Dapper;
using System.Text.Json;
using Esportra.Api.Services;
using Esportra.Contracts.Air;
using Esportra.Contracts.Database;
using Esportra.Core.Air;

namespace Esportra.Api.Endpoints;

public static class AirProjectEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void MapAirProjectEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/api/air").RequireAuthorization("Authenticated").WithTags("Esportra Air projects");
        routes.MapGet("/workspaces/{workspaceId:guid}/projects", List);
        routes.MapPost("/workspaces/{workspaceId:guid}/projects", Create);
        routes.MapGet("/projects/{projectId:guid}", Get);
        routes.MapPut("/projects/{projectId:guid}", Update);
        routes.MapDelete("/projects/{projectId:guid}", Archive);
    }
    private static async Task<IResult> List(Guid workspaceId, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var connection = db.CreateConnection();
        if (!await accounts.OwnsWorkspaceAsync(connection, workspaceId, AirAccountEndpoints.User(ctx))) return Results.NotFound();
        return Results.Ok(await connection.QueryAsync<AirProjectSummary>("SELECT id,workspace_id AS WorkspaceId,title,revision,updated_at AS UpdatedAt FROM air_projects WHERE workspace_id=@workspaceId AND archived_at IS NULL ORDER BY updated_at DESC LIMIT 200", new { workspaceId }));
    }
    private static async Task<IResult> Create(Guid workspaceId, AirProjectRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        var error = Validate(request); if (error is not null) return error;
        using var connection = db.CreateConnection(); var userId = AirAccountEndpoints.User(ctx);
        if (!await accounts.OwnsWorkspaceAsync(connection, workspaceId, userId)) return Results.NotFound();
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Renew Air access before saving a new project." }, statusCode: 403);
        if (!await AssetsBelongAsync(connection, workspaceId, request.Document)) return Results.BadRequest(new { error = "Logos must belong to this workspace." });
        var projectId = await connection.ExecuteScalarAsync<Guid>("INSERT INTO air_projects(id,workspace_id,title,document) SELECT @id,@workspaceId,@title,CAST(@document AS jsonb) WHERE (SELECT count(*) FROM air_projects WHERE workspace_id=@workspaceId AND archived_at IS NULL)<200 ON CONFLICT(id) DO NOTHING RETURNING id", new { id = request.Id is { } supplied && supplied != Guid.Empty ? supplied : Guid.NewGuid(), workspaceId, title = request.Title.Trim(), document = JsonSerializer.Serialize(request.Document, Json) });
        if (projectId == Guid.Empty && request.Id.HasValue) { var existing = await OwnedAsync(connection, request.Id.Value, userId); if (existing is not null) return Results.Conflict(new { error = "Project already exists. Review the saved revision.", current = View(existing) }); }
        return projectId == Guid.Empty ? Results.Conflict(new { error = "Archive old projects before creating more than 200." }) : Results.Ok(new { id = projectId, workspaceId, title = request.Title.Trim(), document = request.Document, revision = 1 });
    }
    private static async Task<IResult> Get(Guid projectId, HttpContext ctx, IDbConnectionFactory db)
    {
        using var connection = db.CreateConnection();
        var project = await OwnedAsync(connection, projectId, AirAccountEndpoints.User(ctx));
        return project is null ? Results.NotFound() : Results.Ok(View(project));
    }
    private static async Task<IResult> Update(Guid projectId, AirProjectRequest request, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        var error = Validate(request); if (error is not null) return error;
        if (request.Revision < 1) return Results.BadRequest(new { error = "Provide the saved revision before syncing." });
        using var connection = db.CreateConnection(); var userId = AirAccountEndpoints.User(ctx);
        var current = await OwnedAsync(connection, projectId, userId); if (current is null) return Results.NotFound();
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Air access expired. Your local draft is retained until access is renewed." }, statusCode: 403);
        if (!await AssetsBelongAsync(connection, current.WorkspaceId, request.Document)) return Results.BadRequest(new { error = "Logos must belong to this workspace." });
        var next = await connection.QuerySingleOrDefaultAsync<ProjectRow>("UPDATE air_projects SET title=@title,document=CAST(@document AS jsonb),revision=revision+1,updated_at=now() WHERE id=@projectId AND revision=@revision AND archived_at IS NULL RETURNING id,workspace_id AS WorkspaceId,title,revision,document::text AS Document,updated_at AS UpdatedAt", new { projectId, title = request.Title.Trim(), document = JsonSerializer.Serialize(request.Document, Json), revision = request.Revision });
        if (next is not null) return Results.Ok(View(next));
        current = await OwnedAsync(connection, projectId, userId);
        return Results.Conflict(new { error = "Project changed on another device. Your local draft has not been discarded.", current = current is null ? null : View(current) });
    }
    private static async Task<IResult> Archive(Guid projectId, int revision, HttpContext ctx, IDbConnectionFactory db, AirAccountService accounts)
    {
        using var connection = db.CreateConnection(); var userId = AirAccountEndpoints.User(ctx);
        if (await OwnedAsync(connection, projectId, userId) is null) return Results.NotFound();
        if (!await accounts.HasAccessAsync(connection, userId)) return Results.Json(new { error = "Active access is required to archive a project." }, statusCode: 403);
        var count = await connection.ExecuteAsync("UPDATE air_projects SET archived_at=now(),updated_at=now(),revision=revision+1 WHERE id=@projectId AND revision=@revision AND archived_at IS NULL", new { projectId, revision });
        return count == 1 ? Results.Ok(new { archived = true }) : Results.Conflict(new { error = "Reload the latest project revision before archiving." });
    }
    private static IResult? Validate(AirProjectRequest request)
    {
        try { AirProjectRules.Validate(request.Title, request.Document); if (request.Document.Screens.Any(screen => !BroadcastScreenEndpoints.IsKnownOverlay(screen.Overlay))) throw new ArgumentException("Choose an installed broadcast screen."); if (JsonSerializer.SerializeToUtf8Bytes(request.Document, Json).Length > 2 * 1024 * 1024) throw new ArgumentException("Project exceeds 2 MB; upload logos separately."); return null; }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
    }
    private static async Task<bool> AssetsBelongAsync(System.Data.IDbConnection connection, Guid workspaceId, AirProjectDocument document)
    {
        var ids = document.Teams.Select(team => team.LogoAssetId).Concat((document.Sponsors ?? []).Select(sponsor => sponsor.LogoAssetId)).Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();
        return ids.Length == 0 || await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM air_assets WHERE workspace_id=@workspaceId AND id=ANY(@ids)", new { workspaceId, ids }) == ids.Length;
    }
    private static async Task<ProjectRow?> OwnedAsync(System.Data.IDbConnection connection, Guid projectId, Guid userId) => await connection.QuerySingleOrDefaultAsync<ProjectRow>("SELECT p.id,p.workspace_id AS WorkspaceId,p.title,p.revision,p.document::text AS Document,p.updated_at AS UpdatedAt FROM air_projects p JOIN air_workspaces w ON w.id=p.workspace_id WHERE p.id=@projectId AND w.owner_id=@userId AND p.archived_at IS NULL", new { projectId, userId });
    private static object View(ProjectRow row) => new { row.Id, row.WorkspaceId, row.Title, row.Revision, row.UpdatedAt, document = JsonSerializer.Deserialize<AirProjectDocument>(row.Document, Json) };
    private sealed record ProjectRow(Guid Id, Guid WorkspaceId, string Title, int Revision, string Document, DateTime UpdatedAt);
}
