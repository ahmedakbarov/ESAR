using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Application.Contracts;
using Esar.Application.Ingestion;
using Esar.Application.Merging;
using Esar.Application.Normalization;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentValidation;
using MediatR;

namespace Esar.Application.Assets;

// ---------- Create ----------
public record CreateAssetCommand(string Hostname, string? Fqdn, string? OperatingSystem, string AssetType,
    string Environment, string Criticality, string? OwnerName, string? Department, string? BusinessUnit,
    string? Location, string? Classification, List<string>? IpAddresses) : IRequest<AssetDto>;

public class CreateAssetValidator : AbstractValidator<CreateAssetCommand>
{
    public CreateAssetValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(255);
        RuleFor(x => x.AssetType).IsEnumName(typeof(AssetType), caseSensitive: false);
        RuleFor(x => x.Environment).IsEnumName(typeof(EnvironmentType), caseSensitive: false);
        RuleFor(x => x.Criticality).IsEnumName(typeof(CriticalityLevel), caseSensitive: false);
    }
}

public class CreateAssetHandler : IRequestHandler<CreateAssetCommand, AssetDto>
{
    private readonly IUnitOfWork _uow;
    private readonly INormalizationService _normalization;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;

    public CreateAssetHandler(IUnitOfWork uow, INormalizationService normalization, IAuditService audit,
        ICurrentUserService user)
    {
        _uow = uow;
        _normalization = normalization;
        _audit = audit;
        _user = user;
    }

    public async Task<AssetDto> Handle(CreateAssetCommand request, CancellationToken ct)
    {
        var asset = new Asset
        {
            Hostname = request.Hostname.Trim(),
            NormalizedHostname = _normalization.NormalizeHostname(request.Hostname),
            Fqdn = request.Fqdn?.Trim().ToLowerInvariant(),
            OperatingSystem = _normalization.NormalizeOs(request.OperatingSystem),
            AssetType = Enum.Parse<AssetType>(request.AssetType, true),
            Environment = Enum.Parse<EnvironmentType>(request.Environment, true),
            Criticality = Enum.Parse<CriticalityLevel>(request.Criticality, true),
            OwnerName = request.OwnerName,
            Department = request.Department,
            BusinessUnit = request.BusinessUnit,
            Location = request.Location,
            Classification = request.Classification,
            CreatedBy = _user.UserName
        };
        asset.Sources.Add(new AssetSource
        {
            AssetId = asset.Id,
            ConnectorType = ConnectorType.ManualImport,
            ExternalId = asset.Id.ToString()
        });
        foreach (var ip in request.IpAddresses ?? new List<string>())
        {
            var normalizedIp = _normalization.NormalizeIp(ip);
            if (normalizedIp is not null)
                asset.IpAddresses.Add(new AssetIp
                {
                    AssetId = asset.Id, IpAddress = normalizedIp, Source = ConnectorType.ManualImport
                });
        }

        await _uow.Assets.AddAsync(asset, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.AssetCreated, nameof(Asset), asset.Id.ToString(),
            new { asset.Hostname }, ct);
        return AssetDto.From(asset);
    }
}

// ---------- Update ----------
public record UpdateAssetCommand(Guid Id, string? OwnerName, string? OwnerEmail, string? Department,
    string? BusinessUnit, string? Location, string? Classification, string? Environment, string? Criticality,
    string? LifecycleStatus, string? Status, bool? PolicyExempt = null) : IRequest<AssetDto?>;

public class UpdateAssetHandler : IRequestHandler<UpdateAssetCommand, AssetDto?>
{
    private readonly IUnitOfWork _uow;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;

    public UpdateAssetHandler(IUnitOfWork uow, IAuditService audit, ICurrentUserService user)
    {
        _uow = uow;
        _audit = audit;
        _user = user;
    }

