namespace Esportra.Core.Match;

public sealed class DefaultVetoSequenceResolver : IVetoSequenceResolver
{
    public IReadOnlyList<VetoStep> Resolve(MatchMapVeto veto, int poolSize)
        => VetoSequences.GetSequence(veto.BestOf, veto.Game ?? "valorant", poolSize);

    public VetoStep? GetStep(MatchMapVeto veto, int actionNumber, int poolSize)
        => VetoSequences.GetStep(veto.BestOf, actionNumber, veto.Game ?? "valorant", poolSize);
}
