namespace Esportra.Core.Br;

public static class BrSeedingService
{
    public static Guid[] ShuffleTeams(Guid[] teams)
    {
        var shuffled = teams.ToArray();
        Random.Shared.Shuffle(shuffled);
        return shuffled;
    }

    public static List<(Guid teamId, Guid groupId, int seedOrder)> BuildRoundRobinAssignments(
        Guid[] teams,
        Guid[] groupIds)
    {
        var assignments = new List<(Guid teamId, Guid groupId, int seedOrder)>();
        var seedCounters = new int[groupIds.Length];

        for (var i = 0; i < teams.Length; i++)
        {
            var groupIndex = i % groupIds.Length;
            seedCounters[groupIndex]++;
            assignments.Add((teams[i], groupIds[groupIndex], seedCounters[groupIndex]));
        }

        return assignments;
    }

    public static List<(Guid teamId, Guid groupId, int seedOrder)> BuildSnakeAssignments(
        Guid[] teams,
        Guid[] groupIds)
    {
        var assignments = new List<(Guid teamId, Guid groupId, int seedOrder)>();
        var seedCounters = new int[groupIds.Length];
        var groupCount = groupIds.Length;

        for (var i = 0; i < teams.Length; i++)
        {
            var pass = i / groupCount;
            var posInPass = i % groupCount;
            var groupIndex = pass % 2 == 0 ? posInPass : groupCount - 1 - posInPass;

            seedCounters[groupIndex]++;
            assignments.Add((teams[i], groupIds[groupIndex], seedCounters[groupIndex]));
        }

        return assignments;
    }

    public static int SnakeGroupIndex(int pickIndex, int groupCount)
    {
        if (groupCount <= 1) return 0;
        var round = pickIndex / groupCount;
        var pos = pickIndex % groupCount;
        return round % 2 == 0 ? pos : groupCount - 1 - pos;
    }
}