    public async Task<AssetDto?> Handle(UpdateAssetCommand request, CancellationToken ct)
    {
        var asset = await _uow.Assets.GetWithDetailsAsync(request.Id, ct);
        if (asset is null || asset.IsDeleted) return null;

        var changes = new List<AssetHistory>();
        Dictionary<string, string> attributeOwners;
        try
        {
            attributeOwners = JsonSerializer.Deserialize<Dictionary<string, string>>(asset.AttributeSourcesJson)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            attributeOwners = new Dictionary<string, string>();
        }
        void Track(string field, string? oldValue, string? newValue)
        {
            if (newValue is null || oldValue == newValue) return;
            changes.Add(new AssetHistory
            {
                AssetId = asset.Id, FieldName = field, OldValue = oldValue, NewValue = newValue,
                ChangedBy = _user.UserName
            });
            if (field is nameof(Asset.OwnerName) or nameof(Asset.OwnerEmail) or nameof(Asset.Department)
                or nameof(Asset.BusinessUnit) or nameof(Asset.Location) or nameof(Asset.Classification)
                or nameof(Asset.Environment) or nameof(Asset.Criticality))
                attributeOwners[field] = "Manual";
        }

        Track(nameof(asset.OwnerName), asset.OwnerName, request.OwnerName);
        asset.OwnerName = request.OwnerName ?? asset.OwnerName;
        Track(nameof(asset.OwnerEmail), asset.OwnerEmail, request.OwnerEmail);
        asset.OwnerEmail = request.OwnerEmail ?? asset.OwnerEmail;
        Track(nameof(asset.Department), asset.Department, request.Department);
        asset.Department = request.Department ?? asset.Department;
        Track(nameof(asset.BusinessUnit), asset.BusinessUnit, request.BusinessUnit);
        asset.BusinessUnit = request.BusinessUnit ?? asset.BusinessUnit;
        Track(nameof(asset.Location), asset.Location, request.Location);
        asset.Location = request.Location ?? asset.Location;
        Track(nameof(asset.Classification), asset.Classification, request.Classification);
        asset.Classification = request.Classification ?? asset.Classification;

        if (request.Environment is not null && Enum.TryParse<EnvironmentType>(request.Environment, true, out var env))
        {
            Track(nameof(asset.Environment), asset.Environment.ToString(), env.ToString());
            asset.Environment = env;
        }
        if (request.Criticality is not null && Enum.TryParse<CriticalityLevel>(request.Criticality, true, out var crit))
        {
            Track(nameof(asset.Criticality), asset.Criticality.ToString(), crit.ToString());
            asset.Criticality = crit;
        }
        if (request.LifecycleStatus is not null && Enum.TryParse<LifecycleStatus>(request.LifecycleStatus, true, out var lc))
        {
            Track(nameof(asset.LifecycleStatus), asset.LifecycleStatus.ToString(), lc.ToString());
            asset.LifecycleStatus = lc;
        }
        if (request.Status is not null && Enum.TryParse<AssetStatus>(request.Status, true, out var st))
        {
            Track(nameof(asset.Status), asset.Status.ToString(), st.ToString());
            asset.Status = st;
        }
        if (request.PolicyExempt is { } exempt && exempt != asset.PolicyExempt)
        {
            Track(nameof(asset.PolicyExempt), asset.PolicyExempt.ToString(), exempt.ToString());
            asset.PolicyExempt = exempt;
            if (exempt)
            {
                asset.ComplianceRecords.Clear();
                asset.ComplianceScore = 0;
                asset.ComplianceStatus = ComplianceStatus.Unknown;
            }
        }

        asset.UpdatedAt = DateTime.UtcNow;
        asset.UpdatedBy = _user.UserName;
        asset.AttributeSourcesJson = JsonSerializer.Serialize(attributeOwners);
        _uow.Assets.Update(asset);
        await _uow.AssetHistories.AddRangeAsync(changes, ct);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.AssetUpdated, nameof(Asset), asset.Id.ToString(),
            new { Fields = changes.Select(c => c.FieldName) }, ct);
        return AssetDto.From(asset);
    }
}

// ---------- Delete (soft) ----------
public record DeleteAssetCommand(Guid Id) : IRequest<bool>;

public class DeleteAssetHandler : IRequestHandler<DeleteAssetCommand, bool>
{
    private readonly IUnitOfWork _uow;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;
    private readonly IEventBus _events;

    public DeleteAssetHandler(IUnitOfWork uow, IAuditService audit, ICurrentUserService user, IEventBus events)
    {
        _uow = uow;
        _audit = audit;
        _user = user;
        _events = events;
    }

