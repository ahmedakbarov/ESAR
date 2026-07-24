using System.Linq.Expressions;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Application.Merging;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Esar.UnitTests;

public class SourcePriorityEngineTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IRepository<SourcePriority>> _priorities = new();
    private readonly Mock<ICacheService> _cache = new();
    private readonly SourcePriorityEngine _sut;

    public SourcePriorityEngineTests()
    {
        _uow.SetupGet(u => u.SourcePriorities).Returns(_priorities.Object);
        _cache.Setup(c => c.GetAsync<List<SourcePriority>>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<SourcePriority>?)null);
        _priorities.Setup(p => p.ListAsync(It.IsAny<Expression<Func<SourcePriority, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SourcePriority>
            {
                new() { ConnectorType = ConnectorType.Azure, Attribute = null, Priority = 10 },
                new() { ConnectorType = ConnectorType.ActiveDirectory, Attribute = null, Priority = 60 },
                new() { ConnectorType = ConnectorType.MicrosoftDefender, Attribute = null, Priority = 80 },
                new() { ConnectorType = ConnectorType.MicrosoftDefender, Attribute = "OperatingSystem", Priority = 15 }
            });
        _sut = new SourcePriorityEngine(_uow.Object, _cache.Object);
    }

    [Fact]
    public async Task Attribute_override_beats_global_priority()
    {
        (await _sut.GetPriorityAsync(ConnectorType.MicrosoftDefender, "OperatingSystem")).Should().Be(15);
        (await _sut.GetPriorityAsync(ConnectorType.MicrosoftDefender, "Hostname")).Should().Be(80);
    }

    [Fact]
    public async Task Higher_priority_source_wins_conflicts()
    {
        (await _sut.WinsAsync(ConnectorType.Azure, ConnectorType.ActiveDirectory, "Hostname")).Should().BeTrue();
        (await _sut.WinsAsync(ConnectorType.MicrosoftDefender, ConnectorType.Azure, "Hostname")).Should().BeFalse();
        // Defender owns OS via attribute override even against Azure.
        (await _sut.WinsAsync(ConnectorType.MicrosoftDefender, ConnectorType.Azure, "OperatingSystem"))
            .Should().BeFalse(); // Azure global=10 still beats Defender OS=15
        (await _sut.WinsAsync(ConnectorType.MicrosoftDefender, ConnectorType.ActiveDirectory, "OperatingSystem"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Source_always_refreshes_its_own_values_and_fills_empty()
    {
        (await _sut.WinsAsync(ConnectorType.MicrosoftDefender, ConnectorType.MicrosoftDefender, "Hostname"))
            .Should().BeTrue();
        (await _sut.WinsAsync(ConnectorType.MicrosoftDefender, null, "Hostname")).Should().BeTrue();
    }
}

public class MergeEngineTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IRepository<AssetHistory>> _history = new();
    private readonly Mock<IRepository<AssetIp>> _ips = new();
    private readonly Mock<IRepository<AssetTag>> _tags = new();
    private readonly Mock<IRepository<MatchRecord>> _matchRecords = new();
    private readonly Mock<IRepository<ApprovalRequest>> _approvals = new();
    private readonly Mock<IRepository<Incident>> _incidents = new();
    private readonly Mock<IRepository<AssetRelationship>> _relationships = new();
    private readonly Mock<IRepository<AssetCompliance>> _compliance = new();
    private readonly Mock<IRepository<AssetEvent>> _events = new();
    private readonly Mock<IRepository<AssetRisk>> _risks = new();
    private readonly Mock<ISourcePriorityEngine> _priority = new();
    private readonly MergeEngine _sut;

    public MergeEngineTests()
    {
        _uow.SetupGet(u => u.AssetHistories).Returns(_history.Object);
        _uow.SetupGet(u => u.AssetIps).Returns(_ips.Object);
        _uow.SetupGet(u => u.AssetTags).Returns(_tags.Object);
        _uow.SetupGet(u => u.MatchRecords).Returns(_matchRecords.Object);
        _uow.SetupGet(u => u.Approvals).Returns(_approvals.Object);
        _uow.SetupGet(u => u.Incidents).Returns(_incidents.Object);
        _uow.SetupGet(u => u.Relationships).Returns(_relationships.Object);
        _uow.SetupGet(u => u.AssetCompliance).Returns(_compliance.Object);
        _uow.SetupGet(u => u.AssetEvents).Returns(_events.Object);
        _uow.SetupGet(u => u.AssetRisks).Returns(_risks.Object);
        _matchRecords.Setup(repository => repository.ListAsync(
                It.IsAny<Expression<Func<MatchRecord, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MatchRecord>());
        _approvals.Setup(repository => repository.ListAsync(
                It.IsAny<Expression<Func<ApprovalRequest, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ApprovalRequest>());
        _incidents.Setup(repository => repository.ListAsync(
                It.IsAny<Expression<Func<Incident, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Incident>());
        _relationships.Setup(repository => repository.ListAsync(
                It.IsAny<Expression<Func<AssetRelationship, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AssetRelationship>());
        _compliance.Setup(repository => repository.ListAsync(
                It.IsAny<Expression<Func<AssetCompliance, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AssetCompliance>());
        _events.Setup(repository => repository.ListAsync(
                It.IsAny<Expression<Func<AssetEvent, bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AssetEvent>());
        _ips.Setup(repository => repository.AddAsync(It.IsAny<AssetIp>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _tags.Setup(repository => repository.AddAsync(It.IsAny<AssetTag>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _sut = new MergeEngine(_uow.Object, _priority.Object, new Esar.Application.Normalization.NormalizationService(),
            NullLogger<MergeEngine>.Instance);
    }

    [Fact]
    public async Task Fills_empty_attributes_and_records_history()
    {
        _priority.Setup(p => p.WinsAsync(It.IsAny<ConnectorType>(), It.IsAny<ConnectorType?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var asset = new Asset { Hostname = "srv-x", NormalizedHostname = "srv-x" };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.ActiveDirectory,
            ExternalId = "g1",
            OperatingSystem = "Windows Server 2019",
            Domain = "corp.local"
        };

        var changed = await _sut.ApplyAsync(asset, incoming);

        asset.OperatingSystem.Should().Be("Windows Server 2019");
        asset.Domain.Should().Be("corp.local");
        changed.Should().Contain(new[] { "OperatingSystem", "Domain" });
        _history.Verify(h => h.AddAsync(It.IsAny<AssetHistory>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task Lower_priority_source_cannot_overwrite_existing_value()
    {
        _priority.Setup(p => p.WinsAsync(ConnectorType.Dns, It.IsAny<ConnectorType?>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var asset = new Asset
        {
            Hostname = "srv-x",
            NormalizedHostname = "srv-x",
            OperatingSystem = "Windows Server 2022",
            AttributeSourcesJson = """{"OperatingSystem":"Azure"}"""
        };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.Dns,
            ExternalId = "d1",
            OperatingSystem = "Linux (wrong)"
        };

        await _sut.ApplyAsync(asset, incoming);

        asset.OperatingSystem.Should().Be("Windows Server 2022");
    }

    [Fact]
    public async Task Merge_assets_moves_children_and_soft_deletes_duplicate()
    {
        var survivor = new Asset { Hostname = "srv-a", NormalizedHostname = "srv-a" };
        var duplicate = new Asset
        {
            Hostname = "srv-a-dup",
            NormalizedHostname = "srv-a-dup",
            Sources = { new AssetSource { ConnectorType = ConnectorType.Qualys, ExternalId = "q9" } },
            IpAddresses = { new AssetIp { IpAddress = "10.0.0.9" } }
        };

        await _sut.MergeAssetsAsync(survivor, duplicate, "tester");

        survivor.Sources.Should().ContainSingle(s => s.ExternalId == "q9");
        survivor.IpAddresses.Should().ContainSingle(i => i.IpAddress == "10.0.0.9");
        duplicate.IsDeleted.Should().BeTrue();
        duplicate.MergedIntoAssetId.Should().Be(survivor.Id);
    }

    [Fact]
    public async Task Same_source_interface_refresh_replaces_stale_mac_for_the_same_ip()
    {
        var asset = new Asset
        {
            IpAddresses =
            {
                new AssetIp
                {
                    IpAddress = "10.0.0.4",
                    MacAddress = "00:11:22:33:44:55",
                    Source = ConnectorType.Azure
                }
            }
        };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "azure-vm-1",
            SeenAt = DateTime.UtcNow,
            Interfaces =
            {
                new DiscoveredInterface
                {
                    IpAddress = "10.0.0.4",
                    MacAddress = "00:aa:bb:cc:dd:ee",
                    IsPrimary = true
                }
            }
        };

        await _sut.ApplyAsync(asset, incoming);

        asset.IpAddresses.Should().ContainSingle(item => item.IsActive);
        var active = asset.IpAddresses.Single(item => item.IsActive);
        active.MacAddress.Should().Be("00:aa:bb:cc:dd:ee");
        active.IsPrimary.Should().BeTrue();
        active.LastSeen.Should().Be(incoming.SeenAt);
        asset.IpAddresses.Should().ContainSingle(item => !item.IsActive && item.ValidTo == incoming.SeenAt);
    }

    [Fact]
    public async Task Same_source_primary_interface_refresh_demotes_the_previous_primary()
    {
        var asset = new Asset { IpAddresses = { new AssetIp { IpAddress = "10.0.0.4", Source = ConnectorType.Azure, IsPrimary = true }, new AssetIp { IpAddress = "10.0.0.5", Source = ConnectorType.Azure, IsPrimary = false } } };
        var incoming = new DiscoveredAsset { Source = ConnectorType.Azure, ExternalId = "azure-vm-1", Interfaces = { new DiscoveredInterface { IpAddress = "10.0.0.5", IsPrimary = true }, new DiscoveredInterface { IpAddress = "10.0.0.4", IsPrimary = false } } };

        await _sut.ApplyAsync(asset, incoming);

        asset.IpAddresses.Should().ContainSingle(networkInterface => networkInterface.IpAddress == "10.0.0.5" && networkInterface.IsPrimary);
        asset.IpAddresses.Should().ContainSingle(networkInterface => networkInterface.IpAddress == "10.0.0.4" && !networkInterface.IsPrimary);
    }

    [Fact]
    public async Task New_connector_interface_and_tag_are_registered_as_added_dependents()
    {
        var asset = new Asset { Hostname = "azure-vm", NormalizedHostname = "azure-vm" };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "azure-vm-1",
            Interfaces = { new DiscoveredInterface { IpAddress = "10.0.0.4", MacAddress = "00:11:22:33:44:55", IsPrimary = true } },
            Tags = { ["public_ip"] = "true" }
        };

        await _sut.ApplyAsync(asset, incoming);

        asset.IpAddresses.Should().ContainSingle(interfaceValue => interfaceValue.IpAddress == "10.0.0.4");
        asset.Tags.Should().ContainSingle(tag => tag.Key == "public_ip" && tag.Value == "true");
        _ips.Verify(repository => repository.AddAsync(
            It.Is<AssetIp>(ip => ip.AssetId == asset.Id && ip.IpAddress == "10.0.0.4" && ip.Source == ConnectorType.Azure),
            It.IsAny<CancellationToken>()), Times.Once);
        _tags.Verify(repository => repository.AddAsync(
            It.Is<AssetTag>(tag => tag.AssetId == asset.Id && tag.Key == "public_ip" && tag.Source == ConnectorType.Azure),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Same_ip_from_different_sources_keeps_separate_provenance()
    {
        var asset = new Asset
        {
            IpAddresses =
            {
                new AssetIp
                {
                    IpAddress = "10.0.0.4",
                    MacAddress = "00:11:22:33:44:55",
                    Source = ConnectorType.Azure,
                    LastSeen = DateTime.UtcNow.AddMinutes(-5)
                }
            }
        };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.ActiveDirectory,
            ExternalId = "CN=vm01,DC=esar,DC=local",
            SeenAt = DateTime.UtcNow,
            Interfaces = { new DiscoveredInterface { IpAddress = "10.0.0.4", IsPrimary = true } }
        };

        await _sut.ApplyAsync(asset, incoming);

        asset.IpAddresses.Should().HaveCount(2);
        asset.IpAddresses.Should().ContainSingle(network =>
            network.Source == ConnectorType.Azure && network.MacAddress == "00:11:22:33:44:55");
        asset.IpAddresses.Should().ContainSingle(network =>
            network.Source == ConnectorType.ActiveDirectory && network.IpAddress == "10.0.0.4");
    }

    [Fact]
    public async Task Connector_cannot_overwrite_manually_owned_attribute()
    {
        var asset = new Asset
        {
            OwnerName = "SOC Team",
            AttributeSourcesJson = """{"OwnerName":"Manual"}"""
        };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "azure-vm-1",
            OwnerName = "Tag Owner"
        };

        await _sut.ApplyAsync(asset, incoming);

        asset.OwnerName.Should().Be("SOC Team");
        _priority.Verify(priority => priority.WinsAsync(
            It.IsAny<ConnectorType>(), It.IsAny<ConnectorType?>(), nameof(Asset.OwnerName),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Merge_assets_fills_missing_metadata_and_moves_software()
    {
        var survivor = new Asset { Hostname = "vm01", NormalizedHostname = "vm01" };
        var duplicate = new Asset
        {
            Hostname = "vm01-old",
            NormalizedHostname = "vm01-old",
            OperatingSystem = "Windows Server 2022",
            OwnerName = "Infrastructure",
            AttributeSourcesJson = """{"OperatingSystem":"MicrosoftDefender","OwnerName":"ServiceNowCmdb"}""",
            Software =
            {
                new AssetSoftware
                {
                    Name = "Defender Agent",
                    Version = "1.0",
                    Source = ConnectorType.MicrosoftDefender
                }
            }
        };

        await _sut.MergeAssetsAsync(survivor, duplicate, "reviewer");

        survivor.OperatingSystem.Should().Be("Windows Server 2022");
        survivor.OwnerName.Should().Be("Infrastructure");
        survivor.Software.Should().ContainSingle(software =>
            software.Name == "Defender Agent" && software.AssetId == survivor.Id);
        survivor.AttributeSourcesJson.Should().Contain("MicrosoftDefender");
        duplicate.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task Apply_updates_normalized_hostname_when_authoritative_hostname_changes()
    {
        var asset = new Asset
        {
            Hostname = "old-name",
            NormalizedHostname = "old-name",
            AttributeSourcesJson = """{"Hostname":"ActiveDirectory"}"""
        };
        _priority.Setup(engine => engine.WinsAsync(
                ConnectorType.Azure, ConnectorType.ActiveDirectory, nameof(Asset.Hostname),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ApplyAsync(asset, new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "azure-rename",
            Hostname = "NEW-NAME.ESAR.LOCAL"
        });

        asset.Hostname.Should().Be("NEW-NAME.ESAR.LOCAL");
        asset.NormalizedHostname.Should().Be("new-name.esar.local");
    }
}
