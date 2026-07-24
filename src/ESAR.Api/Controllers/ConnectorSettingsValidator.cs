using System.Text.RegularExpressions;
using Esar.Domain.Enums;

namespace Esar.Api.Controllers;

/// <summary>
/// Per-connector-type validation schema for connector settings. Mirrors the frontend form schema
/// so invalid input is rejected server-side too, with field-level messages. A value of "***"
/// means "keep the stored secret" and always satisfies a required rule.
/// </summary>
public static class ConnectorSettingsValidator
{
    private enum Kind { Text, Url, Host, Port, Number, Guid, Boolean }

    private sealed record Rule(string Key, bool Required, Kind Kind);

    private static readonly Rule[] AadCredentials =
    {
        new("tenantId", Required: true, Kind.Guid),
        new("clientId", Required: true, Kind.Guid),
        new("clientSecret", Required: true, Kind.Text),
    };

    private static readonly Dictionary<ConnectorType, Rule[]> Schemas = new()
    {
        [ConnectorType.ActiveDirectory] = new[]
        {
            new Rule("server", true, Kind.Host),
            new Rule("baseDn", true, Kind.Text),
            new Rule("username", true, Kind.Text),
            new Rule("password", true, Kind.Text),
            new Rule("port", false, Kind.Port),
            new Rule("useSsl", false, Kind.Boolean),
            new Rule("timeoutSeconds", false, Kind.Number),
            new Rule("resolveDns", false, Kind.Boolean),
            new Rule("dnsTimeoutSeconds", false, Kind.Number),
            new Rule("dnsMaxConcurrency", false, Kind.Number),
        },
        [ConnectorType.Azure] = AadCredentials,
        [ConnectorType.EntraId] = AadCredentials,
        [ConnectorType.Intune] = AadCredentials,
        [ConnectorType.MicrosoftDefender] = AadCredentials,
        [ConnectorType.VmwareVCenter] = new[]
        {
            new Rule("baseUrl", true, Kind.Url),
            new Rule("username", true, Kind.Text),
            new Rule("password", true, Kind.Text),
            new Rule("allowSelfSignedCert", false, Kind.Boolean),
        },
        [ConnectorType.CrowdStrike] = new[]
        {
            new Rule("baseUrl", true, Kind.Url),
            new Rule("clientId", true, Kind.Text),
            new Rule("clientSecret", true, Kind.Text),
        },
        [ConnectorType.SentinelOne] = new[]
        {
            new Rule("baseUrl", true, Kind.Url),
            new Rule("apiToken", true, Kind.Text),
        },
        [ConnectorType.CortexXdr] = new[]
        {
            new Rule("baseUrl", true, Kind.Url),
            new Rule("apiKeyId", true, Kind.Text),
            new Rule("apiKey", true, Kind.Text),
        },
        [ConnectorType.Tenable] = new[]
        {
            new Rule("accessKey", true, Kind.Text),
            new Rule("secretKey", true, Kind.Text),
        },
        [ConnectorType.Qualys] = new[]
        {
            new Rule("baseUrl", true, Kind.Url),
            new Rule("username", true, Kind.Text),
            new Rule("password", true, Kind.Text),
        },
        [ConnectorType.ServiceNowCmdb] = new[]
        {
            new Rule("instanceUrl", true, Kind.Url),
            new Rule("username", true, Kind.Text),
            new Rule("password", true, Kind.Text),
        },
        [ConnectorType.GenericRest] = new[]
        {
            new Rule("url", true, Kind.Url),
        },
    };

    private static readonly Regex HostPattern = new(
        @"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)*$|^(\d{1,3}\.){3}\d{1,3}$",
        RegexOptions.Compiled);

    /// <summary>Returns field → message for every violated rule; empty when the settings are valid.</summary>
    public static Dictionary<string, string> Validate(ConnectorType type, IReadOnlyDictionary<string, string> settings)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Schemas.TryGetValue(type, out var rules)) return errors; // no schema = free-form settings

        foreach (var rule in rules)
        {
            settings.TryGetValue(rule.Key, out var raw);
            var value = raw?.Trim() ?? string.Empty;

            if (value.Length == 0)
            {
                if (rule.Required) errors[rule.Key] = "This field is required.";
                continue;
            }
            if (value == "***") continue; // masked stored secret

            var error = rule.Kind switch
            {
                Kind.Url when !System.Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                              (uri.Scheme != "http" && uri.Scheme != "https")
                    => "Must be an absolute http(s) URL, e.g. https://host.example.com.",
                Kind.Host when value.Contains("://")
                    => "Enter a hostname or IP without a scheme, e.g. dc01.corp.local.",
                Kind.Host when !HostPattern.IsMatch(value)
                    => "Not a valid hostname or IPv4 address.",
                Kind.Port when !int.TryParse(value, out var port) || port is < 1 or > 65535
                    => "Port must be a number between 1 and 65535.",
                Kind.Number when !long.TryParse(value, out var n) || n < 0
                    => "Must be a non-negative number.",
                Kind.Guid when !System.Guid.TryParse(value, out _)
                    => "Must be a GUID, e.g. 00000000-0000-0000-0000-000000000000.",
                Kind.Boolean when !bool.TryParse(value, out _)
                    => "Must be true or false.",
                _ => null
            };
            if (error is not null) errors[rule.Key] = error;
        }
        return errors;
    }
}
