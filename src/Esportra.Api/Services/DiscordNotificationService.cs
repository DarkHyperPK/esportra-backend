using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;

namespace Esportra.Api.Services;

/// <summary>
/// Sends Discord DMs to users who have linked their Discord account and enabled DM notifications.
/// Uses Discord REST API directly — no extra NuGet packages needed.
/// Only sends for specific notification types the user opted into.
/// </summary>
public sealed class DiscordNotificationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IDbConnectionFactory _db;
    private readonly ILogger<DiscordNotificationService> _logger;
    private readonly string? _botToken;
    private readonly string? _guildId;

    private static readonly HashSet<string> DmEligibleTypes = DiscordNotificationTypes.DmEligibleTypes;

    public DiscordNotificationService(
        IHttpClientFactory httpFactory,
        IDbConnectionFactory db,
        IConfiguration config,
        ILogger<DiscordNotificationService> logger)
    {
        _httpFactory = httpFactory;
        _db = db;
        _logger = logger;
        _botToken = config["Discord:BotToken"];
        _guildId = config["Discord:GuildId"];
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_guildId);

    /// <summary>
    /// Attempts to send a Discord DM for a notification. Fire-and-forget safe — never throws.
    /// </summary>
    public async Task TrySendDmAsync(Guid userId, string notificationType, string title, string message)
    {
        if (!IsConfigured) return;
        if (!DmEligibleTypes.Contains(notificationType)) return;

        try
        {
            using var conn = _db.CreateConnection();

            // Check if user has Discord DMs enabled
            var prefs = await conn.QuerySingleOrDefaultAsync<(bool enabled, string? discordId)>(
                """
                SELECT
                    COALESCE((p.settings->>'discord_dm_enabled')::boolean, TRUE) AS enabled,
                    ai.provider_id AS discord_id
                FROM profiles p
                LEFT JOIN auth.identities ai
                    ON ai.user_id = p.id AND ai.provider = 'discord'
                WHERE p.id = @userId
                """,
                new { userId });

            if (!prefs.enabled || string.IsNullOrEmpty(prefs.discordId))
            {
                _logger.LogInformation("Discord DM skipped for user {UserId} — no linked Discord account or DMs disabled", userId);
                return;
            }

            await SendDiscordDmAsync(prefs.discordId, title, message, notificationType);
        }
        catch (Exception ex)
        {
            // Never let Discord failures affect the main notification flow
            _logger.LogWarning(ex, "Failed to send Discord DM for user {UserId}, type {Type}", userId, notificationType);
        }
    }

    /// <summary>
    /// Batch version — sends DMs to multiple users for the same notification.
    /// </summary>
    public async Task TrySendBatchDmAsync(IEnumerable<Guid> userIds, string notificationType, string title, string message)
    {
        if (!IsConfigured) return;
        if (!DmEligibleTypes.Contains(notificationType)) return;

        foreach (var userId in userIds)
        {
            await TrySendDmAsync(userId, notificationType, title, message);
        }
    }

    /// <summary>
    /// Adds a user to the Esportra Discord server using their OAuth access token.
    /// Requires the guilds.join scope on the user's OAuth token.
    /// </summary>
    public async Task<DiscordJoinOutcome> TryAutoJoinGuildAsync(string discordUserId, string userAccessToken)
    {
        if (!IsConfigured || string.IsNullOrEmpty(_guildId))
            return DiscordJoinOutcome.Failed("not_configured");

        try
        {
            var http = _httpFactory.CreateClient("Discord");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _botToken);

            var payload = JsonSerializer.Serialize(new { access_token = userAccessToken });
            var req = new HttpRequestMessage(HttpMethod.Put,
                $"https://discord.com/api/v10/guilds/{_guildId}/members/{discordUserId}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var resp = await http.SendAsync(req);

            // 201 = added, 204 = already a member — both are success
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord user {DiscordId} auto-joined guild", discordUserId);
                return DiscordJoinOutcome.Succeeded;
            }

            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to auto-join Discord user {DiscordId}: {Status} {Body}",
                discordUserId, resp.StatusCode, body);

            return resp.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => DiscordJoinOutcome.Failed("token_expired"),
                System.Net.HttpStatusCode.Forbidden => DiscordJoinOutcome.Failed("missing_scope"),
                _ => DiscordJoinOutcome.Failed("discord_api_error"),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-join Discord user {DiscordId} to guild", discordUserId);
            return DiscordJoinOutcome.Failed("discord_api_error");
        }
    }

    private async Task SendDiscordDmAsync(string discordUserId, string title, string message, string notificationType)
    {
        var http = _httpFactory.CreateClient("Discord");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _botToken);

        // Step 1: Create/open a DM channel with the user
        var dmChannelPayload = JsonSerializer.Serialize(new { recipient_id = discordUserId });
        var dmChannelReq = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/v10/users/@me/channels")
        {
            Content = new StringContent(dmChannelPayload, Encoding.UTF8, "application/json")
        };
        var dmChannelResp = await http.SendAsync(dmChannelReq);

        if (!dmChannelResp.IsSuccessStatusCode)
        {
            var body = await dmChannelResp.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to open DM channel for Discord user {DiscordId}: {Status} {Body}",
                discordUserId, dmChannelResp.StatusCode, body);
            return;
        }

        var channelJson = await dmChannelResp.Content.ReadAsStringAsync();
        var channelData = JsonSerializer.Deserialize<DiscordChannel>(channelJson);
        if (channelData?.Id is null) return;

        // Step 2: Send the message as an embed
        var embed = new DiscordEmbed
        {
            Title = title,
            Description = message,
            Color = GetColorForType(notificationType),
            Footer = new DiscordEmbedFooter { Text = "Esportra • esportra.com" },
            Timestamp = DateTime.UtcNow.ToString("o")
        };

        var msgPayload = JsonSerializer.Serialize(new { embeds = new[] { embed } });
        var msgReq = new HttpRequestMessage(HttpMethod.Post, $"https://discord.com/api/v10/channels/{channelData.Id}/messages")
        {
            Content = new StringContent(msgPayload, Encoding.UTF8, "application/json")
        };
        var msgResp = await http.SendAsync(msgReq);

        var msgBody = await msgResp.Content.ReadAsStringAsync();
        if (msgResp.IsSuccessStatusCode)
        {
            _logger.LogInformation("✅ Discord DM sent to {DiscordId} (channel {Channel}), type={Type}",
                discordUserId, channelData.Id, notificationType);
        }
        else
        {
            _logger.LogWarning("Failed to send DM to Discord user {DiscordId}: {Status} {Body}",
                discordUserId, msgResp.StatusCode, msgBody);
        }
    }

    private static int GetColorForType(string type) => type switch
    {
        "match_ready" => 0x22C55E, // green
        "check_in_reminder" => 0xF59E0B, // amber
        "result_reported" => 0x3B82F6, // blue
        "result_disputed" => 0xEF4444, // red
        "dispute_resolved" => 0x8B5CF6, // purple
        "tournament_registered" => 0x22C55E, // green
        "tournament_announcement" => 0x3B82F6, // blue
        "result_accepted" => 0x22C55E, // green
        "match_walkover" => 0xF59E0B, // amber
        _ => 0xF43F5E, // rose (brand)
    };

    // Minimal Discord API DTOs
    private sealed class DiscordChannel
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class DiscordEmbed
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("color")]
        public int Color { get; set; }

        [JsonPropertyName("footer")]
        public DiscordEmbedFooter? Footer { get; set; }

        [JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }
    }

    private sealed class DiscordEmbedFooter
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}

public sealed record DiscordJoinOutcome(bool Success, string? Reason = null)
{
    public static readonly DiscordJoinOutcome Succeeded = new(true);
    public static DiscordJoinOutcome Failed(string reason) => new(false, reason);
}
