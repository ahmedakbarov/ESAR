using System.Linq.Expressions;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Application.Matching;
using Esar.Application.Normalization;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using MatchType = Esar.Domain.Enums.MatchType;
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
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(MatchAttributes.SerialNumber, "SN12345",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

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
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
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
    public async Task Weak_ip_only_positive_evidence_below_review_threshold_creates_new_asset()
    {
        var existing = new Asset
        {
            Hostname = "other-host",
            NormalizedHostname = "other-host",
            OperatingSystem = "Ubuntu Linux",
            IpAddresses = { new AssetIp { IpAddress = "10.1.1.5", MacAddress = "aa:bb:cc:dd:ee:ff" } }
        };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
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

        // Only IP matches and the conflicting applicable evidence keeps the score below review threshold.
        result.Decision.Should().Be(MatchDecision.NewAsset);
        result.MatchedAsset.Should().BeNull();
    }

    [Fact]
    public async Task Ip_only_match_is_queued_for_review_even_when_it_scores_as_an_auto_merge()
    {
        var existing = new Asset
        {
            IpAddresses = { new AssetIp { IpAddress = "10.1.1.5" } }
        };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var candidate = new DiscoveredAsset
        {
            Source = ConnectorType.Tenable,
            ExternalId = "t-2",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.1.1.5" } }
        };

        var result = await _sut.MatchAsync(candidate);

        result.ConfidenceScore.Should().Be(1m);
        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.MatchedAsset.Should().BeSameAs(existing);
        result.Explanations.Where(e => e.Matched).Should().ContainSingle(e => e.Attribute == MatchAttributes.IpAddress);
    }

    [Fact]
    public async Task Ip_and_mac_below_absolute_threshold_is_queued_for_review()
    {
        var existing = new Asset
        {
            IpAddresses = { new AssetIp { IpAddress = "10.1.1.5", MacAddress = "00:1a:2b:3c:4d:5e" } }
        };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var candidate = new DiscoveredAsset
        {
            Source = ConnectorType.Tenable,
            ExternalId = "t-3",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.1.1.5", MacAddress = "00:1a:2b:3c:4d:5e" } }
        };

        var result = await _sut.MatchAsync(candidate);

        result.ConfidenceScore.Should().Be(1m);
        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.MatchedAsset.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task Ip_and_hostname_without_mac_is_queued_for_review()
    {
        var existing = new Asset
        {
            Hostname = "srv-web01",
            NormalizedHostname = "srv-web01",
            IpAddresses = { new AssetIp { IpAddress = "10.1.1.5" } }
        };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var candidate = new DiscoveredAsset
        {
            Source = ConnectorType.Tenable,
            ExternalId = "t-4",
            Hostname = "srv-web01",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.1.1.5" } }
        };

        var result = await _sut.MatchAsync(candidate);

        result.ConfidenceScore.Should().Be(1m);
        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.MatchedAsset.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task Hostname_only_match_is_never_auto_merged()
    {
        var existing = new Asset { Hostname = "shared-name", NormalizedHostname = "shared-name" };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.MicrosoftDefender,
            ExternalId = "endpoint-1",
            Hostname = "SHARED-NAME"
        });

        result.ConfidenceScore.Should().Be(1m);
        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.MatchedAsset.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task Equal_best_candidates_are_queued_as_ambiguous()
    {
        var first = new Asset
        {
            Hostname = "same-host",
            NormalizedHostname = "same-host",
            OperatingSystem = "Windows Server 2022",
            IpAddresses = { new AssetIp { IpAddress = "10.1.2.3", MacAddress = "00:11:22:33:44:55" } }
        };
        var second = new Asset
        {
            Hostname = "same-host",
            NormalizedHostname = "same-host",
            OperatingSystem = "Windows Server 2022",
            IpAddresses = { new AssetIp { IpAddress = "10.1.2.3", MacAddress = "00:11:22:33:44:55" } }
        };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { first, second });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.MicrosoftDefender,
            ExternalId = "endpoint-2",
            Hostname = "same-host",
            OperatingSystem = "Windows Server 2022",
            Interfaces =
            {
                new DiscoveredInterface { IpAddress = "10.1.2.3", MacAddress = "00:11:22:33:44:55" }
            }
        });

        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.Explanations.Should().Contain(explanation =>
            explanation.Rule == "Ambiguous candidate safety policy");
    }

    [Fact]
    public async Task Stale_network_observation_is_not_used_as_match_evidence()
    {
        var existing = new Asset
        {
            Hostname = "old-host",
            NormalizedHostname = "old-host",
            IpAddresses =
            {
                new AssetIp
                {
                    IpAddress = "10.8.8.8",
                    MacAddress = "00:11:22:33:44:55",
                    LastSeen = DateTime.UtcNow.AddDays(-45)
                }
            }
        };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());
        _assets.Setup(a => a.FindSoftCandidatesAsync(It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Tenable,
            ExternalId = "scanner-1",
            Hostname = "new-host",
            Interfaces =
            {
                new DiscoveredInterface { IpAddress = "10.8.8.8", MacAddress = "00:11:22:33:44:55" }
            }
        });

        result.Decision.Should().Be(MatchDecision.NewAsset);
        result.MatchedAsset.Should().BeNull();
    }

    [Fact]
    public async Task Hard_match_to_decommissioned_asset_requires_review()
    {
        var existing = new Asset
        {
            Hostname = "retired-host",
            SerialNumber = "SERIAL-1",
            Status = AssetStatus.Decommissioned
        };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(MatchAttributes.SerialNumber, "SERIAL-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { existing });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Qualys,
            ExternalId = "qualys-1",
            Identifiers = { [MatchAttributes.SerialNumber] = "SERIAL-1" }
        });

        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.MatchedAsset.Should().BeSameAs(existing);
    }

    [Fact]
    public async Task Duplicate_hard_identifier_requires_review_instead_of_arbitrary_merge()
    {
        var first = new Asset { Hostname = "server-a", SerialNumber = "DUPLICATE-SERIAL" };
        var second = new Asset { Hostname = "server-b", SerialNumber = "DUPLICATE-SERIAL" };
        _assets.Setup(a => a.FindHardIdentifierCandidatesAsync(
                MatchAttributes.SerialNumber, "DUPLICATE-SERIAL", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { first, second });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Qualys,
            ExternalId = "qualys-duplicate",
            Identifiers = { [MatchAttributes.SerialNumber] = "DUPLICATE-SERIAL" }
        });

        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.Explanations.Should().Contain(explanation =>
            explanation.Rule == "Hard identifier conflict safety policy");
    }

    [Fact]
    public async Task Conflicting_hard_identifier_namespaces_require_review()
    {
        _rules.Setup(repository => repository.ListAsync(
                It.IsAny<Expression<Func<MatchingRule, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MatchingRule>
            {
                new() { Name = "BIOS", Attribute = MatchAttributes.BiosUuid, MatchType = MatchType.Hard, Weight = 1m, Order = 10 },
                new() { Name = "Serial", Attribute = MatchAttributes.SerialNumber, MatchType = MatchType.Hard, Weight = 1m, Order = 20 }
            });
        var byBios = new Asset { Hostname = "asset-a" };
        var bySerial = new Asset { Hostname = "asset-b" };
        _assets.Setup(repository => repository.FindHardIdentifierCandidatesAsync(
                MatchAttributes.BiosUuid, "bios-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { byBios });
        _assets.Setup(repository => repository.FindHardIdentifierCandidatesAsync(
                MatchAttributes.SerialNumber, "SERIAL-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { bySerial });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Qualys,
            ExternalId = "qualys-2",
            Identifiers =
            {
                [MatchAttributes.BiosUuid] = "bios-1",
                [MatchAttributes.SerialNumber] = "SERIAL-1"
            }
        });

        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.Explanations.Should().Contain(explanation =>
            explanation.Rule == "Cross-identifier convergence safety policy");
    }
}
