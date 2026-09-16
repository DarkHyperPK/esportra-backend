namespace Esportra.Core.Match;

public static class VetoSequenceResolverFactory
{
    public static IVetoSequenceResolver Create(VetoSettings? settings)
    {
        if (settings?.Mode == VetoMode.Custom && settings.Sequence is { Length: > 0 })
            return new CustomVetoSequenceResolver(settings.Sequence);
        return new DefaultVetoSequenceResolver();
    }
}
