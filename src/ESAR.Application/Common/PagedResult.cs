namespace Esar.Application.Common;

public class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public long TotalCount { get; init; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public class AssetSearchCriteria
{
    public string? Search { get; set; }
    /// <summary>Treat <see cref="Search"/> as a POSIX regular expression over hostname/FQDN.</summary>
    public bool UseRegex { get; set; }
    public string? AssetType { get; set; }
    public string? Status { get; set; }
    public string? LifecycleStatus { get; set; }
    public string? Environment { get; set; }
    public string? Criticality { get; set; }
    public string? ComplianceStatus { get; set; }
    public string? BusinessUnit { get; set; }
    public string? Owner { get; set; }
    public string? Source { get; set; }
    public string? Ip { get; set; }
    public string? Mac { get; set; }
    public string? Os { get; set; }
    public string? Software { get; set; }
    public string? CloudProvider { get; set; }
    public string? TagKey { get; set; }
    public string? TagValue { get; set; }
    public decimal? MaxDataQualityScore { get; set; }
    public bool? PolicyExempt { get; set; }
    public bool IncludeDeleted { get; set; }

    // Excel-style multi-select column filters (repeat the query key: assetTypes=A&assetTypes=B).
    // Values within one list are ORed; different filters are ANDed. The single-value fields above
    // are kept for backward compatibility.
    public List<string>? AssetTypes { get; set; }
    public List<string>? Statuses { get; set; }
    public List<string>? LifecycleStatuses { get; set; }
    public List<string>? Environments { get; set; }
    public List<string>? Criticalities { get; set; }
    public List<string>? ComplianceStatuses { get; set; }
    public List<string>? Sources { get; set; }
    public List<string>? OsNames { get; set; }
    public List<string>? BusinessUnits { get; set; }

    public string SortBy { get; set; } = "hostname";
    public bool SortDescending { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>One distinct value of a filterable asset column, with how many assets carry it.</summary>
public record FilterValue(string Value, int Count);
