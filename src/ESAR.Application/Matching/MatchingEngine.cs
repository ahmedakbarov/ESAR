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
        var rules = await GetRulesAsync(ct);
        var options = await GetOptionsAsync(ct);
        var result = new MatchResult();

        // 1. Hard rules in priority order — a single hit is a definitive match.
        foreach (var rule in rules.Where(r => r.MatchType == MatchType.Hard).OrderBy(r => r.Order))
        {
            if (!candidate.Identifiers.TryGetValue(rule.Attribute, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            var asset = await _uow.Assets.FindByHardIdentifierAsync(rule.Attribute, value, ct);
            if (asset is null) continue;

            result.Decision = MatchDecision.AutoMerged;
            result.MatchedAsset = asset;
            result.ConfidenceScore = 1.0m;
            result.MatchType = MatchType.Hard;
            result.Explanations.Add(new MatchExplanation(rule.Name, rule.Attribute, value, value, 1.0m, 1.0m, true));
            return result;
        }

        // 2. Soft weighted scoring over candidate golden records.
        var softRules = rules.Where(r => r.MatchType == MatchType.Soft).OrderBy(r => r.Order).ToList();
        if (softRules.Count == 0) return result;

        var macs = candidate.Interfaces.Where(i => i.MacAddress != null).Select(i => i.MacAddress!).ToList();
        var ips = candidate.Interfaces.Where(i => i.IpAddress != null).Select(i => i.IpAddress!).ToList();
        var normalizedHostname = _normalization.NormalizeHostname(candidate.Hostname);

        var candidates = await _uow.Assets.FindSoftCandidatesAsync(
            string.IsNullOrEmpty(normalizedHostname) ? null : normalizedHostname, macs, ips, ct);
        if (candidates.Count == 0) return result;

        Asset? best = null;
        decimal bestScore = 0;
        List<MatchExplanation> bestExplanations = new();

        foreach (var asset in candidates)
        {
            var (score, explanations) = ScoreCandidate(candidate, asset, softRules, normalizedHostname, macs, ips);
            if (score > bestScore)
            {
                bestScore = score;
                best = asset;
                bestExplanations = explanations;
            }
        }

        if (best is null) return result;

        result.MatchedAsset = best;
        result.ConfidenceScore = Math.Round(bestScore, 4);
        result.MatchType = MatchType.Soft;
        result.Explanations = bestExplanations;

        // An IP address is commonly reassigned, so it cannot be the sole positive
        // soft-match signal for an automatic merge. Keep the candidate linked to
        // the proposed asset for an analyst to review instead.
        var hasIpOnlyPositiveEvidence = bestExplanations.Any(e =>
            e.Matched && string.Equals(e.Attribute, MatchAttributes.IpAddress, StringComparison.OrdinalIgnoreCase)) &&
            bestExplanations.Where(e => e.Matched).All(e =>
                string.Equals(e.Attribute, MatchAttributes.IpAddress, StringComparison.OrdinalIgnoreCase));

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
        var existingMacs = best.IpAddresses.Where(networkInterface => !string.IsNullOrWhiteSpace(networkInterface.MacAddress))
            .Select(networkInterface => networkInterface.MacAddress!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var macConflict = candidateHasMac && existingMacs.Count > 0 && !candidateMacs.Overlaps(existingMacs);
        var hasStrongAzureAdNetworkIdentity = hostnameMatches &&
            (macMatches || (ipMatches && !macConflict));

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
        else result.Decision = hasIpOnlyPositiveEvidence ? MatchDecision.QueuedForReview
            : bestScore >= options.AutoMergeThreshold ? MatchDecision.AutoMerged
            : bestScore >= options.ReviewThreshold ? MatchDecision.QueuedForReview
            : MatchDecision.NewAsset;

        if (result.Decision == MatchDecision.NewAsset) result.MatchedAsset = null;
        return result;
    }

    private static (decimal Score, List<MatchExplanation>) ScoreCandidate(DiscoveredAsset candidate, Asset asset,
        List<MatchingRule> rules, string normalizedHostname, List<string> macs, List<string> ips)
    {
        decimal total = 0, achieved = 0;
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
                    var assetMacs = asset.IpAddresses.Where(i => i.MacAddress != null).Select(i => i.MacAddress!).ToHashSet();
                    matchedValue = string.Join(",", assetMacs);
                    applicable = macs.Count > 0 && assetMacs.Count > 0;
                    matched = applicable && macs.Any(assetMacs.Contains);
                    break;
                case MatchAttributes.IpAddress:
                    candidateValue = string.Join(",", ips);
                    var assetIps = asset.IpAddresses.Select(i => i.IpAddress).ToHashSet();
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
            total += rule.Weight;
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

    private async Task<MatchingOptions> GetOptionsAsync(CancellationToken ct)
    {
        var options = new MatchingOptions();
        var settings = await _uow.Settings.ListAsync(
            s => s.Key == SettingKeys.MatchAutoMergeThreshold || s.Key == SettingKeys.MatchReviewThreshold, ct);
        foreach (var s in settings)
        {
            if (!decimal.TryParse(s.Value, out var v)) continue;
            if (s.Key == SettingKeys.MatchAutoMergeThreshold) options.AutoMergeThreshold = v;
            if (s.Key == SettingKeys.MatchReviewThreshold) options.ReviewThreshold = v;
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
    public const string StaleAssetDays = "lifecycle.staleAssetDays";
    public const string DecommissionAfterDays = "lifecycle.decommissionAfterDays";
    public const string ComplianceEvidenceMaxAgeDays = "compliance.evidenceMaxAgeDays";
    public const string ApprovalRequireForNewAssets = "approval.requireForNewAssets";
    public const string AuthFederatedAutoProvision = "auth.federated.autoProvision";
    public const string AuthEntraTenantId = "auth.entra.tenantId";
    public const string AuthEntraClientId = "auth.entra.clientId";
}
