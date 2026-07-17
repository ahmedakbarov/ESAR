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
    public bool IncludeDeleted { get; set; }
    public string SortBy { get; set; } = "hostname";
    public bool SortDescending { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
