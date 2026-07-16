using System.Linq.Expressions;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Application.Matching;
using Esar.Application.Normalization;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Esar.UnitTests;

public class MatchingEngineTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IAssetRepository> _assets = new();
    private readonly Mock<IRepository<MatchingRule>> _rules = new();
    private readonly Mock<IRepository<Setting>> _settings = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly MatchingEngine _sut;

    public MatchingEngineTests()
    {
        _uow.SetupGet(u => u.Assets).Returns(_assets.Object);
        _uow.SetupGet(u => u.MatchingRules).Returns(_rules.Object);
        _uow.SetupGet(u => u.Settings).Returns(_settings.Object);
        _cache.Setup(c => c.GetAsync<List<MatchingRule>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<MatchingRule>?)null);
        _rules.Setup(r => r.ListAsync(It.IsAny<Expression<Func<MatchingRule, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultRules());
        _settings.Setup(s => s.ListAsync(It.IsAny<Expression<Func<Setting, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Setting>());
        _sut = new MatchingEngine(_uow.Object, new NormalizationService(), _cache.Object,
            NullLogger<MatchingEngine>.Instance);
    }

    private static List<MatchingRule> DefaultRules() => new()
    {
        new MatchingRule { Name = "Serial Number", Attribute = MatchAttributes.SerialNumber, MatchType = MatchType.Hard, Weight = 1m, Order = 10 },
        new MatchingRule { Name = "Hostname", Attribute = MatchAttributes.Hostname, MatchType = MatchType.Soft, Weight = 0.40m, Order = 20 },
        new MatchingRule { Name = "MAC", Attribute = MatchAttributes.MacAddress, MatchType = MatchType.Soft, Weight = 0.35m, Order = 30 },
        new MatchingRule { Name = "IP", Attribute = MatchAttributes.IpAddress, MatchType = MatchType.Soft, Weight = 0.15m, Order = 40 },
        new MatchingRule { Name = "OS", Attribute = MatchAttributes.OperatingSystem, MatchType = MatchType.Soft, Weight = 0.05m, Order = 50 }
    };

    [Fact]
    public async Task Hard_identifier_match_returns_automerge_with_full_confidence()
    {
        var existing = new Asset { Hostname = "srv-db01", SerialNumber = "SN12345" };
        _assets.Setup(a => a.FindByHardIdentifierAsync(MatchAttributes.SerialNumber, "SN12345",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var candidate = new DiscoveredAsset
        {
            Source = ConnectorType.Qualys,
            ExternalId = "q-1",
            Hostname = "completely-different-name",
            Identifiers = { [MatchAttributes.SerialNumber] = "SN12345" }
        };

        var result = await _sut.MatchAsync(candidate);

        result.Decision.Should().Be(MatchDecision.AutoMerged);
        result.ConfidenceScore.Should().Be(1.0m);
        result.MatchType.Should().Be(MatchType.Hard);
        result.MatchedAsset.Should().BeSameAs(existing);
        result.Explanations.Should().ContainSingle(e => e.Matched);
    }

    [Fact]
    public async Task Strong_soft_match_hostname_and_mac_automerges()
    {
        var existing = new Asset
        {
            Hostname = "srv-web01",
            NormalizedHostname = "srv-web01",
            OperatingSystem = "Windows Server 2019",
            IpAddresses = { new AssetIp { IpAddress = "10.1.1.5", MacAddress = "00:1a:2b:3c:4d:5e" } }
        };
        _assets.Setup(a => a.FindByHardIdentifierAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var candidate = new DiscoveredAsset
        {
            Source = ConnectorType.MicrosoftDefender,
            ExternalId = "d-1",
            Hostname = "SRV-WEB01",
            OperatingSystem = "Windows Server 2019",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.1.1.5", MacAddress = "00-1A-2B-3C-4D-5E" } }
        };

        var result = await _sut.MatchAsync(candidate);

        // hostname 0.40 + mac 0.35 + ip 0.15 + os 0.05 = 0.95 of 0.95 applicable → 1.0
        result.Decision.Should().Be(MatchDecision.AutoMerged);
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(0.85m);
        result.MatchType.Should().Be(MatchType.Soft);
    }

    [Fact]
    public async Task Weak_overlap_only_ip_creates_new_asset()
    {
        var existing = new Asset
        {
            Hostname = "other-host",
            NormalizedHostname = "other-host",
            OperatingSystem = "Ubuntu Linux",
            IpAddresses = { new AssetIp { IpAddress = "10.1.1.5", MacAddress = "aa:bb:cc:dd:ee:ff" } }
        };
        _assets.Setup(a => a.FindByHardIdentifierAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var candidate = new DiscoveredAsset
        {
            Source = ConnectorType.Tenable,
            ExternalId = "t-1",
            Hostname = "new-host",
            OperatingSystem = "Windows Server 2022",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.1.1.5", MacAddress = "11:22:33:44:55:66" } }
        };

        var result = await _sut.MatchAsync(candidate);

        // Only IP matches: 0.15 / 0.95 ≈ 0.16 → far below review threshold.
        result.Decision.Should().Be(MatchDecision.NewAsset);
        result.MatchedAsset.Should().BeNull();
    }
}
