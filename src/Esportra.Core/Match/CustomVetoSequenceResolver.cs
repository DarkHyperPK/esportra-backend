namespace Esportra.Core.Match;

public sealed class CustomVetoSequenceResolver : IVetoSequenceResolver
{
    private readonly IReadOnlyList<VetoStep> _sequence;

    public CustomVetoSequenceResolver(VetoStep[] sequence)
    {
        if (sequence is null || sequence.Length == 0)
            throw new ArgumentException("Custom sequence must be non-empty.", nameof(sequence));
        _sequence = sequence;
    }

    public IReadOnlyList<VetoStep> Resolve(MatchMapVeto veto, int poolSize) => _sequence;

    public VetoStep? GetStep(MatchMapVeto veto, int actionNumber, int poolSize)
        => _sequence.FirstOrDefault(s => s.ActionNumber == actionNumber);
}
