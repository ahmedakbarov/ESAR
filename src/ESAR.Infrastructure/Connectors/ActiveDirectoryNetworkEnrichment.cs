using System.Net;
using Esar.Application.Contracts;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Safe, source-local network enrichment helpers for the Active Directory connector.
/// DNS results and LDAP MAC attributes deliberately remain separate interfaces because
/// LDAP does not establish that a particular address belongs to a particular NIC.
/// </summary>
internal static class ActiveDirectoryNetworkEnrichment
{
    public static string GetBaseDnDomain(string baseDn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDn);

        var labels = baseDn.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(component => component.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(component => component[3..].Trim())
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .ToArray();

        if (labels.Length == 0)
            throw new InvalidOperationException("Connector setting 'baseDn' must contain one or more DC components.");

        return string.Join('.', labels).ToLowerInvariant();
    }

    public static bool IsHostWithinDomain(string? hostname, string baseDnDomain)
    {
        if (string.IsNullOrWhiteSpace(hostname) || string.IsNullOrWhiteSpace(baseDnDomain)) return false;

        var host = hostname.Trim().TrimEnd('.');
        var domain = baseDnDomain.Trim().TrimEnd('.');
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) return false;

        return host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ActiveDirectoryDnsTarget> GetDnsTargets(
        IEnumerable<DiscoveredAsset> assets,
        string baseDnDomain)
    {
        return assets
            .Where(asset => IsHostWithinDomain(asset.Fqdn, baseDnDomain))
            .Select(asset => new ActiveDirectoryDnsTarget(asset, asset.Fqdn!.Trim().TrimEnd('.')))
            .ToArray();
    }

    public static IReadOnlyList<string> NormalizeMacAddresses(IEnumerable<string?> values)
    {
        var macAddresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (TryNormalizeEui48(value, out var macAddress) && seen.Add(macAddress))
                macAddresses.Add(macAddress);
        }

        return macAddresses;
    }

    public static bool TryNormalizeEui48(string? value, out string macAddress)
    {
        macAddress = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var hex = new char[12];
        var count = 0;
        foreach (var character in value.Trim())
        {
            if (IsHex(character))
            {
                if (count == hex.Length) return false;
                hex[count++] = char.ToLowerInvariant(character);
                continue;
            }

            if (character is ':' or '-' or '.' || char.IsWhiteSpace(character)) continue;
            return false;
        }

        if (count != hex.Length) return false;
        var compact = new string(hex);
        if (compact is "000000000000" or "ffffffffffff") return false;

        macAddress = string.Join(':', Enumerable.Range(0, 6).Select(index => compact.Substring(index * 2, 2)));
        return true;
    }

    public static IReadOnlyList<IPAddress> FilterSafeDnsAddresses(IEnumerable<IPAddress> addresses)
    {
        var result = new List<IPAddress>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in addresses)
        {
            if (!IsSafeDnsAddress(address)) continue;

            var canonical = address.ToString();
            if (seen.Add(canonical)) result.Add(address);
        }

        return result;
    }

    public static int AppendMacOnlyInterfaces(DiscoveredAsset asset, IEnumerable<string?> values)
    {
        var added = 0;
        foreach (var macAddress in NormalizeMacAddresses(values))
        {
            var exists = asset.Interfaces.Any(networkInterface =>
                networkInterface.IpAddress is null &&
                string.Equals(networkInterface.MacAddress, macAddress, StringComparison.OrdinalIgnoreCase));
            if (exists) continue;

            asset.Interfaces.Add(new DiscoveredInterface { MacAddress = macAddress });
            added++;
        }

        return added;
    }

    public static int AppendDnsIpOnlyInterfaces(DiscoveredAsset asset, IEnumerable<IPAddress> addresses)
    {
        var added = 0;
        foreach (var address in FilterSafeDnsAddresses(addresses))
        {
            var canonical = address.ToString();
            var exists = asset.Interfaces.Any(networkInterface =>
                networkInterface.MacAddress is null &&
                string.Equals(networkInterface.IpAddress, canonical, StringComparison.OrdinalIgnoreCase));
            if (exists) continue;

            asset.Interfaces.Add(new DiscoveredInterface { IpAddress = canonical });
            added++;
        }

        return added;
    }

    private static bool IsSafeDnsAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.Equals(IPAddress.Broadcast) || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
            return false;

        var bytes = address.GetAddressBytes();
        return !(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes[0] is >= 224 and <= 239);
    }

    private static bool IsHex(char character) =>
        character is >= '0' and <= '9' ||
        character is >= 'a' and <= 'f' ||
        character is >= 'A' and <= 'F';
}

internal sealed record ActiveDirectoryDnsTarget(DiscoveredAsset Asset, string Hostname);
