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
    private readonly INormalizationService _normalization;
    private readonly ILogger<MergeEngine> _logger;

    public MergeEngine(IUnitOfWork uow, ISourcePriorityEngine priority, INormalizationService normalization,
        ILogger<MergeEngine> logger)
    {
        _uow = uow;
        _priority = priority;
        _normalization = normalization;
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
            // Values explicitly curated by an operator are never silently replaced by discovery.
            if (string.Equals(ownerName, "Manual", StringComparison.OrdinalIgnoreCase)) return;
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

        await Set(nameof(asset.Hostname), asset.Hostname, incoming.Hostname, () =>
        {
            asset.Hostname = incoming.Hostname!;
            asset.NormalizedHostname = _normalization.NormalizeHostname(incoming.Hostname);
        });
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

        await MergeInterfacesAsync(asset, incoming, ct);
        foreach (var (key, value) in incoming.Attributes)
            incoming.Tags.TryAdd($"attribute:{key}", value);
        await MergeTagsAsync(asset, incoming, ct);
        MergeSoftware(asset, incoming);

        asset.LastSeen = incoming.SeenAt > asset.LastSeen ? incoming.SeenAt : asset.LastSeen;
        if (asset.Status is AssetStatus.Offline or AssetStatus.Inactive) asset.Status = AssetStatus.Active;
        asset.AttributeSourcesJson = JsonSerializer.Serialize(owners);
        asset.UpdatedAt = DateTime.UtcNow;
        return changed;
    }

    public async Task MergeAssetsAsync(Asset survivor, Asset duplicate, string mergedBy, CancellationToken ct = default)
    {
        var survivorOwners = ParseOwners(survivor.AttributeSourcesJson);
        var duplicateOwners = ParseOwners(duplicate.AttributeSourcesJson);

        async Task Fill(string field, string? current, string? value, Action apply)
        {
            if (string.IsNullOrWhiteSpace(value) || value == current) return;
            survivorOwners.TryGetValue(field, out var currentOwnerName);
            duplicateOwners.TryGetValue(field, out var incomingOwnerName);
            if (string.Equals(currentOwnerName, "Manual", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.IsNullOrWhiteSpace(current))
            {
                if (string.IsNullOrWhiteSpace(incomingOwnerName) ||
                    !Enum.TryParse<ConnectorType>(incomingOwnerName, out var incomingOwner))
                    return;
                ConnectorType? currentOwner = currentOwnerName is not null &&
                    Enum.TryParse<ConnectorType>(currentOwnerName, out var parsedCurrent) ? parsedCurrent : null;
                if (!await _priority.WinsAsync(incomingOwner, currentOwner, field, ct)) return;
            }
            apply();
            if (duplicateOwners.TryGetValue(field, out var owner)) survivorOwners[field] = owner;
            await _uow.AssetHistories.AddAsync(new AssetHistory
            {
                AssetId = survivor.Id,
                FieldName = field,
                OldValue = current,
                NewValue = value,
                ChangedBy = mergedBy
            }, ct);
        }

        await Fill(nameof(Asset.Hostname), survivor.Hostname, duplicate.Hostname, () =>
        {
            survivor.Hostname = duplicate.Hostname;
            survivor.NormalizedHostname = _normalization.NormalizeHostname(duplicate.Hostname);
        });
        await Fill(nameof(Asset.Fqdn), survivor.Fqdn, duplicate.Fqdn, () => survivor.Fqdn = duplicate.Fqdn);
        await Fill(nameof(Asset.Domain), survivor.Domain, duplicate.Domain, () => survivor.Domain = duplicate.Domain);
        await Fill(nameof(Asset.OperatingSystem), survivor.OperatingSystem, duplicate.OperatingSystem,
            () => survivor.OperatingSystem = duplicate.OperatingSystem);
        await Fill(nameof(Asset.OsVersion), survivor.OsVersion, duplicate.OsVersion,
            () => survivor.OsVersion = duplicate.OsVersion);
        await Fill(nameof(Asset.SerialNumber), survivor.SerialNumber, duplicate.SerialNumber,
            () => survivor.SerialNumber = duplicate.SerialNumber);
        await Fill(nameof(Asset.BiosUuid), survivor.BiosUuid, duplicate.BiosUuid,
            () => survivor.BiosUuid = duplicate.BiosUuid);
        await Fill(nameof(Asset.Manufacturer), survivor.Manufacturer, duplicate.Manufacturer,
            () => survivor.Manufacturer = duplicate.Manufacturer);
        await Fill(nameof(Asset.Model), survivor.Model, duplicate.Model, () => survivor.Model = duplicate.Model);
        await Fill(nameof(Asset.CloudProvider), survivor.CloudProvider, duplicate.CloudProvider,
            () => survivor.CloudProvider = duplicate.CloudProvider);
        await Fill(nameof(Asset.CloudResourceId), survivor.CloudResourceId, duplicate.CloudResourceId,
            () => survivor.CloudResourceId = duplicate.CloudResourceId);
        await Fill(nameof(Asset.CloudRegion), survivor.CloudRegion, duplicate.CloudRegion,
            () => survivor.CloudRegion = duplicate.CloudRegion);
        await Fill(nameof(Asset.CloudSubscriptionId), survivor.CloudSubscriptionId, duplicate.CloudSubscriptionId,
            () => survivor.CloudSubscriptionId = duplicate.CloudSubscriptionId);
        await Fill(nameof(Asset.CloudAccountId), survivor.CloudAccountId, duplicate.CloudAccountId,
            () => survivor.CloudAccountId = duplicate.CloudAccountId);
        await Fill(nameof(Asset.OwnerName), survivor.OwnerName, duplicate.OwnerName,
            () => survivor.OwnerName = duplicate.OwnerName);
        await Fill(nameof(Asset.OwnerEmail), survivor.OwnerEmail, duplicate.OwnerEmail,
            () => survivor.OwnerEmail = duplicate.OwnerEmail);
        await Fill(nameof(Asset.Department), survivor.Department, duplicate.Department,
            () => survivor.Department = duplicate.Department);
        await Fill(nameof(Asset.BusinessUnit), survivor.BusinessUnit, duplicate.BusinessUnit,
            () => survivor.BusinessUnit = duplicate.BusinessUnit);
        await Fill(nameof(Asset.Location), survivor.Location, duplicate.Location,
            () => survivor.Location = duplicate.Location);
        await Fill(nameof(Asset.Classification), survivor.Classification, duplicate.Classification,
            () => survivor.Classification = duplicate.Classification);

        if (survivor.AssetType == AssetType.Unknown) survivor.AssetType = duplicate.AssetType;
        if (survivor.Environment == EnvironmentType.Unknown) survivor.Environment = duplicate.Environment;
        if (survivor.Criticality == CriticalityLevel.Unknown) survivor.Criticality = duplicate.Criticality;

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
            var existing = survivor.Tags.FirstOrDefault(t =>
                t.Key.Equals(tag.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                tag.AssetId = survivor.Id;
                survivor.Tags.Add(tag);
            }
            else if (await _priority.WinsAsync(tag.Source, existing.Source, $"Tag:{tag.Key}", ct))
            {
                existing.Value = tag.Value;
                existing.Source = tag.Source;
            }
        }
        foreach (var identifier in duplicate.Identifiers.ToList())
        {
            if (survivor.Identifiers.Any(existing =>
                    existing.Namespace == identifier.Namespace &&
                    existing.NormalizedValue == identifier.NormalizedValue &&
                    existing.Source == identifier.Source))
                continue;
            identifier.AssetId = survivor.Id;
            survivor.Identifiers.Add(identifier);
        }
        foreach (var software in duplicate.Software.ToList())
        {
            if (survivor.Software.Any(existing =>
                    existing.Source == software.Source &&
                    existing.Name.Equals(software.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            software.AssetId = survivor.Id;
            survivor.Software.Add(software);
        }

        foreach (var match in await _uow.MatchRecords.ListAsync(record =>
                     record.MatchedAssetId == duplicate.Id || record.CreatedAssetId == duplicate.Id, ct))
        {
            if (match.MatchedAssetId == duplicate.Id) match.MatchedAssetId = survivor.Id;
            if (match.CreatedAssetId == duplicate.Id) match.CreatedAssetId = survivor.Id;
            _uow.MatchRecords.Update(match);
        }
        foreach (var approval in await _uow.Approvals.ListAsync(item => item.AssetId == duplicate.Id, ct))
        {
            approval.AssetId = survivor.Id;
            _uow.Approvals.Update(approval);
        }
        foreach (var incident in await _uow.Incidents.ListAsync(item => item.AssetId == duplicate.Id, ct))
        {
            incident.AssetId = survivor.Id;
            _uow.Incidents.Update(incident);
        }
        await ReparentRelationshipsAsync(survivor.Id, duplicate.Id, ct);
        await ReparentComplianceAsync(survivor.Id, duplicate.Id, ct);
        foreach (var assetEvent in await _uow.AssetEvents.ListAsync(item => item.AssetId == duplicate.Id, ct))
        {
            assetEvent.AssetId = survivor.Id;
            _uow.AssetEvents.Update(assetEvent);
        }
        var duplicateRisk = await _uow.AssetRisks.FirstOrDefaultAsync(item => item.AssetId == duplicate.Id, ct);
        var survivorRisk = await _uow.AssetRisks.FirstOrDefaultAsync(item => item.AssetId == survivor.Id, ct);
        if (duplicateRisk is not null)
        {
            if (survivorRisk is null)
            {
                duplicateRisk.AssetId = survivor.Id;
                _uow.AssetRisks.Update(duplicateRisk);
            }
            else
            {
                survivorRisk.RiskScore = Math.Max(survivorRisk.RiskScore, duplicateRisk.RiskScore);
                survivorRisk.VulnerabilitiesCritical =
                    Math.Max(survivorRisk.VulnerabilitiesCritical, duplicateRisk.VulnerabilitiesCritical);
                survivorRisk.VulnerabilitiesHigh =
                    Math.Max(survivorRisk.VulnerabilitiesHigh, duplicateRisk.VulnerabilitiesHigh);
                survivorRisk.VulnerabilitiesMedium =
                    Math.Max(survivorRisk.VulnerabilitiesMedium, duplicateRisk.VulnerabilitiesMedium);
                survivorRisk.VulnerabilitiesLow =
                    Math.Max(survivorRisk.VulnerabilitiesLow, duplicateRisk.VulnerabilitiesLow);
                survivorRisk.ExposureScore = Math.Max(survivorRisk.ExposureScore, duplicateRisk.ExposureScore);
                survivorRisk.LastCalculatedAt =
                    survivorRisk.LastCalculatedAt > duplicateRisk.LastCalculatedAt
                        ? survivorRisk.LastCalculatedAt : duplicateRisk.LastCalculatedAt;
                _uow.AssetRisks.Remove(duplicateRisk);
            }
        }

        survivor.FirstSeen = duplicate.FirstSeen < survivor.FirstSeen ? duplicate.FirstSeen : survivor.FirstSeen;
        survivor.LastSeen = duplicate.LastSeen > survivor.LastSeen ? duplicate.LastSeen : survivor.LastSeen;
        survivor.AttributeSourcesJson = JsonSerializer.Serialize(survivorOwners);
        survivor.UpdatedAt = DateTime.UtcNow;
        survivor.UpdatedBy = mergedBy;

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

    private async Task MergeInterfacesAsync(Asset asset, DiscoveredAsset incoming, CancellationToken ct)
    {
        foreach (var previous in asset.IpAddresses.Where(i => i.Source == incoming.Source && i.IsActive))
        {
            previous.IsActive = false;
            previous.ValidTo = incoming.SeenAt;
            previous.IsPrimary = false;
        }

        foreach (var iface in incoming.Interfaces)
        {
            if (iface.IpAddress is null && iface.MacAddress is null) continue;
            if (iface.IsPrimary)
            {
                foreach (var sourceInterface in asset.IpAddresses.Where(i => i.Source == incoming.Source))
                    sourceInterface.IsPrimary = false;
            }

            // Keep one observation per source. AD confirming an Azure IP must not mutate
            // the Azure observation's provenance or freshness.
            var existing = iface.MacAddress is not null
                ? asset.IpAddresses.FirstOrDefault(i =>
                    i.Source == incoming.Source && i.MacAddress == iface.MacAddress)
                : asset.IpAddresses.FirstOrDefault(i =>
                    i.Source == incoming.Source && i.MacAddress == null &&
                    iface.IpAddress != null && i.IpAddress == iface.IpAddress);
            if (existing is null)
            {
                var created = new AssetIp
                {
                    AssetId = asset.Id,
                    IpAddress = iface.IpAddress ?? string.Empty,
                    MacAddress = iface.MacAddress,
                    IsPrimary = iface.IsPrimary,
                    Source = incoming.Source,
                    FirstSeen = incoming.SeenAt,
                    LastSeen = incoming.SeenAt,
                    IsActive = true
                };
                asset.IpAddresses.Add(created);
                // Asset children use client-generated GUID keys. Register them explicitly
                // so EF inserts, rather than graph-updates, the new interface when the
                // parent asset was loaded from the current DbContext.
                await _uow.AssetIps.AddAsync(created, ct);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(iface.IpAddress))
                    existing.IpAddress = iface.IpAddress;
                if (!string.IsNullOrWhiteSpace(iface.MacAddress))
                    existing.MacAddress = iface.MacAddress;
                existing.IsPrimary = iface.IsPrimary;
                existing.LastSeen = incoming.SeenAt;
                existing.ValidTo = null;
                existing.IsActive = true;
            }
        }
    }

    private async Task MergeTagsAsync(Asset asset, DiscoveredAsset incoming, CancellationToken ct)
    {
        foreach (var (key, value) in incoming.Tags)
        {
            var existing = asset.Tags.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                var created = new AssetTag { AssetId = asset.Id, Key = key, Value = value, Source = incoming.Source };
                asset.Tags.Add(created);
                // See the AssetIp note above: explicit Add avoids a 0-row UPDATE for
                // a new client-keyed dependent during connector ingestion.
                await _uow.AssetTags.AddAsync(created, ct);
            }
            else if (existing.Source == incoming.Source ||
                     await _priority.WinsAsync(incoming.Source, existing.Source, $"Tag:{key}", ct))
            {
                existing.Value = value;
                existing.Source = incoming.Source;
            }
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

    private async Task ReparentRelationshipsAsync(Guid survivorId, Guid duplicateId, CancellationToken ct)
    {
        var relationships = await _uow.Relationships.ListAsync(item =>
            item.SourceAssetId == duplicateId || item.TargetAssetId == duplicateId, ct);
        foreach (var relationship in relationships)
        {
            var sourceId = relationship.SourceAssetId == duplicateId ? survivorId : relationship.SourceAssetId;
            var targetId = relationship.TargetAssetId == duplicateId ? survivorId : relationship.TargetAssetId;
            if (sourceId == targetId || (await _uow.Relationships.FirstOrDefaultAsync(existing =>
                    existing.Id != relationship.Id && existing.SourceAssetId == sourceId &&
                    existing.TargetAssetId == targetId && existing.Type == relationship.Type, ct)) is not null)
            {
                _uow.Relationships.Remove(relationship);
                continue;
            }
            relationship.SourceAssetId = sourceId;
            relationship.TargetAssetId = targetId;
            _uow.Relationships.Update(relationship);
        }
    }

    private async Task ReparentComplianceAsync(Guid survivorId, Guid duplicateId, CancellationToken ct)
    {
        var duplicateRecords = await _uow.AssetCompliance.ListAsync(item => item.AssetId == duplicateId, ct);
        foreach (var record in duplicateRecords)
        {
            var existing = await _uow.AssetCompliance.FirstOrDefaultAsync(item =>
                item.AssetId == survivorId && item.Control == record.Control, ct);
            if (existing is null)
            {
                record.AssetId = survivorId;
                _uow.AssetCompliance.Update(record);
            }
            else
            {
                if (record.CheckedAt > existing.CheckedAt)
                {
                    existing.Status = record.Status;
                    existing.Details = record.Details;
                    existing.EvidenceSource = record.EvidenceSource;
                    existing.CheckedAt = record.CheckedAt;
                }
                _uow.AssetCompliance.Remove(record);
            }
        }
    }
}
