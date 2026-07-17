using Esar.Application.Abstractions;
using Esar.Application.Matching;
using Esar.Domain.Enums;

namespace Esar.Application.Merging;

public interface ISourcePriorityEngine
{
    /// <summary>Returns the effective priority of a connector for an attribute (lower = wins).</summary>
    Task<int> GetPriorityAsync(ConnectorType connector, string attribute, CancellationToken ct = default);
    /// <summary>True when <paramref name="incoming"/> may overwrite a value currently owned by <paramref name="currentOwner"/>.</summary>
    Task<bool> WinsAsync(ConnectorType incoming, ConnectorType? currentOwner, string attribute, CancellationToken ct = default);
}

public class SourcePriorityEngine : ISourcePriorityEngine
{
    private const int DefaultPriority = 1000;
    private readonly IUnitOfWork _uow;
    private readonly ICacheService _cache;

    public SourcePriorityEngine(IUnitOfWork uow, ICacheService cache)
    {
        _uow = uow;
        _cache = cache;
    }

    public async Task<int> GetPriorityAsync(ConnectorType connector, string attribute, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        // Attribute-specific override wins over the connector's global priority.
        var specific = all.FirstOrDefault(p => p.ConnectorType == connector &&
            string.Equals(p.Attribute, attribute, StringComparison.OrdinalIgnoreCase));
        if (specific is not null) return specific.Priority;
        var global = all.FirstOrDefault(p => p.ConnectorType == connector && p.Attribute == null);
        return global?.Priority ?? DefaultPriority;
    }

    public async Task<bool> WinsAsync(ConnectorType incoming, ConnectorType? currentOwner, string attribute,
        CancellationToken ct = default)
    {
        if (currentOwner is null) return true;
        if (incoming == currentOwner) return true; // a source may always refresh its own value
        var incomingPriority = await GetPriorityAsync(incoming, attribute, ct);
        var ownerPriority = await GetPriorityAsync(currentOwner.Value, attribute, ct);
        return incomingPriority <= ownerPriority;
    }

    private async Task<List<Domain.Entities.SourcePriority>> GetAllAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<Domain.Entities.SourcePriority>>(CacheKeys.SourcePriorities, ct);
        if (cached is { Count: > 0 }) return cached;
        var all = await _uow.SourcePriorities.ListAsync(null, ct);
        await _cache.SetAsync(CacheKeys.SourcePriorities, all, TimeSpan.FromMinutes(5), ct);
        return all;
    }
}
