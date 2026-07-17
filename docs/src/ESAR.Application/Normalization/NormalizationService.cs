using System.Text.RegularExpressions;
using Esar.Application.Abstractions;
using Esar.Application.Contracts;
using Esar.Domain.Enums;

namespace Esar.Application.Normalization;

public class NormalizationService : INormalizationService
{
    private static readonly Regex MacSeparators = new("[^0-9a-fA-F]", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    public DiscoveredAsset Normalize(DiscoveredAsset asset)
    {
        asset.Hostname = string.IsNullOrWhiteSpace(asset.Hostname) ? asset.Hostname : NormalizeHostname(asset.Hostname);
        asset.Fqdn = asset.Fqdn?.Trim().ToLowerInvariant();
        asset.Domain = NormalizeDomain(asset.Domain);
        asset.OperatingSystem = NormalizeOs(asset.OperatingSystem);

        // If hostname arrived as an FQDN, split it.
        if (!string.IsNullOrEmpty(asset.Hostname) && asset.Hostname.Contains('.'))
        {
            asset.Fqdn ??= asset.Hostname;
            var parts = asset.Hostname.Split('.', 2);
            asset.Hostname = parts[0];
            asset.Domain ??= NormalizeDomain(parts[1]);
        }

        foreach (var iface in asset.Interfaces)
        {
            iface.IpAddress = NormalizeIp(iface.IpAddress);
            iface.MacAddress = NormalizeMac(iface.MacAddress);
        }
        asset.Interfaces.RemoveAll(i => i.IpAddress is null && i.MacAddress is null);

        asset.SerialNumber = NormalizeSerial(asset.SerialNumber);
        asset.BiosUuid = asset.BiosUuid?.Trim().ToLowerInvariant();
        asset.Manufacturer = NormalizeVendor(asset.Manufacturer);
        asset.CloudResourceId = asset.CloudResourceId?.Trim();
        asset.AssetType ??= InferAssetType(asset);

        // Populate canonical matching identifiers from normalized fields.
        SetIfMissing(asset, MatchAttributes.BiosUuid, asset.BiosUuid);
        SetIfMissing(asset, MatchAttributes.SerialNumber, asset.SerialNumber);
        SetIfMissing(asset, MatchAttributes.Hostname, string.IsNullOrEmpty(asset.Hostname) ? null : asset.Hostname);
        var mac = asset.Interfaces.FirstOrDefault(i => i.MacAddress != null)?.MacAddress;
        SetIfMissing(asset, MatchAttributes.MacAddress, mac);
        var ip = asset.Interfaces.FirstOrDefault(i => i.IpAddress != null)?.IpAddress;
        SetIfMissing(asset, MatchAttributes.IpAddress, ip);

        return asset;
    }

    public string NormalizeHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return string.Empty;
        var h = hostname.Trim().ToLowerInvariant();
        h = MultiSpace.Replace(h, "-");
        return h.TrimEnd('.');
    }

    public string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = MacSeparators.Replace(mac, string.Empty).ToLowerInvariant();
        if (hex.Length != 12) return null;
        // Ignore well-known virtual/invalid MACs that break matching.
        if (hex is "000000000000" or "ffffffffffff") return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    public string? NormalizeIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        return System.Net.IPAddress.TryParse(ip.Trim(), out var parsed) ? parsed.ToString() : null;
    }

    public string? NormalizeOs(string? os)
    {
        if (string.IsNullOrWhiteSpace(os)) return null;
        var o = MultiSpace.Replace(os.Trim(), " ");
        var lower = o.ToLowerInvariant();
        if (lower.Contains("windows server")) return CanonicalWindowsServer(lower);
        if (lower.Contains("windows 11")) return "Windows 11";
        if (lower.Contains("windows 10")) return "Windows 10";
        if (lower.Contains("red hat") || lower.Contains("rhel")) return "Red Hat Enterprise Linux";
        if (lower.Contains("ubuntu")) return "Ubuntu Linux";
        if (lower.Contains("centos")) return "CentOS Linux";
        if (lower.Contains("suse") || lower.Contains("sles")) return "SUSE Linux Enterprise";
        if (lower.Contains("debian")) return "Debian Linux";
        if (lower.Contains("amazon linux")) return "Amazon Linux";
        if (lower.Contains("oracle linux")) return "Oracle Linux";
        if (lower.Contains("esxi") || lower.Contains("vmware")) return "VMware ESXi";
        if (lower.Contains("mac os") || lower.Contains("macos")) return "macOS";
        return o;
    }

    public string? NormalizeDomain(string? domain)
        => string.IsNullOrWhiteSpace(domain) ? null : domain.Trim().ToLowerInvariant().TrimEnd('.');

    private static string CanonicalWindowsServer(string lower)
    {
        foreach (var year in new[] { "2025", "2022", "2019", "2016", "2012", "2008" })
            if (lower.Contains(year)) return $"Windows Server {year}";
        return "Windows Server";
    }

    private static string? NormalizeSerial(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return null;
        var s = serial.Trim().ToUpperInvariant();
        // Placeholder serials emitted by hypervisors/OEM tools must never hard-match.
        string[] junk = { "TO BE FILLED BY O.E.M.", "NONE", "N/A", "DEFAULT STRING", "SYSTEM SERIAL NUMBER", "0" };
        return junk.Contains(s) ? null : s;
    }

    private static string? NormalizeVendor(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor)) return null;
        var v = vendor.Trim();
        var lower = v.ToLowerInvariant();
        if (lower.Contains("dell")) return "Dell";
        if (lower.Contains("hewlett") || lower.StartsWith("hp")) return "HPE";
        if (lower.Contains("lenovo")) return "Lenovo";
        if (lower.Contains("vmware")) return "VMware";
        if (lower.Contains("microsoft")) return "Microsoft";
        if (lower.Contains("cisco")) return "Cisco";
        return v;
    }

    private static AssetType? InferAssetType(DiscoveredAsset a)
    {
        if (!string.IsNullOrEmpty(a.CloudResourceId)) return AssetType.CloudInstance;
        var os = a.OperatingSystem?.ToLowerInvariant() ?? string.Empty;
        if (os.Contains("windows server")) return AssetType.WindowsServer;
        if (os.Contains("windows")) return AssetType.Workstation;
        if (os.Contains("linux") || os.Contains("ubuntu") || os.Contains("centos")) return AssetType.LinuxServer;
        if (os.Contains("esxi")) return AssetType.PhysicalServer;
        return null;
    }

    private static void SetIfMissing(DiscoveredAsset asset, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !asset.Identifiers.ContainsKey(key))
            asset.Identifiers[key] = value!;
    }
}
