namespace Esportra.Core.Licensing;

/// <summary>Existing PKR launch rates. A null amount requires an individual quote, not a free pass.</summary>
public static class EventPassPricing
{
    public static int? EstimatePkr(int teams) => teams switch
    {
        > 32 and <= 64 => 12000,
        > 64 and <= 128 => 25000,
        > 128 and <= 256 => 45000,
        > 256 and <= 512 => 80000,
        _ => null,
    };
}
