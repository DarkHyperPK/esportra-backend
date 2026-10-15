using Esportra.Contracts.Air;

namespace Esportra.Core.Air;

public static class AirProjectRules
{
    private static readonly HashSet<string> Formats = ["single_elimination", "double_elimination", "round_robin", "swiss", "groups", "manual"];
    private static readonly HashSet<string> ScreenTypes = ["pre_show", "match", "break", "post_show", "sponsor_slot", "custom"];
    public static void Validate(string title, AirProjectDocument document)
    {
        Text(title, 120, "Project title");
        if (document is not { SchemaVersion: 1, Teams: not null, Matches: not null, Screens: not null, Bracket.Stages: not null }) throw new ArgumentException("Use a version 1 Air project document.");
        if (document.Teams.Length > 128 || document.Matches.Length > 512 || document.Screens.Length > 200 || document.Bracket.Stages.Length > 32) throw new ArgumentException("Project exceeds supported team, match, screen or stage limits.");
        if (document.Teams.Any(team => team is null) || document.Matches.Any(match => match is null) || document.Screens.Any(screen => screen is null) || document.Bracket.Stages.Any(stage => stage is null)) throw new ArgumentException("Project entries cannot be null.");
        Unique(document.Teams.Select(team => team.Id), "teams");
        Unique(document.Matches.Select(match => match.Id), "matches");
        Unique(document.Screens.Select(screen => screen.Id), "screens");
        Unique(document.Bracket.Stages.Select(stage => stage.Id), "stages");
        var teams = document.Teams.Select(team => team.Id).ToHashSet();
        var matches = document.Matches.Select(match => match.Id).ToHashSet();
        foreach (var team in document.Teams)
        {
            Text(team.Name, 80, "Team name"); Text(team.Tag, 12, "Team tag");
            if (team.Players is null || team.Players.Length > 15) throw new ArgumentException("A team supports up to 15 players.");
            if (team.Players.Any(player => player is null)) throw new ArgumentException("Player entries cannot be null.");
            Unique(team.Players.Select(player => player.Id), "players");
            foreach (var player in team.Players)
            {
                Text(player.OriginalName, 80, "Original player name"); Text(player.InGameName, 100, "In-game identity");
                if (player.HudNickname is { Length: > 0 }) Text(player.HudNickname, 80, "HUD nickname");
                if (player.GameId is { Length: > 0 } && !Guid.TryParse(player.GameId, out _)) throw new ArgumentException("Game identity must be a received game player ID.");
                if (player.Role is not ("starter" or "substitute")) throw new ArgumentException("Choose starter or substitute.");
            }
            if (team.Players.GroupBy(player => player.InGameName.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) throw new ArgumentException("Team players need distinct in-game identities.");
        }
        foreach (var match in document.Matches)
        {
            Text(match.Title, 120, "Match title");
            if (match.TeamIds is not { Length: 2 } || match.TeamIds[0] == match.TeamIds[1] || match.TeamIds.Any(id => !teams.Contains(id))) throw new ArgumentException("Choose two different project teams.");
            if (match.BestOf is not (1 or 3 or 5 or 7 or 9) || match.MapPool is null || match.MapPool.Length > 15) throw new ArgumentException("Choose a supported best-of and map pool.");
            foreach (var map in match.MapPool) Text(map, 80, "Map name");
            if (match.Status is not ("scheduled" or "in_progress" or "completed" or "cancelled")) throw new ArgumentException("Choose a supported match status.");
            if (match.Scores is not null && (match.Scores.Length != 2 || match.Scores.Any(score => score < 0 || score > match.BestOf / 2 + 1))) throw new ArgumentException("Enter valid series scores.");
        }
        foreach (var screen in document.Screens)
        {
            Text(screen.Title, 80, "Screen name");
            if (!ScreenTypes.Contains(screen.Type) || !System.Text.RegularExpressions.Regex.IsMatch(screen.Overlay ?? "", "^[a-z0-9-]{1,64}$") || screen.MatchId.HasValue && !matches.Contains(screen.MatchId.Value)) throw new ArgumentException("Use a supported screen and project match.");
        }
        if (!Formats.Contains(document.Bracket.Format)) throw new ArgumentException("Choose a supported bracket format.");
        foreach (var stage in document.Bracket.Stages)
        {
            Text(stage.Label, 100, "Stage label");
            if (!Formats.Contains(stage.Format) || stage.Matches is null || stage.Matches.Length > 512) throw new ArgumentException("Invalid bracket stage.");
            if (stage.Matches.Any(node => node is null)) throw new ArgumentException("Bracket nodes cannot be null.");
            Unique(stage.Matches.Select(node => node.Id), "bracket nodes");
            Unique(stage.Matches.Select(node => node.MatchId), "stage matches");
            foreach (var node in stage.Matches)
                if (!matches.Contains(node.MatchId) || node.Round is < 0 or > 32 || node.Side is not ("upper" or "lower" or "final" or "group") || node.NextMatchId.HasValue && (!matches.Contains(node.NextMatchId.Value) || node.NextMatchId == node.MatchId)) throw new ArgumentException("Bracket connections must reference project matches.");
            var links = stage.Matches.Where(node => node.NextMatchId.HasValue).GroupBy(node => node.MatchId).ToDictionary(group => group.Key, group => group.First().NextMatchId!.Value);
            foreach (var start in links.Keys) { var visited = new HashSet<Guid>(); var cursor = start; while (links.TryGetValue(cursor, out var next)) { if (!visited.Add(cursor)) throw new ArgumentException("Bracket connections cannot contain a cycle."); cursor = next; } }
        }
        var sponsors = document.Sponsors ?? [];
        if (sponsors.Length > 100 || sponsors.Any(sponsor => sponsor is null)) throw new ArgumentException("Use up to 100 sponsors.");
        Unique(sponsors.Select(sponsor => sponsor.Id), "sponsors");
        foreach (var sponsor in sponsors) Text(sponsor.Name, 100, "Sponsor name");
        foreach (var people in new[] { document.Talent ?? [], document.Crew ?? [] })
        {
            if (people.Length > 100 || people.Any(person => person is null)) throw new ArgumentException("Use up to 100 people per team.");
            Unique(people.Select(person => person.Id), "people");
            foreach (var person in people) { Text(person.Name, 100, "Name"); Text(person.Role, 80, "Responsibility"); }
        }
    }
    public static void Text(string? value, int maximum, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(character => char.IsControl(character) || character is '<' or '>')) throw new ArgumentException($"{label} must be plain text, 1–{maximum} characters.");
    }
    private static void Unique(IEnumerable<Guid> ids, string label) { var values = ids.ToArray(); if (values.Any(id => id == Guid.Empty) || values.Distinct().Count() != values.Length) throw new ArgumentException($"Use unique IDs for {label}."); }
}
