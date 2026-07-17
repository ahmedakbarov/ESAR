using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Compliance;

/// <summary>
/// Security-baseline policy resolution: decides which controls an asset must satisfy.
/// Policies are data (CompliancePolicies table) — no hardcoded per-type logic.
/// </summary>
public interface IPolicyEngine
{
    Task<PolicyEvaluationPlan> GetPlanAsync(Asset asset, CancellationToken ct = default);
}

public record PolicyEvaluationPlan(
    Guid? PolicyId,
    string PolicyName,
    IReadOnlyList<ControlType> RequiredControls,
    IReadOnlySet<ControlType> MandatoryControls);

public class PolicyEngine : IPolicyEngine
{
    public const string PoliciesCacheKey = "esar:compliance:policies";

    /// <summary>Fallback baseline when no policy matches an asset.</summary>
    private static readonly ControlType[] DefaultControls = Enum.GetValues<ControlType>();
    private static readonly HashSet<ControlType> DefaultMandatory = new()
        { ControlType.SiemLogSource, ControlType.Edr, ControlType.VulnerabilityScanner };

    private readonly IUnitOfWork _uow;
    private readonly ICacheService _cache;
    private readonly ILogger<PolicyEngine> _logger;

    public PolicyEngine(IUnitOfWork uow, ICacheService cache, ILogger<PolicyEngine> logger)
    {
        _uow = uow;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PolicyEvaluationPlan> GetPlanAsync(Asset asset, CancellationToken ct = default)
    {
        var policies = await GetPoliciesAsync(ct);
        var match = policies.OrderBy(p => p.Priority).FirstOrDefault(p => Applies(p, asset));
        if (match is null)
            return new PolicyEvaluationPlan(null, "Default baseline", DefaultControls, DefaultMandatory);

        var required = ParseControls(match.RequiredControlsJson);
        var mandatory = ParseControls(match.MandatoryControlsJson).ToHashSet();
        if (required.Count == 0)
        {
            _logger.LogWarning("Policy {Policy} has no required controls — falling back to default baseline",
                match.Name);
            return new PolicyEvaluationPlan(match.Id, match.Name, DefaultControls, DefaultMandatory);
        }
        return new PolicyEvaluationPlan(match.Id, match.Name, required, mandatory);
    }

    private static bool Applies(CompliancePolicy policy, Asset asset)
    {
        var types = ParseStrings(policy.AppliesToAssetTypesJson);
        if (types.Count > 0 && !types.Contains(asset.AssetType.ToString(), StringComparer.OrdinalIgnoreCase))
            return false;
        var environments = ParseStrings(policy.AppliesToEnvironmentsJson);
        if (environments.Count > 0 &&
            !environments.Contains(asset.Environment.ToString(), StringComparer.OrdinalIgnoreCase))
            return false;
        if (policy.MinCriticality is { } min && asset.Criticality < min) return false;
        return true;
    }

    private async Task<List<CompliancePolicy>> GetPoliciesAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<CompliancePolicy>>(PoliciesCacheKey, ct);
        if (cached is { Count: > 0 }) return cached;
        var policies = await _uow.CompliancePolicies.ListAsync(p => p.Enabled, ct);
        await _cache.SetAsync(PoliciesCacheKey, policies, TimeSpan.FromMinutes(5), ct);
        return policies;
    }

    private static List<string> ParseStrings(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new List<string>(); }
    }

    private static List<ControlType> ParseControls(string json)
        => ParseStrings(json)
            .Select(s => Enum.TryParse<ControlType>(s, true, out var c) ? c : (ControlType?)null)
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToList();
}
