using Esportra.Api.Services.ScrimInsights;

namespace Esportra.Api.Tests.ScrimInsights;

/// <summary>
/// A tiny hand-checkable match: Blue (the viewer's team) a1,a2 against Red b1,b2, three rounds, Blue wins 2-1.
/// R1 (pistol, Blue attacks): a1 opens on b1, b2 kills a2, a1 kills b2 within 2 s (a2 is traded), a1 plants A.
/// R2 (Blue attacks, loses): b1 opens on a1, b2 kills a2.
/// R3 (Blue defends, wins): a2 opens on b2, a1 kills b1.
/// </summary>
internal static class InsightsFixture
{
    public static InPlayer Player(string puuid, string side, int score, int kills, int deaths, int damage, int head = 0, int body = 0, int leg = 0, int plants = 0) =>
        new(puuid, side, puuid.ToUpperInvariant(), "EUW", "add6443a-41bd-e414-f6ad-e58d267f4e95", null, score, 3, kills, deaths, 0, damage, head, body, leg,
            0, 0, 0, 0, 0, 0, plants, 0, null, null, null, null);

    public static InRound Round(int n, string winner, string role, string? planter = null) =>
        new(n, winner, role, "Elimination", planter, null, planter is null ? null : 30_000, planter is null ? null : "A", null, null, null, null, null);

    public static InKill Kill(int round, int ordinal, string killer, string victim, int ms, params string[] assists) =>
        new(round, ordinal, killer, victim, assists, ms, 100, 200, "Vandal", "Weapon");

    public static InEcon Econ(int round, string puuid, int loadout, string weapon = "Vandal", string armor = "Heavy") =>
        new(round, puuid, loadout, weapon, armor, 0, 0);

    public static InMatch Sample()
    {
        var players = new[]
        {
            Player("a1", "Blue", 600, 2, 1, 300, head: 2, body: 6, leg: 2, plants: 1) with { Grenade = 3, Ability1 = 6, Ultimate = 1 },
            Player("a2", "Blue", 300, 1, 2, 100),
            Player("b1", "Red", 500, 1, 1, 200),
            Player("b2", "Red", 200, 1, 1, 100),
        };
        InRound[] rounds = [Round(1, "Blue", "Attacker", planter: "a1"), Round(2, "Red", "Defender"), Round(3, "Blue", "Defender")];
        InKill[] kills =
        [
            Kill(1, 1, "a1", "b1", 5000), Kill(1, 2, "b2", "a2", 7000), Kill(1, 3, "a1", "b2", 9000),
            Kill(2, 1, "b1", "a1", 4000), Kill(2, 2, "b2", "a2", 20000),
            Kill(3, 1, "a2", "b2", 3000), Kill(3, 2, "a1", "b1", 8000),
        ];
        InEcon[] econ =
        [
            Econ(1, "a1", 800, "Classic", "None"), Econ(1, "a2", 800, "Classic", "None"), Econ(1, "b1", 800, "Classic", "None"), Econ(1, "b2", 800, "Classic", "None"),
            Econ(2, "a1", 1000, "Sheriff", "Light"), Econ(2, "a2", 1000, "Sheriff", "Light"), Econ(2, "b1", 4500), Econ(2, "b2", 4500),
            Econ(3, "a1", 4000), Econ(3, "a2", 4000), Econ(3, "b1", 2500), Econ(3, "b2", 2500),
        ];
        InDamage[] damage = [new(1, "b1", "a1", 80, 0, 1, 0), new(2, "b2", "a1", 20, 0, 1, 0)];
        return new InMatch("Blue", players, rounds, kills, econ, damage);
    }
}
