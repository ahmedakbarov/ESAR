using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Relationships;

public interface IRelationshipService
{
    Task<AssetRelationship?> AddAsync(Guid sourceId, Guid targetId, RelationshipType type,
        string? description, string createdBy, ConnectorType source = ConnectorType.ManualImport,
        CancellationToken ct = default);
    Task<bool> RemoveAsync(Guid relationshipId, CancellationToken ct = default);
    Task<List<AssetRelationship>> GetForAssetAsync(Guid assetId, CancellationToken ct = default);
    /// <summary>Graph traversal: what breaks if this asset fails, and what it depends on.</summary>
    Task<ImpactAnalysisResult> AnalyzeImpactAsync(Guid assetId, int maxDepth = 3, CancellationToken ct = default);
}

public record ImpactNode(Guid AssetId, string Hostname, string AssetType, string Criticality,
    string RelationshipPath, int Depth);
public record ImpactAnalysisResult(Guid AssetId, string Hostname,
    IReadOnlyList<ImpactNode> ImpactedAssets, IReadOnlyList<ImpactNode> Dependencies);

public class RelationshipService : IRelationshipService
{
    /// <summary>Types where the SOURCE is affected when the TARGET fails ("source depends on target").</summary>
    private static readonly RelationshipType[] DependencyTypes =
    {
        RelationshipType.DependsOn, RelationshipType.RunsOn, RelationshipType.Uses,
        RelationshipType.PartOfService, RelationshipType.MemberOfCluster,
        RelationshipType.ProtectedBy, RelationshipType.BackedUpBy
    };

    /// <summary>Types where the TARGET is affected when the SOURCE fails ("source hosts/contains target").</summary>
    private static readonly RelationshipType[] HostingTypes =
        { RelationshipType.Hosts, RelationshipType.Contains };

    private readonly IUnitOfWork _uow;
    private readonly ILogger<RelationshipService> _logger;

    public RelationshipService(IUnitOfWork uow, ILogger<RelationshipService> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    public async Task<AssetRelationship?> AddAsync(Guid sourceId, Guid targetId, RelationshipType type,
        string? description, string createdBy, ConnectorType source = ConnectorType.ManualImport,
        CancellationToken ct = default)
    {
        if (sourceId == targetId) return null;
        var existing = await _uow.Relationships.FirstOrDefaultAsync(
            r => r.SourceAssetId == sourceId && r.TargetAssetId == targetId && r.Type == type, ct);
        if (existing is not null)
        {
            existing.IsActive = true;
            existing.LastSeen = DateTime.UtcNow;
            existing.Description = description ?? existing.Description;
            _uow.Relationships.Update(existing);
            await _uow.SaveChangesAsync(ct);
            return existing;
        }

        var sourceAsset = await _uow.Assets.GetByIdAsync(sourceId, ct);
        var targetAsset = await _uow.Assets.GetByIdAsync(targetId, ct);
        if (sourceAsset is null || targetAsset is null) return null;

        var relationship = new AssetRelationship
        {
            SourceAssetId = sourceId,
            TargetAssetId = targetId,
            Type = type,
            Description = description,
            Source = source,
            CreatedBy = createdBy
        };
        await _uow.Relationships.AddAsync(relationship, ct);
        await _uow.SaveChangesAsync(ct);
        _logger.LogInformation("Relationship {Source} -{Type}-> {Target} created by {User}",
            sourceAsset.Hostname, type, targetAsset.Hostname, createdBy);
        return relationship;
    }

    public async Task<bool> RemoveAsync(Guid relationshipId, CancellationToken ct = default)
    {
        var relationship = await _uow.Relationships.GetByIdAsync(relationshipId, ct);
        if (relationship is null) return false;
        _uow.Relationships.Remove(relationship);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    public Task<List<AssetRelationship>> GetForAssetAsync(Guid assetId, CancellationToken ct = default)
        => _uow.Relationships.ListAsync(
            r => r.IsActive && (r.SourceAssetId == assetId || r.TargetAssetId == assetId), ct);

    public async Task<ImpactAnalysisResult> AnalyzeImpactAsync(Guid assetId, int maxDepth = 3,
        CancellationToken ct = default)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 6);
        var root = await _uow.Assets.GetByIdAsync(assetId, ct)
            ?? throw new InvalidOperationException($"Asset {assetId} not found.");

        var edges = await _uow.Relationships.ListAsync(r => r.IsActive, ct);

        // Downstream: assets impacted when the root fails.
        var impacted = Traverse(assetId, edges, maxDepth, downstream: true);
        // Upstream: assets the root depends on.
        var dependencies = Traverse(assetId, edges, maxDepth, downstream: false);

        var referencedIds = impacted.Keys.Concat(dependencies.Keys).Distinct().ToList();
        var assets = (await _uow.Assets.ListAsync(a => referencedIds.Contains(a.Id), ct))
            .ToDictionary(a => a.Id);

        List<ImpactNode> Materialize(Dictionary<Guid, (string Path, int Depth)> nodes) => nodes
            .Where(kv => assets.ContainsKey(kv.Key))
            .Select(kv =>
            {
                var asset = assets[kv.Key];
                return new ImpactNode(asset.Id, asset.Hostname, asset.AssetType.ToString(),
                    asset.Criticality.ToString(), kv.Value.Path, kv.Value.Depth);
            })
            .OrderBy(n => n.Depth).ThenByDescending(n => n.Criticality)
            .ToList();

        return new ImpactAnalysisResult(root.Id, root.Hostname, Materialize(impacted), Materialize(dependencies));
    }

    private static Dictionary<Guid, (string Path, int Depth)> Traverse(Guid rootId,
        List<AssetRelationship> edges, int maxDepth, bool downstream)
    {
        var visited = new Dictionary<Guid, (string Path, int Depth)>();
        var queue = new Queue<(Guid Id, string Path, int Depth)>();
        queue.Enqueue((rootId, "", 0));
        var seen = new HashSet<Guid> { rootId };

        while (queue.Count > 0)
        {
            var (current, path, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (var edge in edges)
            {
                Guid? next = null;
                if (downstream)
                {
                    // Who is impacted if `current` fails?
                    if (edge.TargetAssetId == current && DependencyTypes.Contains(edge.Type))
                        next = edge.SourceAssetId;         // dependent → impacted
                    else if (edge.SourceAssetId == current && HostingTypes.Contains(edge.Type))
                        next = edge.TargetAssetId;         // hosted workload → impacted
                }
                else
                {
                    // What does `current` depend on?
                    if (edge.SourceAssetId == current && DependencyTypes.Contains(edge.Type))
                        next = edge.TargetAssetId;
                    else if (edge.TargetAssetId == current && HostingTypes.Contains(edge.Type))
                        next = edge.SourceAssetId;
                }

                if (next is null || !seen.Add(next.Value)) continue;
                var nextPath = string.IsNullOrEmpty(path) ? edge.Type.ToString() : $"{path} → {edge.Type}";
                visited[next.Value] = (nextPath, depth + 1);
                queue.Enqueue((next.Value, nextPath, depth + 1));
            }
        }
        return visited;
    }
}
