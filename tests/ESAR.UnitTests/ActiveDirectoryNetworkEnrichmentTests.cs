using System.Net;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Infrastructure.Connectors;
using FluentAssertions;
using Xunit;

namespace Esar.UnitTests;

public class ActiveDirectoryNetworkEnrichmentTests
{
    [Fact]
    public void Parse_uses_bounded_dns_defaults_and_optional_mac_attributes()
    {
        var options = ActiveDirectoryConnectionOptions.Parse(Settings(
            ("resolveDns", "true"),
            ("dnsTimeoutSeconds", "9"),
            ("dnsMaxConcurrency", "4"),
            ("macAttributes", "customMacAddress, msDS-NetworkDeviceMacAddress, customMacAddress")));

        options.ResolveDns.Should().BeTrue();
        options.DnsTimeout.Should().Be(TimeSpan.FromSeconds(9));
        options.DnsMaxConcurrency.Should().Be(4);
        options.MacAttributes.Should().Equal("customMacAddress", "msDS-NetworkDeviceMacAddress");
        options.BaseDnDomain.Should().Be("esar.local");
    }

    [Theory]
    [InlineData("dnsTimeoutSeconds", "0")]
    [InlineData("dnsTimeoutSeconds", "31")]
    [InlineData("dnsMaxConcurrency", "0")]
    [InlineData("dnsMaxConcurrency", "33")]
    [InlineData("macAttributes", "networkAddress,description;binary")]
    [InlineData("macAttributes", "networkAddress")]
    public void Parse_rejects_unsafe_dns_or_mac_settings(string key, string value)
    {
        var action = () => ActiveDirectoryConnectionOptions.Parse(Settings((key, value)));

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("workstation01.esar.local", "esar.local", true)]
    [InlineData("workstation01.esar.local.", "esar.local", true)]
    [InlineData("workstation01.other.local", "esar.local", false)]
    [InlineData("esar.local", "esar.local", false)]
    [InlineData("10.10.10.10", "esar.local", false)]
    public void IsHostWithinDomain_allows_only_in_domain_dns_hostnames(string host, string domain, bool expected)
    {
        ActiveDirectoryNetworkEnrichment.IsHostWithinDomain(host, domain).Should().Be(expected);
    }

    [Fact]
    public void GetDnsTargets_uses_only_ldap_dns_hostnames_inside_base_dn_domain()
    {
        var inside = new DiscoveredAsset { Fqdn = "client01.esar.local", Hostname = "client01.esar.local" };
        var outside = new DiscoveredAsset { Fqdn = "client01.example.net" };
        var noFqdn = new DiscoveredAsset { Hostname = "client02.esar.local" };

        var targets = ActiveDirectoryNetworkEnrichment.GetDnsTargets(new[] { inside, outside, noFqdn }, "esar.local");

        targets.Should().ContainSingle();
        targets[0].Asset.Should().BeSameAs(inside);
        targets[0].Hostname.Should().Be("client01.esar.local");
    }

    [Fact]
    public void AppendMacOnlyInterfaces_normalizes_only_valid_eui48_values()
    {
        var asset = new DiscoveredAsset();

        var added = ActiveDirectoryNetworkEnrichment.AppendMacOnlyInterfaces(asset,
            new[] { "00-11-22-33-44-55", "0011.2233.4455", "00:aa:bb:cc:dd:ee", "not-a-mac", "00:00:00:00:00:00" });

        added.Should().Be(2);
        asset.Interfaces.Should().HaveCount(2);
        asset.Interfaces.Should().OnlyContain(networkInterface => networkInterface.IpAddress == null);
        asset.Interfaces.Select(networkInterface => networkInterface.MacAddress)
            .Should().BeEquivalentTo("00:11:22:33:44:55", "00:aa:bb:cc:dd:ee");
    }

    [Fact]
    public void AppendDnsIpOnlyInterfaces_filters_unsafe_addresses_without_pairing_existing_ldap_mac()
    {
        var asset = new DiscoveredAsset();
        ActiveDirectoryNetworkEnrichment.AppendMacOnlyInterfaces(asset, new[] { "00-11-22-33-44-55" });

        var added = ActiveDirectoryNetworkEnrichment.AppendDnsIpOnlyInterfaces(asset, new[]
        {
            IPAddress.Parse("10.10.20.30"),
            IPAddress.Loopback,
            IPAddress.Any,
            IPAddress.Parse("224.0.0.1"),
            IPAddress.Parse("fe80::1"),
            IPAddress.IPv6Any
        });

        added.Should().Be(1);
        asset.Interfaces.Should().HaveCount(2);
        asset.Interfaces.Should().ContainSingle(networkInterface =>
            networkInterface.MacAddress == "00:11:22:33:44:55" && networkInterface.IpAddress == null);
        asset.Interfaces.Should().ContainSingle(networkInterface =>
            networkInterface.IpAddress == "10.10.20.30" && networkInterface.MacAddress == null);
    }

    private static ConnectorSettings Settings(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "dc01.esar.local",
            ["baseDn"] = "DC=esar,DC=local",
            ["username"] = "svc_esar_ad@esar.local",
            ["password"] = "OnlyForThisTest!"
        };

        foreach (var (key, value) in overrides) values[key] = value;
        return new ConnectorSettings { Values = values };
    }
}
