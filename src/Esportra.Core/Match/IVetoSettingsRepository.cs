namespace Esportra.Core.Match;

public interface IVetoSettingsRepository
{
    Task<VetoSettings?> GetAsync(Guid matchId, CancellationToken ct = default);
    Task SaveAsync(VetoSettings settings, Guid updatedBy, CancellationToken ct = default);
    Task ClearAsync(Guid matchId, CancellationToken ct = default);
}
