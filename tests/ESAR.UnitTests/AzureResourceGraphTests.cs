using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Infrastructure.Connectors;
using FluentAssertions;
using Xunit;

namespace Esar.UnitTests;

public class AzureResourceGraphTests
{
    [Fact]
    public void ParseSubscriptionIds_accepts_csv_and_removes_duplicates()
    {
        var settings = new ConnectorSettings
        {
            Values = new Dictionary<string, string>
            {
                ["subscriptionIds"] = "11111111-1111-1111-1111-111111111111, 22222222-2222-2222-2222-222222222222,11111111-1111-1111-1111-111111111111"
            }
        };

        var subscriptions = AzureResourceGraph.ParseSubscriptionIds(settings);

        subscriptions.Should().Equal(
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222");
    }

    [Fact]
    public void ParseSubscriptionIds_accepts_json_array()
    {
        var settings = new ConnectorSettings
        {
            Values = new Dictionary<string, string>
            {
                ["subscriptionIds"] = "[\"11111111-1111-1111-1111-111111111111\", \"22222222-2222-2222-2222-222222222222\"]"
            }
        };

        var subscriptions = AzureResourceGraph.ParseSubscriptionIds(settings);

        subscriptions.Should().HaveCount(2);
    }

    [Fact]
    public void BuildRequestBody_uses_resource_graph_skip_token_and_scopes_subscriptions()
    {
        var body = AzureResourceGraph.BuildRequestBody(
            "Resources | project id",
            new[] { "11111111-1111-1111-1111-111111111111" },
            "next-page");
        using var document = JsonDocument.Parse(body);

        var root = document.RootElement;
        root.GetProperty("query").GetString().Should().Be("Resources | project id");
        root.GetProperty("subscriptions").EnumerateArray().Select(value => value.GetString())
            .Should().ContainSingle().Which.Should().Be("11111111-1111-1111-1111-111111111111");
        var options = root.GetProperty("options");
        options.GetProperty("$skipToken").GetString().Should().Be("next-page");
        options.TryGetProperty("skipToken", out _).Should().BeFalse();
    }

    [Fact]
    public void GetNextPageToken_rejects_a_truncated_response_without_continuation()
    {
        using var document = JsonDocument.Parse("""{ "resultTruncated": true }""");
        var observed = new HashSet<string>(StringComparer.Ordinal);

        var action = () => AzureResourceGraph.GetNextPageToken(document.RootElement, observed);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*truncated result without a continuation token*");
    }

    [Fact]
    public void EnrichVmAssets_adds_all_private_ips_and_mac_addresses_and_marks_public_ip()
    {
        const string vmId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm01";
        const string publicIpResourceId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/pip01";
        var asset = new DiscoveredAsset();
        var assets = new Dictionary<string, DiscoveredAsset>(StringComparer.OrdinalIgnoreCase)
        {
            [vmId] = asset
        };

        using var nicDocument = JsonDocument.Parse($$"""
            [
              {
                "vmResourceId": "{{vmId.ToUpperInvariant()}}",
                "mac": "00-11-22-33-44-55",
                "isNicPrimary": false,
                "ipConfigurations": [
                  { "properties": { "privateIPAddress": "10.0.0.5" } }
                ]
              },
              {
                "vmResourceId": "{{vmId}}",
                "mac": "00-aa-bb-cc-dd-ee",
                "isNicPrimary": true,
                "ipConfigurations": [
                  { "properties": { "privateIPAddress": "10.0.0.4", "primary": true, "publicIPAddress": { "id": "{{publicIpResourceId}}" } } }
                ]
              }
            ]
            """);
        using var publicIpDocument = JsonDocument.Parse($$"""
            [ { "id": "{{publicIpResourceId.ToUpperInvariant()}}", "publicIp": "20.30.40.50" } ]
            """);

        var observations = AzureResourceGraph.ParseNicObservations(nicDocument.RootElement.EnumerateArray());
        var publicIpAddresses = AzureResourceGraph.ParsePublicIpAddresses(publicIpDocument.RootElement.EnumerateArray());

        var result = AzureResourceGraph.EnrichVmAssets(assets, observations, publicIpAddresses);

        result.MatchedVms.Should().Be(1);
        result.InterfacesAdded.Should().Be(2);
        result.PublicIpReferences.Should().Be(1);
        asset.Interfaces.Should().HaveCount(2);
        asset.Interfaces[0].IpAddress.Should().Be("10.0.0.4");
        asset.Interfaces[0].MacAddress.Should().Be("00-aa-bb-cc-dd-ee");
        asset.Interfaces[0].IsPrimary.Should().BeTrue();
        asset.Interfaces[1].IpAddress.Should().Be("10.0.0.5");
        asset.Interfaces[1].MacAddress.Should().Be("00-11-22-33-44-55");
        asset.Interfaces[1].IsPrimary.Should().BeFalse();
        asset.Tags["public_ip"].Should().Be("true");
        asset.Tags["internet_facing"].Should().Be("true");
        asset.Tags["azure_public_ips"].Should().Be("20.30.40.50");
    }

    [Fact]
    public void ParseNicObservations_reads_dynamic_configurations_serialized_as_a_json_string()
    {
        using var nicDocument = JsonDocument.Parse("""
            [
              {
                "vmResourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm01",
                "mac": "00-11-22-33-44-55",
                "isNicPrimary": true,
                "ipConfigurations": "[{\"properties\":{\"privateIPAddress\":\"10.0.0.4\",\"primary\":true}}]"
              }
            ]
            """);

        var observations = AzureResourceGraph.ParseNicObservations(nicDocument.RootElement.EnumerateArray());

        observations.Should().ContainSingle();
        observations[0].PrivateIpAddress.Should().Be("10.0.0.4");
        observations[0].MacAddress.Should().Be("00-11-22-33-44-55");
        observations[0].IsPrimary.Should().BeTrue();
    }

    [Fact]
    public void ParseNicObservations_uses_primary_scalar_fallback_when_dynamic_configurations_are_unavailable()
    {
        using var nicDocument = JsonDocument.Parse("""
            [
              {
                "vmResourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm01",
                "mac": "00-11-22-33-44-55",
                "isNicPrimary": true,
                "primaryPrivateIp": "10.0.0.4",
                "primaryPublicIpResourceId": "/subscriptions/s/resourceGroups/rg/providers/Microsoft.Network/publicIPAddresses/pip01"
              }
            ]
            """);

        var observations = AzureResourceGraph.ParseNicObservations(nicDocument.RootElement.EnumerateArray());

        observations.Should().ContainSingle();
        observations[0].PrivateIpAddress.Should().Be("10.0.0.4");
        observations[0].MacAddress.Should().Be("00-11-22-33-44-55");
        observations[0].PublicIpResourceId.Should().Contain("publicIPAddresses/pip01");

        var asset = new DiscoveredAsset();
        var assets = new Dictionary<string, DiscoveredAsset>(StringComparer.OrdinalIgnoreCase)
        {
            ["/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm01"] = asset
        };

        AzureResourceGraph.EnrichVmAssets(assets, observations, new Dictionary<string, string>());

        asset.Interfaces.Should().ContainSingle(interfaceValue => interfaceValue.IpAddress == "10.0.0.4");
        asset.Tags["public_ip"].Should().Be("true");
        asset.Tags["internet_facing"].Should().Be("true");
    }

    [Fact]
    public void ParseSubscriptionIds_rejects_invalid_values()
    {
        var settings = new ConnectorSettings
        {
            Values = new Dictionary<string, string> { ["subscriptionIds"] = "not-a-subscription" }
        };

        var action = () => AzureResourceGraph.ParseSubscriptionIds(settings);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*subscriptionIds*valid Azure subscription GUID*");
    }
}
