using Esar.Application.Abstractions;
using Esar.Application.Approvals;
using Esar.Application.Contracts;
using Esar.Application.Ingestion;
using Esar.Application.Matching;
using Esar.Application.Merging;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Esar.UnitTests;

public class AssetIngestionServiceTests
{
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IAssetRepository> _assets = new();
    private readonly Mock<IRepository<AssetSource>> _sources = new();
    private readonly Mock<IRepository<MatchRecord>> _matchRecords = new();
    private readonly Mock<INormalizationService> _normalization = new();
    private readonly Mock<IMatchingEngine> _matching = new();
    private readonly Mock<IMergeEngine> _merge = new();
    private readonly Mock<IApprovalService> _approvals = new();
    private readonly Mock<IEventBus> _events = new();
    private readonly AssetIngestionService _sut;

    public AssetIngestionServiceTests()
    {
        _uow.SetupGet(unit => unit.Assets).Returns(_assets.Object);
        _uow.SetupGet(unit => unit.AssetSources).Returns(_sources.Object);
        _uow.SetupGet(unit => unit.MatchRecords).Returns(_matchRecords.Object);
        _uow.Setup(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _normalization.Setup(service => service.Normalize(It.IsAny<DiscoveredAsset>()))
            .Returns((DiscoveredAsset asset) => asset);
        _merge.Setup(engine => engine.ApplyAsync(It.IsAny<Asset>(), It.IsAny<DiscoveredAsset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _sources.Setup(repository => repository.AddAsync(It.IsAny<AssetSource>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _matchRecords.Setup(repository => repository.AddAsync(It.IsAny<MatchRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _events.Setup(bus => bus.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _sut = new AssetIngestionService(_uow.Object, _normalization.Object, _matching.Object, _merge.Object,
            _approvals.Object, _events.Object, NullLogger<AssetIngestionService>.Instance);
    }

    [Fact]
    public async Task Auto_merge_registers_new_source_link_as_an_added_dependent()
    {
        const string externalId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm01";
        var seenAt = new DateTime(2026, 7, 20, 15, 0, 0, DateTimeKind.Utc);
        var existing = new Asset { Hostname = "vm01", NormalizedHostname = "vm01" };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = externalId,
            Hostname = "vm01",
            SeenAt = seenAt
        };

        _assets.Setup(repository => repository.FindBySourceAsync(ConnectorType.Azure, externalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
        _matching.Setup(engine => engine.MatchAsync(incoming, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchResult { Decision = MatchDecision.AutoMerged, MatchedAsset = existing });

        var outcome = await _sut.IngestAsync(incoming);

        outcome.Should().Be(IngestionOutcome.Updated);
        existing.Sources.Should().ContainSingle(source =>
            source.AssetId == existing.Id && source.ConnectorType == ConnectorType.Azure &&
            source.ExternalId == externalId && source.SourceHostname == "vm01" &&
            source.FirstSeen == seenAt && source.LastSeen == seenAt);
        _sources.Verify(repository => repository.AddAsync(
            It.Is<AssetSource>(source => source.AssetId == existing.Id &&
                source.ConnectorType == ConnectorType.Azure && source.ExternalId == externalId),
            It.IsAny<CancellationToken>()), Times.Once);
        _assets.Verify(repository => repository.Update(existing), Times.Once);
        _uow.Verify(unit => unit.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
