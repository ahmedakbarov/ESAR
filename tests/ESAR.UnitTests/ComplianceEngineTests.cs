using System.Linq.Expressions;
using Esar.Application.Abstractions;
using Esar.Application.Compliance;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Esar.UnitTests;

public class ComplianceEngineTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IAssetRepository> _assets = new();
    private readonly Mock<IRepository<Setting>> _settings = new();
    private readonly Mock<IEventBus> _events = new();
    private readonly ComplianceEngine _sut;

    public ComplianceEngineTests()
    {
        _uow.SetupGet(u => u.Assets).Returns(_assets.Object);
        _uow.SetupGet(u => u.Settings).Returns(_settings.Object);
        _settings.Setup(s => s.FirstOrDefaultAsync(It.IsAny<Expression<Func<Setting, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Setting?)null);
        _sut = new ComplianceEngine(_uow.Object, _events.Object, NullLogger<ComplianceEngine>.Instance);
    }

    [Fact]
    public async Task Fully_covered_asset_is_compliant_on_mandatory_controls()
    {
        var asset = new Asset
        {
            Hostname = "srv-ok",
            Classification = "Internal",
            Sources =
            {
                new AssetSource { ConnectorType = ConnectorType.MicrosoftSentinel, ExternalId = "s1", LastSeen = DateTime.UtcNow },
                new AssetSource { ConnectorType = ConnectorType.MicrosoftDefender, ExternalId = "d1", LastSeen = DateTime.UtcNow },
                new AssetSource { ConnectorType = ConnectorType.Qualys, ExternalId = "q1", LastSeen = DateTime.UtcNow }
            },
            Tags =
            {
                new AssetTag { Key = "disk_encryption", Value = "true", Source = ConnectorType.Intune },
                new AssetTag { Key = "patch_status", Value = "up_to_date", Source = ConnectorType.Sccm }
            }
        };

        await _sut.EvaluateAsync(asset);

        asset.ComplianceRecords.Should().Contain(c =>
            c.Control == ControlType.SiemLogSource && c.Status == ComplianceStatus.Compliant);
        asset.ComplianceRecords.Should().Contain(c =>
            c.Control == ControlType.Edr && c.Status == ComplianceStatus.Compliant);
        asset.ComplianceRecords.Should().Contain(c =>
            c.Control == ControlType.VulnerabilityScanner && c.Status == ComplianceStatus.Compliant);
        asset.ComplianceScore.Should().Be(100m);
        asset.ComplianceStatus.Should().Be(ComplianceStatus.Compliant);
    }

    [Fact]
    public async Task Missing_siem_marks_asset_noncompliant()
    {
        var asset = new Asset
        {
            Hostname = "srv-nosiem",
            Classification = "Internal",
            Sources =
            {
                new AssetSource { ConnectorType = ConnectorType.MicrosoftDefender, ExternalId = "d1", LastSeen = DateTime.UtcNow },
                new AssetSource { ConnectorType = ConnectorType.Tenable, ExternalId = "t1", LastSeen = DateTime.UtcNow }
            }
        };

        var status = await _sut.EvaluateAsync(asset);

        status.Should().Be(ComplianceStatus.NonCompliant);
        asset.ComplianceRecords.Should().Contain(c =>
            c.Control == ControlType.SiemLogSource && c.Status == ComplianceStatus.NonCompliant);
    }

    [Fact]
    public async Task Stale_evidence_older_than_window_is_noncompliant()
    {
        var asset = new Asset
        {
            Hostname = "srv-stale",
            Sources =
            {
                new AssetSource
                {
                    ConnectorType = ConnectorType.Splunk, ExternalId = "s1",
                    LastSeen = DateTime.UtcNow.AddDays(-30)
                }
            }
        };

        await _sut.EvaluateAsync(asset);

        asset.ComplianceRecords.Should().Contain(c =>
            c.Control == ControlType.SiemLogSource && c.Status == ComplianceStatus.NonCompliant &&
            c.Details!.Contains("stale"));
    }
}
