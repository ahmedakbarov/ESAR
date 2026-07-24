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
using MatchType = Esar.Domain.Enums.MatchType;

namespace Esar.UnitTests;

public class AzureActiveDirectoryCorrelationTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IAssetRepository> _assets = new();
    private readonly Mock<IRepository<MatchingRule>> _rules = new();
    private readonly Mock<IRepository<Setting>> _settings = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly MatchingEngine _sut;

    public AzureActiveDirectoryCorrelationTests()
    {
        _uow.SetupGet(unit => unit.Assets).Returns(_assets.Object);
        _uow.SetupGet(unit => unit.MatchingRules).Returns(_rules.Object);
        _uow.SetupGet(unit => unit.Settings).Returns(_settings.Object);
        _cache.Setup(cache => cache.GetAsync<List<MatchingRule>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<MatchingRule>?)null);
        _cache.Setup(cache => cache.SetAsync(It.IsAny<string>(), It.IsAny<List<MatchingRule>>(),
                It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _rules.Setup(repository => repository.ListAsync(It.IsAny<Expression<Func<MatchingRule, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(NetworkRules());
        _settings.Setup(repository => repository.ListAsync(It.IsAny<Expression<Func<Setting, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Setting>());
        _assets.Setup(repository => repository.FindSoftCandidatesAsync(It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset>());

        _sut = new MatchingEngine(_uow.Object, new NormalizationService(), _cache.Object,
            NullLogger<MatchingEngine>.Instance);
    }

    [Fact]
    public async Task Azure_and_active_directory_hostname_plus_ip_auto_merge()
    {
        var activeDirectoryAsset = ActiveDirectoryAsset("vm-app-01", "10.90.0.10");
        _assets.Setup(repository => repository.FindSoftCandidatesAsync(It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { activeDirectoryAsset });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-app-01",
            Hostname = "VM-APP-01",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.90.0.10", MacAddress = "00-aa-bb-cc-dd-ee" } }
        });

        result.Decision.Should().Be(MatchDecision.AutoMerged);
        result.MatchedAsset.Should().BeSameAs(activeDirectoryAsset);
    }

    [Fact]
    public async Task Azure_and_active_directory_hostname_plus_mac_auto_merge_when_ips_differ()
    {
        var activeDirectoryAsset = ActiveDirectoryAsset("vm-app-mac-01", "10.90.0.11", "00:11:22:33:44:55");
        _assets.Setup(repository => repository.FindSoftCandidatesAsync(It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { activeDirectoryAsset });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-app-mac-01",
            Hostname = "VM-APP-MAC-01",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.90.0.99", MacAddress = "00:11:22:33:44:55" } }
        });

        result.Decision.Should().Be(MatchDecision.AutoMerged);
        result.ConfidenceScore.Should().BeGreaterThanOrEqualTo(0.95m);
        result.Explanations.Should().Contain(explanation =>
            explanation.Attribute == MatchAttributes.AzureAdNetworkIdentity && explanation.Matched);
    }

    [Fact]
    public async Task Azure_and_active_directory_hostname_only_is_queued_for_review()
    {
        var activeDirectoryAsset = ActiveDirectoryAsset("vm-app-02", null);
        _assets.Setup(repository => repository.FindSoftCandidatesAsync(It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { activeDirectoryAsset });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-app-02",
            Hostname = "VM-APP-02"
        });

        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.Explanations.Should().Contain(explanation =>
            explanation.Attribute == MatchAttributes.AzureAdNetworkIdentity);
    }

    [Fact]
    public async Task Azure_and_active_directory_conflicting_mac_is_queued_for_review()
    {
        var activeDirectoryAsset = ActiveDirectoryAsset("vm-app-03", "10.90.0.12", "00:11:22:33:44:55");
        _assets.Setup(repository => repository.FindSoftCandidatesAsync(It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { activeDirectoryAsset });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-app-03",
            Hostname = "VM-APP-03",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.90.0.12", MacAddress = "00:aa:bb:cc:dd:ee" } }
        });

        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.Explanations.Should().Contain(explanation =>
            explanation.Attribute == MatchAttributes.AzureAdNetworkIdentity &&
            explanation.MatchedValue == "conflicting MAC observations");
    }

    [Fact]
    public async Task Azure_and_decommissioned_active_directory_counterpart_is_queued_for_review()
    {
        var activeDirectoryAsset = ActiveDirectoryAsset("vm-app-04", "10.90.0.14");
        activeDirectoryAsset.Status = AssetStatus.Decommissioned;
        _assets.Setup(repository => repository.FindSoftCandidatesAsync(It.IsAny<string?>(),
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Asset> { activeDirectoryAsset });

        var result = await _sut.MatchAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "/subscriptions/test/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm-app-04",
            Hostname = "VM-APP-04",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.90.0.14" } }
        });

        result.Decision.Should().Be(MatchDecision.QueuedForReview);
        result.Explanations.Should().Contain(explanation =>
            explanation.Attribute == MatchAttributes.AzureAdNetworkIdentity &&
            explanation.MatchedValue == "counterpart asset is decommissioned");
    }

    private static List<MatchingRule> NetworkRules() => new()
    {
        new() { Name = "Hostname", Attribute = MatchAttributes.Hostname, MatchType = MatchType.Soft, Weight = 0.40m, Order = 10 },
        new() { Name = "MAC", Attribute = MatchAttributes.MacAddress, MatchType = MatchType.Soft, Weight = 0.35m, Order = 20 },
        new() { Name = "IP", Attribute = MatchAttributes.IpAddress, MatchType = MatchType.Soft, Weight = 0.15m, Order = 30 }
    };

    private static Asset ActiveDirectoryAsset(string hostname, string? ipAddress, string? macAddress = null)
    {
        var asset = new Asset { Hostname = hostname, NormalizedHostname = hostname.ToLowerInvariant() };
        asset.Sources.Add(new AssetSource { ConnectorType = ConnectorType.ActiveDirectory, ExternalId = $"CN={hostname},DC=esar,DC=local" });
        if (ipAddress is not null || macAddress is not null)
            asset.IpAddresses.Add(new AssetIp { IpAddress = ipAddress ?? string.Empty, MacAddress = macAddress, Source = ConnectorType.ActiveDirectory });
        return asset;
    }
}
