using Esar.Application.Contracts;
using Esar.Application.Normalization;
using Esar.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Esar.UnitTests;

public class NormalizationServiceTests
{
    private readonly NormalizationService _sut = new();

    [Theory]
    [InlineData("  SRV-DB01  ", "srv-db01")]
    [InlineData("WEB01.corp.local.", "web01.corp.local")]
    [InlineData("Host Name", "host-name")]
    public void NormalizeHostname_produces_canonical_form(string input, string expected)
        => _sut.NormalizeHostname(input).Should().Be(expected);

    [Theory]
    [InlineData("00-1A-2B-3C-4D-5E", "00:1a:2b:3c:4d:5e")]
    [InlineData("001a.2b3c.4d5e", "00:1a:2b:3c:4d:5e")]
    [InlineData("001A2B3C4D5E", "00:1a:2b:3c:4d:5e")]
    public void NormalizeMac_handles_all_common_formats(string input, string expected)
        => _sut.NormalizeMac(input).Should().Be(expected);

    [Theory]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    [InlineData("not-a-mac")]
    [InlineData("")]
    public void NormalizeMac_rejects_invalid_and_placeholder_values(string input)
        => _sut.NormalizeMac(input).Should().BeNull();

    [Theory]
    [InlineData("Microsoft Windows Server 2019 Datacenter", "Windows Server 2019")]
    [InlineData("windows server 2022 standard", "Windows Server 2022")]
    [InlineData("Ubuntu 22.04.3 LTS", "Ubuntu Linux")]
    [InlineData("Red Hat Enterprise Linux 8.9", "Red Hat Enterprise Linux")]
    public void NormalizeOs_maps_to_canonical_names(string input, string expected)
        => _sut.NormalizeOs(input).Should().Be(expected);

    [Fact]
    public void NormalizeIp_rejects_garbage_and_accepts_valid()
    {
        _sut.NormalizeIp("10.0.0.5").Should().Be("10.0.0.5");
        _sut.NormalizeIp("999.1.1.1").Should().BeNull();
        _sut.NormalizeIp("127.0.0.1").Should().BeNull();
        _sut.NormalizeIp("169.254.10.20").Should().BeNull();
        _sut.NormalizeIp(null).Should().BeNull();
    }

    [Fact]
    public void Normalize_splits_fqdn_into_hostname_and_domain()
    {
        var asset = new DiscoveredAsset
        {
            Source = ConnectorType.ActiveDirectory,
            ExternalId = "guid-1",
            Hostname = "SRV-APP01.CORP.EXAMPLE.COM"
        };

        var result = _sut.Normalize(asset);

        result.Hostname.Should().Be("srv-app01");
        result.Domain.Should().Be("corp.example.com");
        result.Fqdn.Should().Be("srv-app01.corp.example.com");
        result.Identifiers[MatchAttributes.Hostname].Should().Be("srv-app01");
    }

    [Fact]
    public void Normalize_drops_placeholder_serial_numbers()
    {
        var asset = new DiscoveredAsset
        {
            Source = ConnectorType.Sccm,
            ExternalId = "1",
            SerialNumber = "To Be Filled By O.E.M."
        };

        _sut.Normalize(asset).SerialNumber.Should().BeNull();
    }

    [Fact]
    public void Normalize_applies_namespace_specific_identifier_rules()
    {
        var asset = new DiscoveredAsset
        {
            Source = ConnectorType.Azure,
            ExternalId = "azure-1",
            Identifiers =
            {
                [MatchAttributes.AzureResourceId] = " /SUBSCRIPTIONS/ABC/VM/ONE ",
                [MatchAttributes.SerialNumber] = " serial-01 "
            }
        };

        var result = _sut.Normalize(asset);

        result.Identifiers[MatchAttributes.AzureResourceId].Should().Be("/subscriptions/abc/vm/one");
        result.Identifiers[MatchAttributes.SerialNumber].Should().Be("SERIAL-01");
    }
}
