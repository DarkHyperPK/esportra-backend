namespace Esportra.Contracts.Requests;

// ── Phase 15: Dashboard Customization ─────────────────────────────────────────

public sealed record DashboardWidgetConfig(
    string WidgetId,
    int    Position,
    bool   Visible,
    int?   RefreshInterval);

public sealed record SaveDashboardPreferencesRequest(DashboardWidgetConfig[] Layout);
