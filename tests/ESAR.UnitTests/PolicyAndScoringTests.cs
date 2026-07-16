using System.Linq.Expressions;
using Esar.Application.Abstractions;
using Esar.Application.Compliance;
using Esar.Application.Scoring;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Esar.UnitTests;

public class PolicyEngineTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IRepository<CompliancePolicy>> _policies = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly PolicyEngine _sut;

    public PolicyEngineTests()
    {
        _uow.SetupGet(u => u.CompliancePolicies).Returns(_policies.Object);
        _cache.Setup(c => c.GetAsync<List<CompliancePolicy>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<CompliancePolicy>?)null);
        _policies.Setup(p => p.ListAsync(It.IsAny<Expression<Func<CompliancePolicy, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CompliancePolicy>
            {
                new()
                {
                    Name = "Server Baseline", Priority = 10,
                    AppliesToAssetTypesJson = """["WindowsServer","LinuxServer"]""",
                    RequiredControlsJson = """["SiemLogSource","Edr","VulnerabilityScanner"]""",
                    MandatoryControlsJson = """["SiemLogSource"]"""
                },
                new()
                {
                    Name = "Network Baseline", Priority = 20,
                    AppliesToAssetTypesJson = """["Firewall","Switch","Router","NetworkDevice"]""",
                    RequiredControlsJson = """["SiemLogSource","BackupAgent"]""",
                    MandatoryControlsJson = """["SiemLogSource"]"""
                }
            });
        _sut = new PolicyEngine(_uow.Object, _cache.Object, NullLogger<PolicyEngine>.Instance);
    }

    [Fact]
    public async Task Asset_type_selects_the_matching_policy()
    {
        var server = new Asset { AssetType = AssetType.WindowsServer };
        var plan = await _sut.GetPlanAsync(server);
        plan.PolicyName.Should().Be("Server Baseline");
        plan.RequiredControls.Should().BeEquivalentTo(new[]
            { ControlType.SiemLogSource, ControlType.Edr, ControlType.VulnerabilityScanner });
        plan.MandatoryControls.Should().ContainSingle().Which.Should().Be(ControlType.SiemLogSource);

        var firewall = new Asset { AssetType = AssetType.Firewall };
        (await _sut.GetPlanAsync(firewall)).PolicyName.Should().Be("Network Baseline");
    }

    [Fact]
    public async Task Unmatched_asset_falls_back_to_default_baseline()
    {
        var app = new Asset { AssetType = AssetType.Application };
        var plan = await _sut.GetPlanAsync(app);
        plan.PolicyId.Should().BeNull();
        plan.RequiredControls.Should().Contain(ControlType.AssetClassification);
    }
}

public class ScoringEngineTests
{
    [Fact]
    public void Data_quality_penalizes_missing_metadata()
    {
        var engine = new DataQualityEngine();
        var complete = new Asset
        {
            Hostname = "srv-full", NormalizedHostname = "srv-full",
            OwnerName = "Owner", BusinessUnit = "IT", Criticality = CriticalityLevel.High,
            Environment = EnvironmentType.Production, Classification = "Internal",
            OperatingSystem = "Windows Server 2022", AssetType = AssetType.WindowsServer,
            LastSeen = DateTime.UtcNow,
            Sources =
            {
                new AssetSource { ConnectorType = ConnectorType.Azure, ExternalId = "a", SourceHostname = "srv-full" },
                new AssetSource { ConnectorType = ConnectorType.MicrosoftDefender, ExternalId = "b", SourceHostname = "srv-full" }
            },
            IpAddresses = { new AssetIp { IpAddress = "10.0.0.1" } }
        };
        engine.Evaluate(complete);
        complete.DataQualityScore.Should().Be(100);

        var empty = new Asset { Hostname = "x", NormalizedHostname = "x", LastSeen = DateTime.UtcNow };
        var issues = engine.Evaluate(empty);
        empty.DataQualityScore.Should().BeLessThan(50);
        issues.Should().Contain(i => i.Code == "MISSING_OWNER");
        issues.Should().Contain(i => i.Code == "NO_IP_ADDRESS");
    }

    [Fact]
    public void Health_engine_reflects_stale_telemetry_and_missing_edr()
    {
        var engine = new AssetHealthEngine();
        var stale = new Asset { LastSeen = DateTime.UtcNow.AddDays(-45) };
        engine.Evaluate(stale);
        stale.HealthScore.Should().BeLessThan(30);

        var healthy = new Asset
        {
            LastSeen = DateTime.UtcNow,
            ComplianceStatus = ComplianceStatus.Compliant,
            Sources =
            {
                new AssetSource { ConnectorType = ConnectorType.MicrosoftDefender, ExternalId = "d",
                    LastSeen = DateTime.UtcNow },
                new AssetSource { ConnectorType = ConnectorType.Azure, ExternalId = "a", LastSeen = DateTime.UtcNow }
            },
            Tags =
            {
                new AssetTag { Key = "monitoring_agent", Value = "true" },
                new AssetTag { Key = "backup_agent", Value = "true" }
            }
        };
        engine.Evaluate(healthy);
        healthy.HealthScore.Should().Be(100);
    }

    [Fact]
    public void Risk_engine_raises_score_for_exposed_noncompliant_critical_assets()
    {
        var engine = new RiskScoringEngine();
        var lowRisk = new Asset
        {
            Criticality = CriticalityLevel.Low, ComplianceStatus = ComplianceStatus.Compliant
        };
        var highRisk = new Asset
        {
            Criticality = CriticalityLevel.Critical,
            ComplianceStatus = ComplianceStatus.NonCompliant,
            Environment = EnvironmentType.Production,
            Classification = "Confidential",
            Tags = { new AssetTag { Key = "internet_facing", Value = "true" } },
            Risk = new AssetRisk { VulnerabilitiesCritical = 3, VulnerabilitiesHigh = 5 }
        };

        var low = engine.Evaluate(lowRisk);
        var high = engine.Evaluate(highRisk);

        high.Should().BeGreaterThan(low + 40);
        highRisk.Risk!.ExposureScore.Should().BeGreaterThan(0);
        highRisk.Risk.RiskScore.Should().Be(high);
    }
}
