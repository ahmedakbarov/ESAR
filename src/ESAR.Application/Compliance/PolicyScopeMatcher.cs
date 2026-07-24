using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;

namespace Esar.Application.Compliance;

/// <summary>
/// Parsed/compiled form of a CompliancePolicy's scope filters. Building one involves JSON
/// parsing, regex compilation and CIDR parsing — comparatively expensive next to the match
/// itself, so PolicyEngine computes this once per policy (alongside its 5-minute policy cache)
/// instead of once per asset evaluated against it.
/// </summary>
public sealed class CompiledPolicyScope
{
    public IReadOnlyList<string> AssetTypes { get; }
    public IReadOnlyList<string> Environments { get; }
    public CriticalityLevel? MinCriticality { get; }
    public IReadOnlyList<string> Connectors { get; }
    public IReadOnlyList<(string Key, string? Value)> Tags { get; }
    public IReadOnlyList<Regex> HostnamePatterns { get; }
    public IReadOnlyList<IPNetwork> IpRanges { get; }
    public IReadOnlyList<string> Subscriptions { get; }

    private CompiledPolicyScope(CompliancePolicy policy)
    {
        AssetTypes = PolicyScopeMatcher.ParseStrings(policy.AppliesToAssetTypesJson);
        Environments = PolicyScopeMatcher.ParseStrings(policy.AppliesToEnvironmentsJson);
        MinCriticality = policy.MinCriticality;
        Connectors = PolicyScopeMatcher.ParseStrings(policy.AppliesToConnectorsJson);
        Tags = PolicyScopeMatcher.ParseStrings(policy.AppliesToTagsJson)
            .Select(PolicyScopeMatcher.ParseTagFilter).ToList();
        HostnamePatterns = PolicyScopeMatcher.ParseStrings(policy.AppliesToHostnamePatternsJson)
            .Select(PolicyScopeMatcher.CompileGlob).ToList();
        IpRanges = PolicyScopeMatcher.ParseStrings(policy.AppliesToIpRangesJson)
            .Select(r => PolicyScopeMatcher.TryParseCidr(r, out var n) ? n : (IPNetwork?)null)
            .Where(n => n is not null).Select(n => n!.Value).ToList();
        Subscriptions = PolicyScopeMatcher.ParseStrings(policy.AppliesToSubscriptionsJson);
    }

    public static CompiledPolicyScope From(CompliancePolicy policy) => new(policy);
}

/// <summary>
/// Decides whether a CompliancePolicy's scope covers a given asset. Operates on an already-loaded
/// Asset (Sources/Tags/IpAddresses included — see AssetRepository.GetWithDetailsAsync) — every
/// dimension here reads a navigation property that ComplianceEngine already requires to be
/// populated, so no additional query work is needed at the call site.
/// </summary>
public static class PolicyScopeMatcher
{
    public static bool Applies(CompiledPolicyScope scope, Asset asset)
    {
        if (!MatchesSet(scope.AssetTypes, asset.AssetType.ToString())) return false;
        if (!MatchesSet(scope.Environments, asset.Environment.ToString())) return false;
        if (scope.MinCriticality is { } min && asset.Criticality < min) return false;
        if (!MatchesAny(scope.Connectors, asset.Sources.Select(s => s.ConnectorType.ToString()))) return false;
        if (!MatchesTags(scope.Tags, asset.Tags)) return false;
        if (!MatchesHostnames(scope.HostnamePatterns, asset.NormalizedHostname)) return false;
        if (!MatchesIpRanges(scope.IpRanges, asset.IpAddresses.Where(ip => ip.IsActive).ToList())) return false;
        if (!MatchesSet(scope.Subscriptions, asset.CloudSubscriptionId)) return false;
        return true;
    }

    /// <summary>Convenience overload for tests and one-off checks — compiles the policy on every
    /// call. PolicyEngine's evaluation hot path uses the CompiledPolicyScope overload instead,
    /// via its policy cache, so a fleet-wide sweep does not recompile per asset.</summary>
    public static bool Applies(CompliancePolicy policy, Asset asset)
        => Applies(CompiledPolicyScope.From(policy), asset);

    private static bool MatchesSet(IReadOnlyList<string> allowed, string? value)
        => allowed.Count == 0 || (value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesAny(IReadOnlyList<string> allowed, IEnumerable<string> candidates)
        => allowed.Count == 0 || candidates.Any(v => allowed.Contains(v, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesTags(IReadOnlyList<(string Key, string? Value)> filters, ICollection<AssetTag> tags)
    {
        if (filters.Count == 0) return true;
        foreach (var (key, value) in filters)
        {
            var matched = value is null
                ? tags.Any(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                : tags.Any(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                             && t.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (matched) return true;
        }
        return false;
    }

    private static bool MatchesHostnames(IReadOnlyList<Regex> patterns, string normalizedHostname)
        => patterns.Count == 0 ||
           (!string.IsNullOrEmpty(normalizedHostname) && patterns.Any(p => p.IsMatch(normalizedHostname)));

    private static bool MatchesIpRanges(IReadOnlyList<IPNetwork> ranges, ICollection<AssetIp> ips)
        => ranges.Count == 0 || ips.Any(ip => IPAddress.TryParse(ip.IpAddress, out var addr) &&
            ranges.Any(n => n.BaseAddress.AddressFamily == addr.AddressFamily && n.Contains(addr)));

    /// <summary>"key" -> (key, null) means "any value"; "key=value" means an exact, case-insensitive match.</summary>
    internal static (string Key, string? Value) ParseTagFilter(string entry)
    {
        var eq = entry.IndexOf('=');
        return eq < 0 ? (entry.Trim(), null) : (entry[..eq].Trim(), entry[(eq + 1)..].Trim());
    }

    internal static Regex CompileGlob(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Bare IP without "/N" is widened to a host route (/32 IPv4, /128 IPv6) as a UX nicety.</summary>
    public static bool TryParseCidr(string range, out IPNetwork network)
    {
        var candidate = range.Contains('/') ? range : range + (range.Contains(':') ? "/128" : "/32");
        return IPNetwork.TryParse(candidate, out network);
    }

    internal static List<string> ParseStrings(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new List<string>(); }
    }
}
