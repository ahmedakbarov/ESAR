using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Esar.IntegrationTests;

/// <summary>
/// End-to-end API tests against a real PostgreSQL (Testcontainers).
/// Requires a local Docker daemon (present on GitHub Actions ubuntu runners).
/// </summary>
public class ApiFixture : IAsyncLifetime
{
    private const string AdminPassword = "Integration-Test-Passw0rd!";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("esar_test")
        .WithUsername("esar")
        .WithPassword("esar_test_pw")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Environment.SetEnvironmentVariable("ESAR_ADMIN_INITIAL_PASSWORD", AdminPassword);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("ConnectionStrings:Redis", "");
            builder.UseSetting("RabbitMq:Host", "");
            builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-with-32+chars!!");
            builder.UseSetting("Security:EncryptionKey",
                Convert.ToBase64String(new byte[32])); // deterministic test key
            builder.UseSetting("Database:AutoMigrate", "true");
        });
        Client = Factory.CreateClient();

        // Authenticate once as the seeded admin.
        var login = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "admin", password = AdminPassword });
        login.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("token").GetString();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public class ApiIntegrationTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;
    public ApiIntegrationTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        using var anonymous = _fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync("/api/v1/assets");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_endpoints_are_public()
    {
        using var anonymous = _fixture.Factory.CreateClient();
        (await anonymous.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Asset_crud_and_search_roundtrip()
    {
        // Create
        var create = await _fixture.Client.PostAsJsonAsync("/api/v1/assets", new
        {
            hostname = "int-test-srv01",
            fqdn = "int-test-srv01.corp.local",
            operatingSystem = "Windows Server 2022",
            assetType = "WindowsServer",
            environment = "Test",
            criticality = "High",
            ownerName = "Integration Owner",
            businessUnit = "IT",
            ipAddresses = new[] { "10.99.0.10" }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();

        // Read
        var get = await _fixture.Client.GetAsync($"/api/v1/assets/{id}");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        using var detail = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        detail.RootElement.GetProperty("hostname").GetString().Should().Be("int-test-srv01");
        detail.RootElement.GetProperty("ipAddresses").GetArrayLength().Should().Be(1);

        // Search
        var search = await _fixture.Client.GetAsync("/api/v1/assets?search=int-test-srv01");
        using var results = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        results.RootElement.GetProperty("totalCount").GetInt64().Should().BeGreaterThan(0);

        // Update
        var update = await _fixture.Client.PutAsJsonAsync($"/api/v1/assets/{id}",
            new { id, department = "SOC", criticality = "Critical" });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        // History captured the change
        var history = await _fixture.Client.GetAsync($"/api/v1/assets/{id}/history");
        (await history.Content.ReadAsStringAsync()).Should().Contain("Department");

        // Soft delete
        (await _fixture.Client.DeleteAsync($"/api/v1/assets/{id}")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Bulk_import_deduplicates_same_host_via_matching_pipeline()
    {
        var payload = new
        {
            assets = new[]
            {
                new
                {
                    hostname = "bulk-dup-01", fqdn = (string?)null,
                    operatingSystem = "Ubuntu 22.04", assetType = "LinuxServer",
                    environment = "Production", criticality = "Medium",
                    ownerName = (string?)null, department = (string?)null,
                    businessUnit = (string?)null, location = (string?)null,
                    classification = (string?)null,
                    ipAddresses = new[] { "10.99.1.1" }
                }
            }
        };

        var first = await _fixture.Client.PostAsJsonAsync("/api/v1/assets/bulk", payload);
        first.EnsureSuccessStatusCode();
        var second = await _fixture.Client.PostAsJsonAsync("/api/v1/assets/bulk", payload);
        second.EnsureSuccessStatusCode();

        var search = await _fixture.Client.GetAsync("/api/v1/assets?search=bulk-dup-01");
        using var results = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        results.RootElement.GetProperty("totalCount").GetInt64()
            .Should().Be(1, "the second import must match the existing golden record, not duplicate it");
    }

    [Fact]
    public async Task Dashboard_summary_returns_counters()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/dashboard/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("totalAssets", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Matching_rules_are_seeded_and_readable()
    {
        var response = await _fixture.Client.GetAsync("/api/v1/matching/rules");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("AzureResourceId").And.Contain("SerialNumber");
    }

    [Fact]
    public async Task Swagger_document_is_served()
    {
        using var anonymous = _fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync("/swagger/v1/swagger.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Enterprise Security Asset Registry");
    }
}
