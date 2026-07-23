using Esar.Application.Abstractions;
using Esar.Application.Approvals;
using Esar.Application.Auditing;
using Esar.Application.Contracts;
using Esar.Application.Ingestion;
using Esar.Application.Matching;
using Esar.Application.Merging;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using MatchType = Esar.Domain.Enums.MatchType;
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
    private readonly Mock<IAuditService> _audit = new();
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
        _audit.Setup(audit => audit.LogAsync(It.IsAny<AuditAction>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _sut = new AssetIngestionService(_uow.Object, _normalization.Object, _matching.Object, _merge.Object,
            _approvals.Object, _events.Object, _audit.Object, NullLogger<AssetIngestionService>.Instance);
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

    [Fact]
    public async Task Rediscovering_a_deleted_asset_reactivates_it_instead_of_creating_a_duplicate()
    {
        const string externalId = "CN=old-server,OU=Computers,DC=esar,DC=local";
        var seenAt = new DateTime(2026, 7, 22, 9, 0, 0, DateTimeKind.Utc);
        var deletedAsset = new Asset
        {
            Hostname = "old-server", NormalizedHostname = "old-server",
            IsDeleted = true, Status = AssetStatus.Decommissioned, LifecycleStatus = LifecycleStatus.Retired
        };
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.ActiveDirectory, ExternalId = externalId, Hostname = "old-server", SeenAt = seenAt
        };

        _assets.Setup(repository =>
                repository.FindBySourceAsync(ConnectorType.ActiveDirectory, externalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
        _assets.Setup(repository =>
                repository.FindDeletedBySourceAsync(ConnectorType.ActiveDirectory, externalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedAsset);

        var outcome = await _sut.IngestAsync(incoming);

        outcome.Should().Be(IngestionOutcome.Updated);
        deletedAsset.IsDeleted.Should().BeFalse();
        deletedAsset.Status.Should().Be(AssetStatus.Active);
        deletedAsset.LifecycleStatus.Should().Be(LifecycleStatus.Active);
        _matching.Verify(engine => engine.MatchAsync(It.IsAny<DiscoveredAsset>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(audit => audit.LogAsync(AuditAction.AssetReactivated, nameof(Asset), deletedAsset.Id.ToString(),
            It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Repeated_ambiguous_sync_updates_existing_review_instead_of_adding_duplicate()
    {
        var incoming = new DiscoveredAsset
        {
            Source = ConnectorType.Tenable,
            ExternalId = "tenable-asset-1",
            Hostname = "shared-host"
        };
        var matched = new Asset { Hostname = "shared-host", NormalizedHostname = "shared-host" };
        var pending = new MatchRecord
        {
            SourceConnector = incoming.Source,
            ExternalId = incoming.ExternalId,
            Decision = MatchDecision.QueuedForReview,
            ConfidenceScore = 0.40m
        };

        _assets.Setup(repository => repository.FindBySourceAsync(
                incoming.Source, incoming.ExternalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
        _assets.Setup(repository => repository.FindDeletedBySourceAsync(
                incoming.Source, incoming.ExternalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
        _matching.Setup(engine => engine.MatchAsync(incoming, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MatchResult
            {
                Decision = MatchDecision.QueuedForReview,
                MatchedAsset = matched,
                ConfidenceScore = 0.55m,
                MatchType = MatchType.Soft
            });
        _matchRecords.Setup(repository => repository.FirstOrDefaultAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<MatchRecord, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);

        var outcome = await _sut.IngestAsync(incoming);

        outcome.Should().Be(IngestionOutcome.QueuedForReview);
        pending.ConfidenceScore.Should().Be(0.55m);
        pending.MatchedAssetId.Should().Be(matched.Id);
        _matchRecords.Verify(repository => repository.AddAsync(
            It.IsAny<MatchRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        _matchRecords.Verify(repository => repository.Update(pending), Times.Once);
    }
}
