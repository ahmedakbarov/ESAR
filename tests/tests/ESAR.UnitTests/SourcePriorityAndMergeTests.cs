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
    private readonly Mock<ISourcePriorityEngine> _priority = new();
    private readonly MergeEngine _sut;

    public MergeEngineTests()
    {
        _uow.SetupGet(u => u.AssetHistories).Returns(_history.Object);
        _sut = new MergeEngine(_uow.Object, _priority.Object, NullLogger<MergeEngine>.Instance);
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
}
