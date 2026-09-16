namespace Esportra.Core.Match;

public interface IVetoSequenceResolver
{
    IReadOnlyList<VetoStep> Resolve(MatchMapVeto veto, int poolSize);
    VetoStep? GetStep(MatchMapVeto veto, int actionNumber, int poolSize);
}
