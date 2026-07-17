using System.Text.Json;
using Esar.Infrastructure.Connectors;
using FluentAssertions;
using Xunit;

namespace Esar.UnitTests;

public class AzureAssetMapperTests
{
    [Fact]
    public void Maps_machine_without_nic_and_preserves_unmapped_tags()
    {
        using var doc = JsonDocument.Parse("""{"id":"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1","name":"vm1","location":"westeurope","subscriptionId":"s","resourceGroup":"rg","tags":{"Environment":"prod","CostCenter":"cc1"}}""");
        var asset = AzureAssetMapper.MapMachine(doc.RootElement, true)!;
        asset.Environment.ToString().Should().Be("Production");
        asset.Tags["CostCenter"].Should().Be("cc1");
        asset.Interfaces.Should().BeEmpty();
    }

    [Fact]
    public void Aggregates_multiple_nics_and_marks_public_ip()
    {
        using var vm = JsonDocument.Parse("""{"id":"/subscriptions/s/resourceGroups/rg/providers/Microsoft.Compute/virtualMachines/vm1","name":"vm1"}""");
        using var first = JsonDocument.Parse("""{"privateIp":"10.0.0.4","macAddress":"00-11-22-33-44-55"}""");
        using var second = JsonDocument.Parse("""{"privateIp":"10.0.0.5","macAddress":"00-11-22-33-44-56","publicIp":"20.1.1.1"}""");
        var asset = AzureAssetMapper.MapMachine(vm.RootElement, true)!;
        AzureAssetMapper.ApplyNicRow(asset, first.RootElement);
        AzureAssetMapper.ApplyNicRow(asset, second.RootElement);
        asset.Interfaces.Should().HaveCount(2);
        asset.Tags["public_ip"].Should().Be("true");
        asset.Tags["internet_facing"].Should().Be("true");
    }

    [Fact]
    public void Parses_subscription_ids_from_json_or_csv()
    {
        AzureAssetMapper.ParseSubscriptionIds("[\"a\", \"b\"]").Should().Equal("a", "b");
        AzureAssetMapper.ParseSubscriptionIds("a, b").Should().Equal("a", "b");
    }
}
