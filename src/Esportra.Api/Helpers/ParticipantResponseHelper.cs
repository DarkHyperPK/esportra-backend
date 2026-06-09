using Esportra.Core.Tournaments;

namespace Esportra.Api.Helpers;

public static class ParticipantResponseHelper
{
    public static object EnrichParticipant(
        dynamic first,
        IEnumerable<object>? members = null,
        string? teamMembersCsv = null)
    {
        var isMock = first.is_mock as bool?;
        var teamKindRaw = first.team_kind as string;
        var isSolo = first.is_solo as bool?;
        var tag = first.team_tag as string;

        var teamKind = ParticipantEntryMetadata.ResolveTeamKind(isMock, teamKindRaw, isSolo, tag);
        var participantType = first.participant_type as string;
        var entryKind = ParticipantEntryMetadata.ResolveEntryKind(isMock, teamKind, participantType);

        var teamName = first.team_name as string;
        var soloUsername = first.solo_username as string;
        var soloFullName = first.solo_full_name as string;
        var teamLogoUrl = first.team_logo_url as string;
        var soloAvatarUrl = first.solo_avatar_url as string;

        return new
        {
            id = (Guid)first.id,
            tournament_id = (Guid)first.tournament_id,
            user_id = first.user_id as Guid?,
            team_id = first.team_id as Guid?,
            participant_type = participantType,
            status = (string)first.status,
            created_at = first.created_at,
            checked_in_at = first.checked_in_at as DateTime?,
            is_mock = isMock,
            is_solo = teamKind == "solo" || string.Equals(participantType, "solo", StringComparison.OrdinalIgnoreCase),
            team_kind = teamKind,
            entry_kind = entryKind,
            team_name = teamName,
            team_logo_url = teamLogoUrl,
            display_name = ParticipantEntryMetadata.ResolveDisplayName(entryKind, teamName, soloUsername, soloFullName),
            display_logo_url = ParticipantEntryMetadata.ResolveDisplayLogoUrl(entryKind, teamLogoUrl, soloAvatarUrl),
            team_members = teamMembersCsv ?? string.Empty,
            members = members ?? Array.Empty<object>(),
            solo_username = soloUsername,
            solo_full_name = soloFullName,
            solo_riot_tag = first.solo_riot_tag as string,
            solo_avatar_url = soloAvatarUrl,
        };
    }
}
