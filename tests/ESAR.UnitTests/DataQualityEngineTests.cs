using Esar.Application.Scoring;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Esar.UnitTests;

/// <summary>
/// Locks down the data-quality score formula: 100 minus the sum of issue penalties,
/// clamped to 0–100, with source-corroboration checks counting only fresh sources.
/// </summary>
public class DataQualityEngineTests
{
    private readonly DataQualityEngine _sut = new();

    private static Asset CompleteAsset() => new()
    {
        Hostname = "srv-complete",
        NormalizedHostname = "srv-complete",
        OwnerName = "Owner",
        BusinessUnit = "IT",
        Criticality = CriticalityLevel.High,
        Environment = EnvironmentType.Production,
        Classification = "Internal",
        OperatingSystem = "Windows Server 2022",
        AssetType = AssetType.WindowsServer,
        LastSeen = DateTime.UtcNow,
        Sources =
        {
            new AssetSource { ConnectorType = ConnectorType.Azure, ExternalId = "a",
                SourceHostname = "srv-complete", LastSeen = DateTime.UtcNow },
            new AssetSource { ConnectorType = ConnectorType.MicrosoftDefender, ExternalId = "b",
                SourceHostname = "srv-complete", LastSeen = DateTime.UtcNow }
        },
        IpAddresses = { new AssetIp { IpAddress = "10.0.0.1" } }
    };

    [Fact]
    public void Complete_asset_scores_100_with_no_issues()
    {
        var asset = CompleteAsset();
        var issues = _sut.Evaluate(asset);
        issues.Should().BeEmpty();
        asset.DataQualityScore.Should().Be(100);
    }

    [Theory]
    [InlineData("MISSING_OWNER", 12)]
    [InlineData("MISSING_BUSINESS_UNIT", 10)]
    [InlineData("MISSING_CRITICALITY", 12)]
    [InlineData("MISSING_ENVIRONMENT", 8)]
    [InlineData("MISSING_CLASSIFICATION", 8)]
    [InlineData("MISSING_OS", 8)]
    [InlineData("MISSING_ASSET_TYPE", 8)]
    [InlineData("NO_IP_ADDRESS", 8)]
    [InlineData("STALE_TELEMETRY", 12)]
    public void Each_defect_costs_exactly_its_documented_penalty(string code, int penalty)
    {
        var asset = CompleteAsset();
        switch (code)
        {
            case "MISSING_OWNER": asset.OwnerName = null; break;
            case "MISSING_BUSINESS_UNIT": asset.BusinessUnit = null; break;
            case "MISSING_CRITICALITY": asset.Criticality = CriticalityLevel.Unknown; break;
            case "MISSING_ENVIRONMENT": asset.Environment = EnvironmentType.Unknown; break;
            case "MISSING_CLASSIFICATION": asset.Classification = null; break;
            case "MISSING_OS": asset.OperatingSystem = null; break;
            case "MISSING_ASSET_TYPE": asset.AssetType = AssetType.Unknown; break;
            case "NO_IP_ADDRESS": asset.IpAddresses.Clear(); break;
            case "STALE_TELEMETRY": asset.LastSeen = DateTime.UtcNow.AddDays(-45); break;
        }

        var issues = _sut.Evaluate(asset);

        issues.Should().ContainSingle().Which.Code.Should().Be(code);
        asset.DataQualityScore.Should().Be(100 - penalty);
    }

    [Fact]
    public void Blank_hostname_is_reported_as_missing_not_invalid()
    {
        var asset = CompleteAsset();
        asset.Hostname = "";
        asset.NormalizedHostname = "";

        var issues = _sut.Evaluate(asset);

        issues.Should().ContainSingle(i => i.Code == "MISSING_HOSTNAME");
        issues.Should().NotContain(i => i.Code == "INVALID_HOSTNAME");
    }

    [Fact]
    public void Malformed_hostname_violates_naming_rules()
    {
        var asset = CompleteAsset();
        asset.NormalizedHostname = "-bad-name-";

        _sut.Evaluate(asset).Should().ContainSingle(i => i.Code == "INVALID_HOSTNAME");
    }

    [Fact]
    public void Stale_source_hostname_conflict_is_not_penalized()
    {
        // The asset was renamed; a connector that stopped reporting months ago still stores
        // the old name. That stale echo must not depress the score forever.
        var asset = CompleteAsset();
        asset.Sources.Add(new AssetSource
        {
            ConnectorType = ConnectorType.ServiceNowCmdb,
            ExternalId = "c",
            SourceHostname = "srv-oldname",
            LastSeen = DateTime.UtcNow.AddDays(-90)
        });

        var issues = _sut.Evaluate(asset);

        issues.Should().NotContain(i => i.Code == "CONFLICTING_HOSTNAME");
        asset.DataQualityScore.Should().Be(100);
    }

    [Fact]
    public void Fresh_source_hostname_conflict_is_penalized()
    {
        var asset = CompleteAsset();
        asset.Sources.Add(new AssetSource
        {
            ConnectorType = ConnectorType.ServiceNowCmdb,
            ExternalId = "c",
            SourceHostname = "srv-different",
            LastSeen = DateTime.UtcNow
        });

        _sut.Evaluate(asset).Should().ContainSingle(i => i.Code == "CONFLICTING_HOSTNAME");
    }

    [Fact]
    public void Only_fresh_sources_count_as_corroboration()
    {
        // Two sources on record, but one went silent long ago — effectively single-sourced.
        var asset = CompleteAsset();
        asset.Sources.First(s => s.ConnectorType == ConnectorType.MicrosoftDefender).LastSeen =
            DateTime.UtcNow.AddDays(-90);

        var issues = _sut.Evaluate(asset);

        issues.Should().ContainSingle(i => i.Code == "SINGLE_SOURCE");
        asset.DataQualityScore.Should().Be(95);
    }

    [Fact]
    public void Score_never_drops_below_zero()
    {
        var asset = new Asset
        {
            Hostname = "",
            NormalizedHostname = "",
            LastSeen = DateTime.UtcNow.AddDays(-120)
        };

        _sut.Evaluate(asset);

        asset.DataQualityScore.Should().Be(0);
        // Sanity: the raw penalty sum genuinely exceeds 100, proving the clamp engaged.
        // owner 12 + BU 10 + crit 12 + env 8 + class 8 + os 8 + type 8 + hostname 10 +
        // no-ip 8 + stale 12 + single-source 5 = 101.
    }

    [Fact]
    public void Issues_json_is_written_alongside_the_score()
    {
        var asset = CompleteAsset();
        asset.OwnerName = null;

        _sut.Evaluate(asset);

        asset.DataQualityIssuesJson.Should().Contain("MISSING_OWNER");
    }
}
