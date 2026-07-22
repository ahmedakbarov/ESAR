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

    // Compiled once per PolicyEngine instance (PolicyEngine is scoped — one instance per HTTP
    // request or per Hangfire job run), so ComplianceJobs.EvaluateAllAsync's fleet-wide sweep
    // compiles each policy's hostname-glob/CIDR filters once instead of once per asset evaluated.
    private List<(CompliancePolicy Policy, CompiledPolicyScope Scope)>? _compiled;

    public PolicyEngine(IUnitOfWork uow, ICacheService cache, ILogger<PolicyEngine> logger)
    {
        _uow = uow;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PolicyEvaluationPlan> GetPlanAsync(Asset asset, CancellationToken ct = default)
    {
        var compiled = await GetCompiledPoliciesAsync(ct);
        var match = compiled.OrderBy(c => c.Policy.Priority)
            .FirstOrDefault(c => PolicyScopeMatcher.Applies(c.Scope, asset)).Policy;
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

    private async Task<List<(CompliancePolicy Policy, CompiledPolicyScope Scope)>> GetCompiledPoliciesAsync(
        CancellationToken ct)
    {
        if (_compiled is not null) return _compiled;
        var policies = await GetPoliciesAsync(ct);
        _compiled = policies.Select(p => (p, CompiledPolicyScope.From(p))).ToList();
        return _compiled;
    }

    private async Task<List<CompliancePolicy>> GetPoliciesAsync(CancellationToken ct)
    {
        var cached = await _cache.GetAsync<List<CompliancePolicy>>(PoliciesCacheKey, ct);
        if (cached is { Count: > 0 }) return cached;
        var policies = await _uow.CompliancePolicies.ListAsync(p => p.Enabled, ct);
        await _cache.SetAsync(PoliciesCacheKey, policies, TimeSpan.FromMinutes(5), ct);
        return policies;
    }

    private static List<ControlType> ParseControls(string json)
        => PolicyScopeMatcher.ParseStrings(json)
            .Select(s => Enum.TryParse<ControlType>(s, true, out var c) ? c : (ControlType?)null)
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .ToList();
}
