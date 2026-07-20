using System.DirectoryServices.Protocols;
using Esar.Application.Abstractions;
using Esar.Infrastructure.Connectors;
using FluentAssertions;
using Xunit;

namespace Esar.UnitTests;

public class ActiveDirectoryConnectionOptionsTests
{
    [Fact]
    public void Parse_uses_secure_simple_bind_defaults()
    {
        var options = ActiveDirectoryConnectionOptions.Parse(Settings());

        options.Server.Should().Be("dc01.esar.local");
        options.Port.Should().Be(636);
        options.BaseDn.Should().Be("DC=esar,DC=local");
        options.UseSsl.Should().BeTrue();
        options.AuthType.Should().Be(AuthType.Basic);
        options.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Parse_accepts_simple_alias_and_explicit_timeout()
    {
        var options = ActiveDirectoryConnectionOptions.Parse(Settings(
            ("authType", "simple"), ("port", "1636"), ("timeoutSeconds", "45")));

        options.AuthType.Should().Be(AuthType.Basic);
        options.Port.Should().Be(1636);
        options.Timeout.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void Parse_rejects_invalid_port(string port)
    {
        var action = () => ActiveDirectoryConnectionOptions.Parse(Settings(("port", port)));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*'port' must be an integer from 1 to 65535.*");
    }

    [Theory]
    [InlineData("false")]
    [InlineData("not-a-boolean")]
    public void Parse_rejects_non_ldaps_transport(string useSsl)
    {
        var action = () => ActiveDirectoryConnectionOptions.Parse(Settings(("useSsl", useSsl)));

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Negotiate")]
    [InlineData("Digest")]
    public void Parse_rejects_unsupported_authentication_modes(string authType)
    {
        var action = () => ActiveDirectoryConnectionOptions.Parse(Settings(("authType", authType)));

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("ldap://dc01.esar.local", "DC=esar,DC=local")]
    [InlineData("dc01.esar.local", "OU=Servers")]
    public void Parse_rejects_unsafe_server_or_base_dn(string server, string baseDn)
    {
        var action = () => ActiveDirectoryConnectionOptions.Parse(Settings(
            ("server", server), ("baseDn", baseDn), ("password", "OnlyForThisTest!")));

        action.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().NotContain("OnlyForThisTest!");
    }

    [Theory]
    [InlineData("4")]
    [InlineData("301")]
    [InlineData("bad")]
    public void Parse_rejects_invalid_timeout(string timeoutSeconds)
    {
        var action = () => ActiveDirectoryConnectionOptions.Parse(Settings(("timeoutSeconds", timeoutSeconds)));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*'timeoutSeconds' must be an integer from 5 to 300.*");
    }

    private static ConnectorSettings Settings(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["server"] = "  dc01.esar.local  ",
            ["baseDn"] = "  DC=esar,DC=local  ",
            ["username"] = "svc_esar_ad@esar.local",
            ["password"] = "OnlyForThisTest!"
        };

        foreach (var (key, value) in overrides) values[key] = value;
        return new ConnectorSettings { Values = values };
    }
}
