using Esar.Application.Abstractions;
using Esar.Application.Incidents;
using Esar.Application.Matching;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Lifecycle;

public interface ILifecycleService
{
    /// <summary>Marks assets not seen recently as Offline/Inactive and raises incidents where required.</summary>
    Task<int> ProcessStaleAssetsAsync(CancellationToken ct = default);
}

public class LifecycleService : ILifecycleService
{
    private readonly IUnitOfWork _uow;
    private readonly IIncidentService _incidents;
    private readonly ILogger<LifecycleService> _logger;

    public LifecycleService(IUnitOfWork uow, IIncidentService incidents, ILogger<LifecycleService> logger)
    {
        _uow = uow;
        _incidents = incidents;
        _logger = logger;
    }

    public async Task<int> ProcessStaleAssetsAsync(CancellationToken ct = default)
    {
        var staleDays = await GetIntSettingAsync(SettingKeys.StaleAssetDays, 7, ct);
        var decommissionDays = await GetIntSettingAsync(SettingKeys.DecommissionAfterDays, 90, ct);
        var staleCutoff = DateTime.UtcNow.AddDays(-staleDays);
        var decommissionCutoff = DateTime.UtcNow.AddDays(-decommissionDays);

        var stale = await _uow.Assets.ListAsync(a => !a.IsDeleted &&
            a.Status == AssetStatus.Active && a.LastSeen < staleCutoff, ct);

        var processed = 0;
        foreach (var asset in stale)
        {
            if (asset.LastSeen < decommissionCutoff)
            {
                asset.Status = AssetStatus.Decommissioned;
                asset.LifecycleStatus = LifecycleStatus.Retired;
            }
            else
            {
                asset.Status = AssetStatus.Offline;
                if (asset.Criticality >= CriticalityLevel.High)
                {
                    await _incidents.RaiseAsync(IncidentType.AssetOffline,
                        asset.Criticality == CriticalityLevel.Critical ? IncidentSeverity.High : IncidentSeverity.Medium,
                        $"Asset offline: {asset.Hostname}",
                        $"Asset {asset.Hostname} ({asset.AssetType}) has not been seen by any source since {asset.LastSeen:yyyy-MM-dd HH:mm} UTC.",
                        asset.Id, null, ct);
                }
            }
            asset.UpdatedAt = DateTime.UtcNow;
            _uow.Assets.Update(asset);
            processed++;
        }

        if (processed > 0)
        {
            await _uow.SaveChangesAsync(ct);
            _logger.LogInformation("Lifecycle pass processed {Count} stale assets", processed);
        }
        return processed;
    }

    private async Task<int> GetIntSettingAsync(string key, int fallback, CancellationToken ct)
    {
        var setting = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting is not null && int.TryParse(setting.Value, out var v) ? v : fallback;
    }
}
