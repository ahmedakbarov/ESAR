using System.Globalization;
using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using MatchType = Esar.Domain.Enums.MatchType;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Matching;

public interface IMatchingEngine
{
    /// <summary>Finds the best golden-record match for a discovered asset with an explainable score.</summary>
    Task<MatchResult> MatchAsync(DiscoveredAsset candidate, CancellationToken ct = default);
    /// <summary>Dry-run used by the matching simulation endpoint — never writes.</summary>
    Task<MatchResult> SimulateAsync(DiscoveredAsset candidate, CancellationToken ct = default);
}

public class MatchResult
{
    public MatchDecision Decision { get; set; } = MatchDecision.NewAsset;
    public Asset? MatchedAsset { get; set; }
    public decimal ConfidenceScore { get; set; }
    public MatchType? MatchType { get; set; }
    public List<MatchExplanation> Explanations { get; set; } = new();
    public string ExplanationJson => JsonSerializer.Serialize(Explanations);
}

public record MatchExplanation(string Rule, string Attribute, string? CandidateValue, string? MatchedValue,
    decimal Weight, decimal Contribution, bool Matched);

public class MatchingOptions
{
    /// <summary>Score at or above which a soft match is merged automatically.</summary>
    public decimal AutoMergeThreshold { get; set; } = 0.85m;
    /// <summary>Score at or above which a soft match is queued for manual review.</summary>
    public decimal ReviewThreshold { get; set; } = 0.60m;
    /// <summary>Best and second-best candidates closer than this are considered ambiguous.</summary>
    public decimal AmbiguityDelta { get; set; } = 0.10m;
    /// <summary>Network observations older than this are not identity evidence.</summary>
    public int NetworkEvidenceMaxAgeDays { get; set; } = 30;
}

public class MatchingEngine : IMatchingEngine
{
    private readonly IUnitOfWork _uow;
    private readonly INormalizationService _normalization;
    private readonly ICacheService _cache;
    private readonly ILogger<MatchingEngine> _logger;

    public MatchingEngine(IUnitOfWork uow, INormalizationService normalization, ICacheService cache,
        ILogger<MatchingEngine> logger)
    {
        _uow = uow;
        _normalization = normalization;
        _cache = cache;
        _logger = logger;
    }

    public Task<MatchResult> SimulateAsync(DiscoveredAsset candidate, CancellationToken ct = default)
        => MatchAsync(_normalization.Normalize(candidate), ct);

