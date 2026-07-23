using System.Text.Json;
using Esar.Application.Abstractions;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Decrypts a ConnectorConfig's stored SettingsJson ("enc:"-prefixed values are AES-encrypted at
/// rest) into a usable ConnectorSettings. Shared by ConnectorRunner, ConnectorsController and
/// LdapLoginService — previously duplicated identically in each.
/// </summary>
public static class ConnectorSettingsCodec
{
    public static ConnectorSettings Decrypt(string settingsJson, ISecretProtector secrets)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(settingsJson) ?? new();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in raw)
            values[key] = value.StartsWith("enc:") ? secrets.Unprotect(value) : value;
        return new ConnectorSettings { Values = values };
    }
}