    public async Task<bool> Handle(DeleteAssetCommand request, CancellationToken ct)
    {
        var asset = await _uow.Assets.GetByIdAsync(request.Id, ct);
        if (asset is null || asset.IsDeleted) return false;
        asset.IsDeleted = true;
        asset.Status = AssetStatus.Decommissioned;
        asset.LifecycleStatus = LifecycleStatus.Retired;
        asset.UpdatedAt = DateTime.UtcNow;
        asset.UpdatedBy = _user.UserName;
        _uow.Assets.Update(asset);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.AssetDeleted, nameof(Asset), asset.Id.ToString(), null, ct);
        await _events.PublishAsync(EventTopics.AssetDeleted,
            new { AssetId = asset.Id, asset.Hostname, DeletedBy = _user.UserName }, ct);
        return true;
    }
}

// ---------- Manual merge (dedup) ----------
public record MergeAssetsCommand(Guid SurvivorId, Guid DuplicateId) : IRequest<bool>;

public class MergeAssetsHandler : IRequestHandler<MergeAssetsCommand, bool>
{
    private readonly IUnitOfWork _uow;
    private readonly IMergeEngine _merge;
    private readonly IAuditService _audit;
    private readonly ICurrentUserService _user;

    public MergeAssetsHandler(IUnitOfWork uow, IMergeEngine merge, IAuditService audit, ICurrentUserService user)
    {
        _uow = uow;
        _merge = merge;
        _audit = audit;
        _user = user;
    }

    public async Task<bool> Handle(MergeAssetsCommand request, CancellationToken ct)
    {
        if (request.SurvivorId == request.DuplicateId) return false;
        var survivor = await _uow.Assets.GetWithDetailsAsync(request.SurvivorId, ct);
        var duplicate = await _uow.Assets.GetWithDetailsAsync(request.DuplicateId, ct);
        if (survivor is null || duplicate is null || survivor.IsDeleted || duplicate.IsDeleted) return false;

        await _merge.MergeAssetsAsync(survivor, duplicate, _user.UserName, ct);
        _uow.Assets.Update(survivor);
        _uow.Assets.Update(duplicate);
        await _uow.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditAction.AssetMerged, nameof(Asset), survivor.Id.ToString(),
            new { DuplicateId = duplicate.Id }, ct);
        return true;
    }
}

// ---------- Bulk import ----------
public record BulkImportAssetsCommand(List<CreateAssetCommand> Assets) : IRequest<BulkImportResult>;
public record BulkImportResult(int Imported, int Failed, List<string> Errors);

public class BulkImportAssetsHandler : IRequestHandler<BulkImportAssetsCommand, BulkImportResult>
{
    private readonly IAssetIngestionService _ingestion;
    private readonly INormalizationService _normalization;

    public BulkImportAssetsHandler(IAssetIngestionService ingestion, INormalizationService normalization)
    {
        _ingestion = ingestion;
        _normalization = normalization;
    }

    public async Task<BulkImportResult> Handle(BulkImportAssetsCommand request, CancellationToken ct)
    {
        int imported = 0, failed = 0;
        var errors = new List<string>();
        foreach (var item in request.Assets)
        {
            try
            {
                // Bulk import flows through the full ingestion pipeline so matching/dedup still applies.
                var discovered = new DiscoveredAsset
                {
                    Source = ConnectorType.ManualImport,
                    ExternalId = $"import:{_normalization.NormalizeHostname(item.Hostname)}",
                    Hostname = item.Hostname,
                    Fqdn = item.Fqdn,
                    OperatingSystem = item.OperatingSystem,
                    AssetType = Enum.TryParse<AssetType>(item.AssetType, true, out var at) ? at : null,
                    Environment = Enum.TryParse<EnvironmentType>(item.Environment, true, out var env) ? env : null,
                    Criticality = Enum.TryParse<CriticalityLevel>(item.Criticality, true, out var crit) ? crit : null,
                    OwnerName = item.OwnerName,
                    Department = item.Department,
                    BusinessUnit = item.BusinessUnit,
                    Location = item.Location,
                    Interfaces = (item.IpAddresses ?? new List<string>())
                        .Select(ip => new DiscoveredInterface { IpAddress = ip }).ToList()
                };
                var outcome = await _ingestion.IngestAsync(discovered, ct);
                if (outcome == IngestionOutcome.Failed) { failed++; errors.Add($"{item.Hostname}: ingestion failed"); }
                else imported++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{item.Hostname}: {ex.Message}");
            }
        }
        return new BulkImportResult(imported, failed, errors);
    }
}
