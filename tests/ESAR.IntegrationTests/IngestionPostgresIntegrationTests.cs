using Esar.Application.Contracts;
using Esar.Application.Ingestion;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Esar.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Esar.IntegrationTests;

/// <summary>
/// Exercises the connector ingestion pipeline against the Testcontainers PostgreSQL instance.
/// Each persistence assertion uses a fresh DI scope to match the worker's scoped EF behavior.
/// </summary>
public class IngestionPostgresIntegrationTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public IngestionPostgresIntegrationTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Azure_discovery_persists_network_children_tags_and_source_link()
    {
        var identity = NetworkIdentity.Create();
        var externalId = AzureExternalId(identity.Token);
        var discovered = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = externalId,
            Hostname = $"az-persist-{identity.Token[..8]}",
            CloudProvider = "Azure",
            CloudResourceId = externalId,
            SeenAt = DateTime.UtcNow,
            Interfaces =
            {
                new DiscoveredInterface { IpAddress = identity.PrimaryIp, MacAddress = identity.DashedMac, IsPrimary = true },
                new DiscoveredInterface { IpAddress = identity.SecondaryIp, MacAddress = identity.SecondDashedMac, IsPrimary = false }
            },
            Tags =
            {
                ["public_ip"] = "true",
                ["internet_facing"] = "true"
            }
        };

        var outcome = await IngestAsync(discovered);

        outcome.Should().Be(IngestionOutcome.Created);
        var asset = await LoadAssetBySourceAsync(ConnectorType.Azure, externalId);
        asset.CloudResourceId.Should().Be(externalId);
        asset.Sources.Should().ContainSingle(source =>
            source.ConnectorType == ConnectorType.Azure && source.ExternalId == externalId);
        asset.IpAddresses.Should().ContainSingle(networkInterface =>
            networkInterface.IpAddress == identity.PrimaryIp && networkInterface.MacAddress == identity.Mac &&
            networkInterface.IsPrimary && networkInterface.Source == ConnectorType.Azure);
        asset.IpAddresses.Should().ContainSingle(networkInterface =>
            networkInterface.IpAddress == identity.SecondaryIp && networkInterface.MacAddress == identity.SecondMac &&
            !networkInterface.IsPrimary && networkInterface.Source == ConnectorType.Azure);
        asset.Tags.Should().Contain(tag => tag.Key == "public_ip" && tag.Value == "true" && tag.Source == ConnectorType.Azure);
        asset.Tags.Should().Contain(tag => tag.Key == "internet_facing" && tag.Value == "true" && tag.Source == ConnectorType.Azure);
    }

    [Fact]
    public async Task Azure_first_then_active_directory_auto_merges_into_one_golden_asset()
    {
        var identity = NetworkIdentity.Create();
        var hostname = $"az-ad-{identity.Token[..8]}";
        var azureExternalId = AzureExternalId(identity.Token);
        var adExternalId = $"CN={hostname},OU=Servers,DC=esar,DC=local";

        (await IngestAsync(new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = azureExternalId,
            Hostname = hostname,
            CloudProvider = "Azure",
            CloudResourceId = azureExternalId,
            Interfaces = { new DiscoveredInterface { IpAddress = identity.PrimaryIp, MacAddress = identity.DashedMac, IsPrimary = true } }
        })).Should().Be(IngestionOutcome.Created);

        (await IngestAsync(new DiscoveredAsset
        {
            Source = ConnectorType.ActiveDirectory,
            ExternalId = adExternalId,
            Hostname = hostname.ToUpperInvariant(),
            Domain = "esar.local",
            Interfaces = { new DiscoveredInterface { IpAddress = identity.PrimaryIp, IsPrimary = true } }
        })).Should().Be(IngestionOutcome.Updated);

        var asset = await LoadAssetBySourceAsync(ConnectorType.Azure, azureExternalId);
        asset.Sources.Should().Contain(source => source.ConnectorType == ConnectorType.Azure && source.ExternalId == azureExternalId);
        asset.Sources.Should().Contain(source => source.ConnectorType == ConnectorType.ActiveDirectory && source.ExternalId == adExternalId);
        asset.IpAddresses.Should().Contain(networkInterface =>
            networkInterface.Source == ConnectorType.Azure && networkInterface.IpAddress == identity.PrimaryIp &&
            networkInterface.MacAddress == identity.Mac);
        asset.IpAddresses.Should().Contain(networkInterface =>
            networkInterface.Source == ConnectorType.ActiveDirectory &&
            networkInterface.IpAddress == identity.PrimaryIp);

        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EsarDbContext>();
        (await db.AssetSources.Where(source => source.ExternalId == azureExternalId || source.ExternalId == adExternalId)
            .Select(source => source.AssetId).Distinct().CountAsync()).Should().Be(1);
        var match = await db.MatchRecords.SingleAsync(record =>
            record.SourceConnector == ConnectorType.ActiveDirectory && record.ExternalId == adExternalId);
        match.Decision.Should().Be(MatchDecision.AutoMerged);
        match.MatchedAssetId.Should().Be(asset.Id);
    }

    [Fact]
    public async Task Active_directory_first_then_repeat_azure_sync_preserves_one_asset_and_no_duplicate_network_rows()
    {
        var identity = NetworkIdentity.Create();
        var hostname = $"ad-az-{identity.Token[..8]}";
        var azureExternalId = AzureExternalId(identity.Token);
        var adExternalId = $"CN={hostname},OU=Servers,DC=esar,DC=local";

        (await IngestAsync(new DiscoveredAsset
        {
            Source = ConnectorType.ActiveDirectory,
            ExternalId = adExternalId,
            Hostname = hostname,
            Domain = "esar.local",
            Interfaces = { new DiscoveredInterface { IpAddress = identity.PrimaryIp, IsPrimary = true } }
        })).Should().Be(IngestionOutcome.Created);

        var azureDiscovery = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = azureExternalId,
            Hostname = hostname.ToUpperInvariant(),
            CloudProvider = "Azure",
            CloudResourceId = azureExternalId,
            Interfaces = { new DiscoveredInterface { IpAddress = identity.PrimaryIp, MacAddress = identity.DashedMac, IsPrimary = true } },
            Tags = { ["public_ip"] = "true" }
        };
        (await IngestAsync(azureDiscovery)).Should().Be(IngestionOutcome.Updated);

        azureDiscovery.SeenAt = DateTime.UtcNow.AddMinutes(1);
        (await IngestAsync(azureDiscovery)).Should().Be(IngestionOutcome.Updated);

        var asset = await LoadAssetBySourceAsync(ConnectorType.Azure, azureExternalId);
        asset.Sources.Count(source => source.ConnectorType == ConnectorType.Azure).Should().Be(1);
        asset.Sources.Count(source => source.ConnectorType == ConnectorType.ActiveDirectory).Should().Be(1);
        asset.IpAddresses.Count(networkInterface => networkInterface.Source == ConnectorType.Azure &&
            networkInterface.IpAddress == identity.PrimaryIp && networkInterface.MacAddress == identity.Mac).Should().Be(1);
        asset.Tags.Count(tag => tag.Key == "public_ip" && tag.Source == ConnectorType.Azure).Should().Be(1);
    }

    private async Task<IngestionOutcome> IngestAsync(DiscoveredAsset discovered)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IAssetIngestionService>();
        return await ingestion.IngestAsync(discovered);
    }

    private async Task<Asset> LoadAssetBySourceAsync(ConnectorType source, string externalId)
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EsarDbContext>();
        return await db.Assets.AsNoTracking()
            .Include(asset => asset.Sources)
            .Include(asset => asset.IpAddresses)
            .Include(asset => asset.Tags)
            .AsSplitQuery()
            .SingleAsync(asset => asset.Sources.Any(link => link.ConnectorType == source && link.ExternalId == externalId));
    }

    private static string AzureExternalId(string token) =>
        $"/subscriptions/integration-{token}/resourceGroups/rg-integration/providers/Microsoft.Compute/virtualMachines/vm-{token}";

    private sealed record NetworkIdentity(string Token, string PrimaryIp, string SecondaryIp, string Mac,
        string SecondMac)
    {
        public string DashedMac => Mac.Replace(':', '-').ToUpperInvariant();
        public string SecondDashedMac => SecondMac.Replace(':', '-').ToUpperInvariant();

        public static NetworkIdentity Create()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            var token = Guid.NewGuid().ToString("N");
            return new NetworkIdentity(
                token,
                $"10.250.{bytes[0] % 200 + 20}.{bytes[1] % 200 + 20}",
                $"10.251.{bytes[2] % 200 + 20}.{bytes[3] % 200 + 20}",
                $"02:{bytes[4]:x2}:{bytes[5]:x2}:{bytes[6]:x2}:{bytes[7]:x2}:{bytes[8]:x2}",
                $"02:{bytes[9]:x2}:{bytes[10]:x2}:{bytes[11]:x2}:{bytes[12]:x2}:{bytes[13]:x2}");
        }
    }
}