    public async Task<MatchResult> MatchAsync(DiscoveredAsset candidate, CancellationToken ct = default)
    {
        candidate = _normalization.Normalize(candidate);
        var rules = await GetRulesAsync(ct);
        var options = await GetOptionsAsync(ct);
        var result = new MatchResult();

        // 1. Resolve every supplied hard identifier; all hits must converge.
        var hardHits = new List<(MatchingRule Rule, string Value, Asset Asset)>();
        var hardConflicts = new List<MatchExplanation>();
        foreach (var rule in rules.Where(r => r.MatchType == MatchType.Hard).OrderBy(r => r.Order))
        {
            if (!candidate.Identifiers.TryGetValue(rule.Attribute, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            var hardCandidates = await _uow.Assets.FindHardIdentifierCandidatesAsync(rule.Attribute, value, ct);
            if (hardCandidates.Count == 0) continue;
            if (hardCandidates.Count > 1)
            {
                hardConflicts.Add(new MatchExplanation(
                    "Hard identifier conflict safety policy",
                    rule.Attribute,
                    value,
                    string.Join(",", hardCandidates.Select(candidateAsset => candidateAsset.Id)),
                    1m,
                    0m,
                    false));
                continue;
            }
            hardHits.Add((rule, value, hardCandidates[0]));
        }

        if (hardHits.Count > 0 || hardConflicts.Count > 0)
        {
            var distinctAssets = hardHits.Select(hit => hit.Asset).GroupBy(asset => asset.Id).ToList();
            var asset = distinctAssets.FirstOrDefault()?.First();
            result.MatchedAsset = asset;
            result.ConfidenceScore = 1m;
            result.MatchType = MatchType.Hard;
            result.Explanations.AddRange(hardHits.Select(hit => new MatchExplanation(
                hit.Rule.Name, hit.Rule.Attribute, hit.Value, hit.Value, 1m, 1m, true)));
            result.Explanations.AddRange(hardConflicts);

            if (hardConflicts.Count > 0 || distinctAssets.Count != 1)
            {
                result.Decision = MatchDecision.QueuedForReview;
                result.Explanations.Add(new MatchExplanation(
                    "Cross-identifier convergence safety policy", "HardIdentifierConvergence",
                    string.Join(",", hardHits.Select(hit => $"{hit.Rule.Attribute}={hit.Value}")),
                    string.Join(",", distinctAssets.Select(group => group.Key)), 1m, 0m, false));
            }
            else if (asset!.Status == AssetStatus.Decommissioned)
            {
                result.Decision = MatchDecision.QueuedForReview;
                result.Explanations.Add(new MatchExplanation(
                    "Decommissioned asset safety policy", "AssetStatus", asset.Status.ToString(),
                    asset.Id.ToString(), 1m, 0m, false));
            }
            else result.Decision = MatchDecision.AutoMerged;
            return result;
        }

        // 2. Soft weighted scoring over candidate golden records.
        var softRules = rules.Where(r => r.MatchType == MatchType.Soft).OrderBy(r => r.Order).ToList();
        if (softRules.Count == 0) return result;

        var macs = candidate.Interfaces.Where(i => i.MacAddress != null).Select(i => i.MacAddress!).ToList();
        var ips = candidate.Interfaces.Where(i => IsPrivateAddress(i.IpAddress))
            .Select(i => i.IpAddress!).ToList();
        var normalizedHostname = _normalization.NormalizeHostname(candidate.Hostname);

        var networkEvidenceCutoff = DateTime.UtcNow.AddDays(-options.NetworkEvidenceMaxAgeDays);
        var candidates = await _uow.Assets.FindSoftCandidatesAsync(
            string.IsNullOrEmpty(normalizedHostname) ? null : normalizedHostname, macs, ips,
            networkEvidenceCutoff, ct);
        // Compatibility fallback for custom repository implementations compiled against
        // the earlier contract; the production repository always supports the cutoff overload.
        candidates ??= await _uow.Assets.FindSoftCandidatesAsync(
            string.IsNullOrEmpty(normalizedHostname) ? null : normalizedHostname, macs, ips, ct);
        if (candidates.Count == 0) return result;
        var ranked = candidates
            .Select(asset =>
            {
                var scored = ScoreCandidate(candidate, asset, softRules, normalizedHostname, macs, ips,
                    networkEvidenceCutoff);
                return new { Asset = asset, scored.Score, scored.Explanations };
            })
            .OrderByDescending(candidateScore => candidateScore.Score)
            .ThenBy(candidateScore => candidateScore.Asset.Id)
            .ToList();

        var bestCandidate = ranked.FirstOrDefault();
        if (bestCandidate is null || bestCandidate.Score <= 0) return result;
        var best = bestCandidate.Asset;
        var bestScore = bestCandidate.Score;
        var bestExplanations = bestCandidate.Explanations;
        var secondBest = ranked.Skip(1).FirstOrDefault();

        result.MatchedAsset = best;
        result.ConfidenceScore = Math.Round(bestScore, 4);
        result.MatchType = MatchType.Soft;
        result.Explanations = bestExplanations;

        // An IP address is commonly reassigned, so it cannot be the sole positive
        // soft-match signal for an automatic merge. Keep the candidate linked to
        // the proposed asset for an analyst to review instead.
        var azureAdCounterpart = candidate.Source == ConnectorType.Azure
            ? best.Sources.Any(source => source.ConnectorType == ConnectorType.ActiveDirectory)
            : candidate.Source == ConnectorType.ActiveDirectory &&
                best.Sources.Any(source => source.ConnectorType == ConnectorType.Azure);
        var hostnameMatches = bestExplanations.Any(e => e.Matched &&
            string.Equals(e.Attribute, MatchAttributes.Hostname, StringComparison.OrdinalIgnoreCase));
        var ipMatches = bestExplanations.Any(e => e.Matched &&
            string.Equals(e.Attribute, MatchAttributes.IpAddress, StringComparison.OrdinalIgnoreCase));
        var macMatches = bestExplanations.Any(e => e.Matched &&
            string.Equals(e.Attribute, MatchAttributes.MacAddress, StringComparison.OrdinalIgnoreCase));
        var candidateHasMac = macs.Count > 0;
        var candidateMacs = macs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingMacs = best.IpAddresses.Where(networkInterface =>
                networkInterface.IsActive &&
                networkInterface.LastSeen >= networkEvidenceCutoff &&
                !string.IsNullOrWhiteSpace(networkInterface.MacAddress))
            .Select(networkInterface => networkInterface.MacAddress!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var macConflict = candidateHasMac && existingMacs.Count > 0 && !candidateMacs.Overlaps(existingMacs);
        var hasStrongAzureAdNetworkIdentity = hostnameMatches &&
            (macMatches || (ipMatches && !macConflict));
        // MAC+IP alone can describe a recycled DHCP lease or cloned virtual NIC.
        // Generic auto-merge requires the stable name and MAC to agree.
        var hasStrongGenericIdentity = macMatches && hostnameMatches;
        var positiveEvidenceCount = bestExplanations.Count(e => e.Matched);
        var ambiguous = secondBest is not null && secondBest.Score > 0 &&
            bestScore - secondBest.Score < options.AmbiguityDelta;

        // AD DNS and LDAP observations are not guaranteed to be one NIC record. Restrict
        // Azure-to-AD auto-merges to a matching hostname plus IP or MAC evidence. All weaker
        // or contradictory combinations stay in the existing analyst review workflow.
        if (azureAdCounterpart && best.Status == AssetStatus.Decommissioned)
        {
            result.Decision = MatchDecision.QueuedForReview;
            result.Explanations.Add(new MatchExplanation(
                "Azure-AD network correlation policy",
                MatchAttributes.AzureAdNetworkIdentity,
                $"hostname={hostnameMatches}; ip={ipMatches}; mac={macMatches}",
                "counterpart asset is decommissioned",
                1m, 0m, false));
        }
        else if (ambiguous)
        {
            result.Decision = MatchDecision.QueuedForReview;
            result.Explanations.Add(new MatchExplanation(
                "Ambiguous candidate safety policy",
                "SecondBestCandidate",
                best.Id.ToString(),
                secondBest!.Asset.Id.ToString(),
                1m,
                secondBest.Score,
                false));
        }
        else if (azureAdCounterpart && !hasStrongAzureAdNetworkIdentity)
        {
            result.Decision = MatchDecision.QueuedForReview;
            result.Explanations.Add(new MatchExplanation(
                "Azure-AD network correlation policy",
                MatchAttributes.AzureAdNetworkIdentity,
                $"hostname={hostnameMatches}; ip={ipMatches}; mac={macMatches}",
                macConflict ? "conflicting MAC observations" : "hostname plus IP or MAC is required",
                1m, 0m, false));
        }
        else if (azureAdCounterpart && hasStrongAzureAdNetworkIdentity)
        {
            result.Decision = MatchDecision.AutoMerged;
            result.ConfidenceScore = Math.Max(result.ConfidenceScore, 0.95m);
            result.Explanations.Add(new MatchExplanation(
                "Azure-AD network correlation policy",
                MatchAttributes.AzureAdNetworkIdentity,
                $"hostname={hostnameMatches}; ip={ipMatches}; mac={macMatches}",
                "hostname plus IP or MAC matched",
                1m, 1m, true));
        }
        else if (hasStrongGenericIdentity && bestScore >= options.AutoMergeThreshold)
            result.Decision = MatchDecision.AutoMerged;
        else result.Decision = positiveEvidenceCount > 0 && bestScore >= options.ReviewThreshold
            ? MatchDecision.QueuedForReview
            : MatchDecision.NewAsset;

        if (result.Decision == MatchDecision.NewAsset) result.MatchedAsset = null;
        return result;
    }

    private static (decimal Score, List<MatchExplanation> Explanations) ScoreCandidate(
        DiscoveredAsset candidate, Asset asset,
        List<MatchingRule> rules, string normalizedHostname, List<string> macs, List<string> ips,
        DateTime networkEvidenceCutoff)
    {
        decimal total = 0;
        decimal achieved = 0;
        var explanations = new List<MatchExplanation>();

        foreach (var rule in rules)
        {
            string? candidateValue = null, matchedValue = null;
            bool applicable = true, matched = false;

            switch (rule.Attribute)
            {
                case MatchAttributes.Hostname:
                    candidateValue = normalizedHostname;
                    matchedValue = asset.NormalizedHostname;
                    applicable = !string.IsNullOrEmpty(candidateValue);
                    matched = applicable && candidateValue == matchedValue;
                    break;
                case MatchAttributes.MacAddress:
                    candidateValue = string.Join(",", macs);
                    var assetMacs = asset.IpAddresses
                        .Where(i => i.IsActive && i.LastSeen >= networkEvidenceCutoff && i.MacAddress != null)
                        .Select(i => i.MacAddress!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    matchedValue = string.Join(",", assetMacs);
                    applicable = macs.Count > 0 && assetMacs.Count > 0;
                    matched = applicable && macs.Any(assetMacs.Contains);
                    break;
                case MatchAttributes.IpAddress:
                    candidateValue = string.Join(",", ips);
                    var assetIps = asset.IpAddresses
                        .Where(i => i.IsActive && i.LastSeen >= networkEvidenceCutoff)
                        .Select(i => i.IpAddress)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    matchedValue = string.Join(",", assetIps);
                    applicable = ips.Count > 0 && assetIps.Count > 0;
                    matched = applicable && ips.Any(assetIps.Contains);
                    break;
                case MatchAttributes.OperatingSystem:
                    candidateValue = candidate.OperatingSystem;
                    matchedValue = asset.OperatingSystem;
                    applicable = candidateValue != null && matchedValue != null;
                    matched = applicable && string.Equals(candidateValue, matchedValue, StringComparison.OrdinalIgnoreCase);
                    break;
                case MatchAttributes.Domain:
                    candidateValue = candidate.Domain;
                    matchedValue = asset.Domain;
                    applicable = candidateValue != null && matchedValue != null;
                    matched = applicable && string.Equals(candidateValue, matchedValue, StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    applicable = candidate.Identifiers.TryGetValue(rule.Attribute, out var idValue);
                    candidateValue = applicable ? idValue : null;
                    matched = false;
                    break;
            }

            if (!applicable) continue;
            if (rule.Weight > 0) total += rule.Weight;
            var contribution = matched ? rule.Weight : 0;
            achieved += contribution;
            explanations.Add(new MatchExplanation(rule.Name, rule.Attribute, candidateValue, matchedValue,
                rule.Weight, contribution, matched));
        }

        var score = total == 0 ? 0 : achieved / total;
        return (score, explanations);
    }

    private async Task<List<MatchingRule>> GetRulesAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<MatchingRule>>(CacheKeys.MatchingRules, ct);
        if (cached is { Count: > 0 }) return cached;
        var rules = await _uow.MatchingRules.ListAsync(r => r.Enabled, ct);
        await _cache.SetAsync(CacheKeys.MatchingRules, rules, TimeSpan.FromMinutes(5), ct);
        return rules;
    }

    private static bool IsPrivateAddress(string? value)
    {
        if (!System.Net.IPAddress.TryParse(value, out var address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127 ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        return address.IsIPv6UniqueLocal;
    }

    private async Task<MatchingOptions> GetOptionsAsync(CancellationToken ct)
    {
        var options = new MatchingOptions();
        var settings = await _uow.Settings.ListAsync(
            s => s.Key == SettingKeys.MatchAutoMergeThreshold ||
                 s.Key == SettingKeys.MatchReviewThreshold ||
                 s.Key == SettingKeys.MatchAmbiguityDelta ||
                 s.Key == SettingKeys.MatchNetworkEvidenceMaxAgeDays, ct);
        foreach (var s in settings)
        {
            if (s.Key == SettingKeys.MatchNetworkEvidenceMaxAgeDays &&
                int.TryParse(s.Value, out var days) && days > 0)
            {
                options.NetworkEvidenceMaxAgeDays = days;
                continue;
            }
            if (!decimal.TryParse(s.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) continue;
            if (s.Key == SettingKeys.MatchAutoMergeThreshold) options.AutoMergeThreshold = v;
            if (s.Key == SettingKeys.MatchReviewThreshold) options.ReviewThreshold = v;
            if (s.Key == SettingKeys.MatchAmbiguityDelta) options.AmbiguityDelta = v;
        }
        return options;
    }
}

public static class CacheKeys
{
    public const string MatchingRules = "esar:matching:rules";
    public const string SourcePriorities = "esar:source-priorities";
    public const string DashboardSummary = "esar:dashboard:summary";
}

public static class SettingKeys
{
    public const string MatchAutoMergeThreshold = "matching.autoMergeThreshold";
    public const string MatchReviewThreshold = "matching.reviewThreshold";
    public const string MatchAmbiguityDelta = "matching.ambiguityDelta";
    public const string MatchNetworkEvidenceMaxAgeDays = "matching.networkEvidenceMaxAgeDays";
    public const string StaleAssetDays = "lifecycle.staleAssetDays";
    public const string DecommissionAfterDays = "lifecycle.decommissionAfterDays";
    public const string ComplianceEvidenceMaxAgeDays = "compliance.evidenceMaxAgeDays";
    public const string ApprovalRequireForNewAssets = "approval.requireForNewAssets";
    public const string AuthFederatedAutoProvision = "auth.federated.autoProvision";
    public const string AuthEntraTenantId = "auth.entra.tenantId";
    public const string AuthEntraClientId = "auth.entra.clientId";
    public const string SecurityPasswordMinLength = "security.password.minLength";
    public const string SecurityLoginMaxFailedAttempts = "security.login.maxFailedAttempts";
    public const string SecurityLoginLockoutMinutes = "security.login.lockoutMinutes";
    public const string SecuritySessionTokenLifetimeMinutes = "security.session.tokenLifetimeMinutes";
    public const string SecuritySessionIdleTimeoutMinutes = "security.session.idleTimeoutMinutes";
    public const string SecurityAuditRetentionDays = "security.audit.retentionDays";
}
