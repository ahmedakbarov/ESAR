using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Merging;

public interface IMergeEngine
{
    /// <summary>Applies a discovered payload onto an existing golden record, honoring source priorities
    /// and recording field-level history. Returns the list of changed field names.</summary>
    Task<IReadOnlyList<string>> ApplyAsync(Asset asset, DiscoveredAsset incoming, CancellationToken ct = default);
    /// <summary>Merges a duplicate golden record into a surviving one (manual/auto dedup).</summary>
    Task MergeAssetsAsync(Asset survivor, Asset duplicate, string mergedBy, CancellationToken ct = default);
}

public class MergeEngine : IMergeEngine
{
    private readonly IUnitOfWork _uow;
    private readonly ISourcePriorityEngine _priority;
    private readonly ILogger<MergeEngine> _logger;

    public MergeEngine(IUnitOfWork uow, ISourcePriorityEngine priority, ILogger<MergeEngine> logger)
    {
        _uow = uow;
        _priority = priority;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(Asset asset, DiscoveredAsset incoming, CancellationToken ct = default)
    {
        var changed = new List<string>();
        var owners = ParseOwners(asset.AttributeSourcesJson);

        async Task Set(string field, string? current, string? value, Action apply)
        {
            if (string.IsNullOrWhiteSpace(value) || value == current) return;
            owners.TryGetValue(field, out var ownerName);
            ConnectorType? owner = ownerName != null && Enum.TryParse<ConnectorType>(ownerName, out var o) ? o : null;
            // Empty values are filled by anyone; conflicting values only by a higher-priority source.
            if (!string.IsNullOrWhiteSpace(current) && !await _priority.WinsAsync(incoming.Source, owner, field, ct))
                return;

            apply();
            owners[field] = incoming.Source.ToString();
            changed.Add(field);
            await _uow.AssetHistories.AddAsync(new AssetHistory
            {
                AssetId = asset.Id,
                FieldName = field,
                OldValue = current,
                NewValue = value,
                Source = incoming.Source,
                ChangedBy = $"connector:{incoming.Source}"
            }, ct);
        }

        await Set(nameof(asset.Hostname), asset.Hostname, incoming.Hostname, () => asset.Hostname = incoming.Hostname!);
        await Set(nameof(asset.Fqdn), asset.Fqdn, incoming.Fqdn, () => asset.Fqdn = incoming.Fqdn);
        await Set(nameof(asset.Domain), asset.Domain, incoming.Domain, () => asset.Domain = incoming.Domain);
        await Set(nameof(asset.OperatingSystem), asset.OperatingSystem, incoming.OperatingSystem,
            () => asset.OperatingSystem = incoming.OperatingSystem);
        await Set(nameof(asset.OsVersion), asset.OsVersion, incoming.OsVersion, () => asset.OsVersion = incoming.OsVersion);
        await Set(nameof(asset.SerialNumber), asset.SerialNumber, incoming.SerialNumber,
            () => asset.SerialNumber = incoming.SerialNumber);
        await Set(nameof(asset.BiosUuid), asset.BiosUuid, incoming.BiosUuid, () => asset.BiosUuid = incoming.BiosUuid);
        await Set(nameof(asset.Manufacturer), asset.Manufacturer, incoming.Manufacturer,
            () => asset.Manufacturer = incoming.Manufacturer);
        await Set(nameof(asset.Model), asset.Model, incoming.Model, () => asset.Model = incoming.Model);
        await Set(nameof(asset.CloudProvider), asset.CloudProvider, incoming.CloudProvider,
            () => asset.CloudProvider = incoming.CloudProvider);
        await Set(nameof(asset.CloudResourceId), asset.CloudResourceId, incoming.CloudResourceId,
            () => asset.CloudResourceId = incoming.CloudResourceId);
        await Set(nameof(asset.CloudRegion), asset.CloudRegion, incoming.CloudRegion,
            () => asset.CloudRegion = incoming.CloudRegion);
        await Set(nameof(asset.CloudSubscriptionId), asset.CloudSubscriptionId, incoming.CloudSubscriptionId,
            () => asset.CloudSubscriptionId = incoming.CloudSubscriptionId);
        await Set(nameof(asset.CloudAccountId), asset.CloudAccountId, incoming.CloudAccountId,
            () => asset.CloudAccountId = incoming.CloudAccountId);
        await Set(nameof(asset.OwnerName), asset.OwnerName, incoming.OwnerName, () => asset.OwnerName = incoming.OwnerName);
        await Set(nameof(asset.OwnerEmail), asset.OwnerEmail, incoming.OwnerEmail,
            () => asset.OwnerEmail = incoming.OwnerEmail);
        await Set(nameof(asset.Department), asset.Department, incoming.Department,
            () => asset.Department = incoming.Department);
        await Set(nameof(asset.BusinessUnit), asset.BusinessUnit, incoming.BusinessUnit,
            () => asset.BusinessUnit = incoming.BusinessUnit);
        await Set(nameof(asset.Location), asset.Location, incoming.Location, () => asset.Location = incoming.Location);
        await Set(nameof(asset.Classification), asset.Classification, incoming.Classification,
            () => asset.Classification = incoming.Classification);

        if (incoming.AssetType is { } at && at != AssetType.Unknown && asset.AssetType == AssetType.Unknown)
        {
            asset.AssetType = at;
            changed.Add(nameof(asset.AssetType));
        }
        if (incoming.Environment is { } env && env != EnvironmentType.Unknown && asset.Environment == EnvironmentType.Unknown)
        {
            asset.Environment = env;
            changed.Add(nameof(asset.Environment));
        }
        if (incoming.Criticality is { } crit && crit != CriticalityLevel.Unknown && asset.Criticality == CriticalityLevel.Unknown)
        {
            asset.Criticality = crit;
            changed.Add(nameof(asset.Criticality));
        }

        MergeInterfaces(asset, incoming);
        MergeTags(asset, incoming);
        MergeSoftware(asset, incoming);

        asset.LastSeen = incoming.SeenAt > asset.LastSeen ? incoming.SeenAt : asset.LastSeen;
        if (asset.Status is AssetStatus.Offline or AssetStatus.Inactive) asset.Status = AssetStatus.Active;
        asset.AttributeSourcesJson = JsonSerializer.Serialize(owners);
        asset.UpdatedAt = DateTime.UtcNow;
        return changed;
    }

    public async Task MergeAssetsAsync(Asset survivor, Asset duplicate, string mergedBy, CancellationToken ct = default)
    {
        foreach (var source in duplicate.Sources.ToList())
        {
            source.AssetId = survivor.Id;
            if (!survivor.Sources.Any(s => s.ConnectorType == source.ConnectorType && s.ExternalId == source.ExternalId))
                survivor.Sources.Add(source);
        }
        foreach (var ip in duplicate.IpAddresses.ToList())
        {
            if (!survivor.IpAddresses.Any(i => i.IpAddress == ip.IpAddress && i.MacAddress == ip.MacAddress))
            {
                ip.AssetId = survivor.Id;
                survivor.IpAddresses.Add(ip);
            }
        }
        foreach (var tag in duplicate.Tags.ToList())
        {
            if (!survivor.Tags.Any(t => t.Key == tag.Key))
            {
                tag.AssetId = survivor.Id;
                survivor.Tags.Add(tag);
            }
        }

        survivor.FirstSeen = duplicate.FirstSeen < survivor.FirstSeen ? duplicate.FirstSeen : survivor.FirstSeen;
        survivor.LastSeen = duplicate.LastSeen > survivor.LastSeen ? duplicate.LastSeen : survivor.LastSeen;

        duplicate.IsDeleted = true;
        duplicate.MergedIntoAssetId = survivor.Id;
        duplicate.Status = AssetStatus.Decommissioned;
        duplicate.UpdatedAt = DateTime.UtcNow;

        await _uow.AssetHistories.AddAsync(new AssetHistory
        {
            AssetId = survivor.Id,
            FieldName = "MergedFrom",
            OldValue = null,
            NewValue = duplicate.Id.ToString(),
            ChangedBy = mergedBy
        }, ct);

        _logger.LogInformation("Asset {Duplicate} merged into {Survivor} by {User}",
            duplicate.Id, survivor.Id, mergedBy);
    }

    private static void MergeInterfaces(Asset asset, DiscoveredAsset incoming)
    {
        foreach (var iface in incoming.Interfaces)
        {
            if (iface.IpAddress is null && iface.MacAddress is null) continue;
            var existing = asset.IpAddresses.FirstOrDefault(i =>
                (iface.IpAddress != null && i.IpAddress == iface.IpAddress) ||
                (iface.IpAddress == null && iface.MacAddress != null && i.MacAddress == iface.MacAddress));
            if (existing is null)
            {
                asset.IpAddresses.Add(new AssetIp
                {
                    AssetId = asset.Id,
                    IpAddress = iface.IpAddress ?? string.Empty,
                    MacAddress = iface.MacAddress,
                    IsPrimary = iface.IsPrimary,
                    Source = incoming.Source,
                    LastSeen = incoming.SeenAt
                });
            }
            else
            {
                existing.MacAddress ??= iface.MacAddress;
                existing.LastSeen = incoming.SeenAt;
            }
        }
    }

    private static void MergeTags(Asset asset, DiscoveredAsset incoming)
    {
        foreach (var (key, value) in incoming.Tags)
        {
            var existing = asset.Tags.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                asset.Tags.Add(new AssetTag { AssetId = asset.Id, Key = key, Value = value, Source = incoming.Source });
            else if (existing.Source == incoming.Source)
                existing.Value = value;
        }
    }

    private static void MergeSoftware(Asset asset, DiscoveredAsset incoming)
    {
        foreach (var sw in incoming.Software)
        {
            var existing = asset.Software.FirstOrDefault(s =>
                s.Name.Equals(sw.Name, StringComparison.OrdinalIgnoreCase) && s.Source == incoming.Source);
            if (existing is null)
            {
                asset.Software.Add(new AssetSoftware
                {
                    AssetId = asset.Id,
                    Name = sw.Name,
                    Version = sw.Version,
                    Vendor = sw.Vendor,
                    Source = incoming.Source,
                    LastSeen = incoming.SeenAt
                });
            }
            else
            {
                existing.Version = sw.Version ?? existing.Version;
                existing.LastSeen = incoming.SeenAt;
            }
        }
    }

    private static Dictionary<string, string> ParseOwners(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
