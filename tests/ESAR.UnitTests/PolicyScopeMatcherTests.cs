using Esar.Application.Compliance;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace Esar.UnitTests;

public class PolicyScopeMatcherTests
{
    [Fact]
    public void Empty_scope_matches_every_asset()
    {
        var policy = new CompliancePolicy();
        var asset = new Asset { AssetType = AssetType.Application, Environment = EnvironmentType.Test };
        PolicyScopeMatcher.Applies(policy, asset).Should().BeTrue();
    }

    [Fact]
    public void Min_criticality_gate_still_works_after_the_scope_matcher_refactor()
    {
        var policy = new CompliancePolicy { MinCriticality = CriticalityLevel.High };
        PolicyScopeMatcher.Applies(policy, new Asset { Criticality = CriticalityLevel.Critical }).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, new Asset { Criticality = CriticalityLevel.Medium }).Should().BeFalse();
    }

    [Fact]
    public void Connector_filter_matches_if_any_asset_source_is_listed()
    {
        var policy = new CompliancePolicy { AppliesToConnectorsJson = """["Azure","ActiveDirectory"]""" };
        var matching = new Asset
        {
            Sources = { new AssetSource { ConnectorType = ConnectorType.Azure, ExternalId = "a" } }
        };
        var nonMatching = new Asset
        {
            Sources = { new AssetSource { ConnectorType = ConnectorType.Qualys, ExternalId = "b" } }
        };
        PolicyScopeMatcher.Applies(policy, matching).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, nonMatching).Should().BeFalse();
    }

    [Fact]
    public void Tag_filter_without_a_value_matches_on_key_presence_only()
    {
        var policy = new CompliancePolicy { AppliesToTagsJson = """["env"]""" };
        var withTag = new Asset { Tags = { new AssetTag { Key = "env", Value = "anything" } } };
        var withoutTag = new Asset { Tags = { new AssetTag { Key = "owner", Value = "x" } } };
        PolicyScopeMatcher.Applies(policy, withTag).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, withoutTag).Should().BeFalse();
    }

    [Fact]
    public void Tag_filter_with_a_value_requires_an_exact_case_insensitive_match()
    {
        var policy = new CompliancePolicy { AppliesToTagsJson = """["env=Prod"]""" };
        var exact = new Asset { Tags = { new AssetTag { Key = "env", Value = "prod" } } };
        var wrongValue = new Asset { Tags = { new AssetTag { Key = "env", Value = "staging" } } };
        PolicyScopeMatcher.Applies(policy, exact).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, wrongValue).Should().BeFalse();
    }

    [Fact]
    public void AD_group_membership_is_a_tag_filter_under_the_adgroup_prefix()
    {
        // Mirrors how ActiveDirectoryConnector writes group membership: asset.Tags["adgroup:domain admins"] = "true".
        var policy = new CompliancePolicy { AppliesToTagsJson = """["adgroup:domain admins"]""" };
        var member = new Asset { Tags = { new AssetTag { Key = "adgroup:domain admins", Value = "true" } } };
        var nonMember = new Asset { Tags = { new AssetTag { Key = "adgroup:backup operators", Value = "true" } } };
        PolicyScopeMatcher.Applies(policy, member).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, nonMember).Should().BeFalse();
    }

    [Fact]
    public void Hostname_pattern_supports_glob_wildcards_case_insensitively()
    {
        var policy = new CompliancePolicy { AppliesToHostnamePatternsJson = """["prod-db-*"]""" };
        PolicyScopeMatcher.Applies(policy, new Asset { NormalizedHostname = "prod-db-01" }).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, new Asset { NormalizedHostname = "PROD-DB-02" }).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, new Asset { NormalizedHostname = "test-db-01" }).Should().BeFalse();
    }

    [Fact]
    public void Ip_range_filter_matches_a_cidr_range()
    {
        var policy = new CompliancePolicy { AppliesToIpRangesJson = """["10.0.0.0/8"]""" };
        var inside = new Asset { IpAddresses = { new AssetIp { IpAddress = "10.1.2.3" } } };
        var outside = new Asset { IpAddresses = { new AssetIp { IpAddress = "192.168.1.1" } } };
        PolicyScopeMatcher.Applies(policy, inside).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, outside).Should().BeFalse();
    }

    [Fact]
    public void Ip_range_filter_treats_a_bare_ip_as_a_slash_32_host_route()
    {
        var policy = new CompliancePolicy { AppliesToIpRangesJson = """["10.0.0.5"]""" };
        var exact = new Asset { IpAddresses = { new AssetIp { IpAddress = "10.0.0.5" } } };
        var neighbor = new Asset { IpAddresses = { new AssetIp { IpAddress = "10.0.0.6" } } };
        PolicyScopeMatcher.Applies(policy, exact).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, neighbor).Should().BeFalse();
    }

    [Fact]
    public void Ip_range_filter_does_not_cross_match_address_families()
    {
        var policy = new CompliancePolicy { AppliesToIpRangesJson = """["10.0.0.0/8"]""" };
        var ipv6 = new Asset { IpAddresses = { new AssetIp { IpAddress = "::1" } } };
        PolicyScopeMatcher.Applies(policy, ipv6).Should().BeFalse();
    }

    [Fact]
    public void Subscription_filter_matches_on_exact_id()
    {
        var policy = new CompliancePolicy { AppliesToSubscriptionsJson = """["sub-123"]""" };
        PolicyScopeMatcher.Applies(policy, new Asset { CloudSubscriptionId = "sub-123" }).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, new Asset { CloudSubscriptionId = "sub-999" }).Should().BeFalse();
    }

    [Fact]
    public void Multiple_dimensions_combine_with_AND_not_OR()
    {
        var policy = new CompliancePolicy
        {
            AppliesToAssetTypesJson = """["WindowsServer"]""",
            AppliesToTagsJson = """["env=prod"]"""
        };
        var satisfiesBoth = new Asset
        {
            AssetType = AssetType.WindowsServer,
            Tags = { new AssetTag { Key = "env", Value = "prod" } }
        };
        var satisfiesOnlyType = new Asset
        {
            AssetType = AssetType.WindowsServer,
            Tags = { new AssetTag { Key = "env", Value = "staging" } }
        };
        var satisfiesOnlyTag = new Asset
        {
            AssetType = AssetType.LinuxServer,
            Tags = { new AssetTag { Key = "env", Value = "prod" } }
        };
        PolicyScopeMatcher.Applies(policy, satisfiesBoth).Should().BeTrue();
        PolicyScopeMatcher.Applies(policy, satisfiesOnlyType).Should().BeFalse();
        PolicyScopeMatcher.Applies(policy, satisfiesOnlyTag).Should().BeFalse();
    }
}
