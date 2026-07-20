using System.DirectoryServices.Protocols;
using System.Text.RegularExpressions;
using Esar.Application.Abstractions;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Validated LDAP settings for the Docker-based Active Directory connector.
/// Password is deliberately not included in generated diagnostic strings.
/// </summary>
internal sealed class ActiveDirectoryConnectionOptions
{
    public required string Server { get; init; }
    public required int Port { get; init; }
    public required string BaseDn { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required bool UseSsl { get; init; }
    public required AuthType AuthType { get; init; }
    public required TimeSpan Timeout { get; init; }
    public required bool ResolveDns { get; init; }
    public required TimeSpan DnsTimeout { get; init; }
    public required int DnsMaxConcurrency { get; init; }
    public required IReadOnlyList<string> MacAttributes { get; init; }
    public required string BaseDnDomain { get; init; }

    public static ActiveDirectoryConnectionOptions Parse(ConnectorSettings settings)
    {
        var useSsl = ReadBoolean(settings, "useSsl", defaultValue: true);
        if (!useSsl)
            throw new InvalidOperationException(
                "Active Directory simple bind requires LDAPS. Set useSsl=true and use port 636.");

        var server = settings.Get("server").Trim();
        if (server.Contains("://", StringComparison.Ordinal))
            throw new InvalidOperationException("Use a DC hostname only in 'server' (without ldap:// or ldaps://).");

        var baseDn = settings.Get("baseDn").Trim();
        if (!baseDn.Contains("DC=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Connector setting 'baseDn' must contain a domain component such as DC=esar,DC=local.");

        return new ActiveDirectoryConnectionOptions
        {
            Server = server,
            Port = ReadPort(settings),
            BaseDn = baseDn,
            Username = settings.Get("username"),
            Password = settings.Get("password"),
            UseSsl = useSsl,
            AuthType = ReadAuthType(settings),
            Timeout = TimeSpan.FromSeconds(ReadTimeoutSeconds(settings)),
            ResolveDns = ReadBoolean(settings, "resolveDns", defaultValue: false),
            DnsTimeout = TimeSpan.FromSeconds(ReadDnsTimeoutSeconds(settings)),
            DnsMaxConcurrency = ReadDnsMaxConcurrency(settings),
            MacAttributes = ReadMacAttributes(settings),
            BaseDnDomain = ActiveDirectoryNetworkEnrichment.GetBaseDnDomain(baseDn)
        };
    }

    private static AuthType ReadAuthType(ConnectorSettings settings)
    {
        var value = settings.GetOptional("authType");
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("basic", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("simple", StringComparison.OrdinalIgnoreCase))
            return AuthType.Basic;

        if (value.Equals("negotiate", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "authType=Negotiate requires a domain-joined, Kerberos-configured host and is not supported by the ESAR Docker deployment. Use authType=Basic.");

        throw new InvalidOperationException("Connector setting 'authType' must be Basic.");
    }

    private static int ReadPort(ConnectorSettings settings)
    {
        var value = settings.GetOptional("port");
        if (string.IsNullOrWhiteSpace(value)) return 636;
        if (int.TryParse(value, out var port) && port is >= 1 and <= 65535) return port;
        throw new InvalidOperationException("Connector setting 'port' must be an integer from 1 to 65535.");
    }

    private static int ReadTimeoutSeconds(ConnectorSettings settings)
    {
        var value = settings.GetOptional("timeoutSeconds");
        if (string.IsNullOrWhiteSpace(value)) return 30;
        if (int.TryParse(value, out var seconds) && seconds is >= 5 and <= 300) return seconds;
        throw new InvalidOperationException("Connector setting 'timeoutSeconds' must be an integer from 5 to 300.");
    }

    private static int ReadDnsTimeoutSeconds(ConnectorSettings settings)
    {
        var value = settings.GetOptional("dnsTimeoutSeconds");
        if (string.IsNullOrWhiteSpace(value)) return 5;
        if (int.TryParse(value, out var seconds) && seconds is >= 1 and <= 30) return seconds;
        throw new InvalidOperationException("Connector setting 'dnsTimeoutSeconds' must be an integer from 1 to 30.");
    }

    private static int ReadDnsMaxConcurrency(ConnectorSettings settings)
    {
        var value = settings.GetOptional("dnsMaxConcurrency");
        if (string.IsNullOrWhiteSpace(value)) return 8;
        if (int.TryParse(value, out var concurrency) && concurrency is >= 1 and <= 32) return concurrency;
        throw new InvalidOperationException("Connector setting 'dnsMaxConcurrency' must be an integer from 1 to 32.");
    }

    private static IReadOnlyList<string> ReadMacAttributes(ConnectorSettings settings)
    {
        var value = settings.GetOptional("macAttributes");
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

        var attributes = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (attributes.Length == 0) return Array.Empty<string>();
        if (attributes.Length > 16)
            throw new InvalidOperationException("Connector setting 'macAttributes' may contain at most 16 LDAP attribute names.");

        foreach (var attribute in attributes)
        {
            if (!Regex.IsMatch(attribute, "^[A-Za-z][A-Za-z0-9-]{0,63}$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException(
                    "Each 'macAttributes' value must be an LDAP attribute name containing letters, digits, or hyphens.");

            if (attribute.Equals("networkAddress", StringComparison.OrdinalIgnoreCase) ||
                attribute.Equals("ipHostNumber", StringComparison.OrdinalIgnoreCase) ||
                attribute.Equals("netbootGUID", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"LDAP attribute '{attribute}' is not safe for MAC enrichment. Configure a dedicated text EUI-48 attribute instead.");
        }

        return attributes;
    }

    private static bool ReadBoolean(ConnectorSettings settings, string key, bool defaultValue)
    {
        var value = settings.GetOptional(key);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (bool.TryParse(value, out var result)) return result;
        throw new InvalidOperationException($"Connector setting '{key}' must be true or false.");
    }
}
