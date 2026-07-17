# ESAR — Developer Guide

## Environment
.NET 8 SDK, Node 20, Docker (for integration tests and local Postgres/Redis/RabbitMQ).

```bash
dotnet build ESAR.sln
dotnet test tests/ESAR.UnitTests
dotnet test tests/ESAR.IntegrationTests     # spins up postgres:16 via Testcontainers
cd src/frontend && npm install && npm run dev
```

## Architecture rules
- **Domain** has no dependencies. **Application** depends only on Domain (business logic + abstractions).
- **Infrastructure** implements Application abstractions (EF Core, Redis, RabbitMQ, connectors).
- **Api/Workers** wire everything via `AddApplication()` + `AddInfrastructure(configuration)`.
- Data access goes through `IUnitOfWork`/repositories; read-heavy dashboards use `IDashboardQueries`.
- Asset write/read use-cases are MediatR commands/queries with FluentValidation + logging behaviors.
- Cross-service integration is event-driven — publish to `IEventBus` using `EventTopics` constants.

## Adding a connector

1. Implement `IConnector` (subclass `RestConnectorBase` for HTTP APIs — it provides retry,
   rate limiting and JSON helpers; see `SecurityToolConnectors.cs` for reference):

```csharp
public class FooConnector : RestConnectorBase
{
    public FooConnector(IHttpClientFactory f, ILogger<FooConnector> l) : base(f, l) { }
    public override ConnectorType Type => ConnectorType.Foo;   // add enum member

    public override async Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings s, CancellationToken ct)
        => /* cheap auth/permission probe */;

    public override async IAsyncEnumerable<DiscoveredAsset> DiscoverAsync(ConnectorSettings s,
        SyncContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // authenticate, then page through the API (honor ctx.Mode / ctx.LastSuccessfulSyncAt
        // for incremental sync and call RateLimitAsync(ctx, ct) between pages)
        yield return new DiscoveredAsset
        {
            Source = Type,
            ExternalId = "...",              // REQUIRED, stable per source
            Hostname = "...",
            Identifiers = { [MatchAttributes.SerialNumber] = "..." },  // hard-match keys
            Tags = { ["monitoring_agent"] = "true" },                  // compliance evidence
            RawJson = raw
        };
    }
}
```

2. Register in `Infrastructure/DependencyInjection.cs`: `services.AddScoped<IConnector, FooConnector>();`
3. Add a `SourcePriority` seed row and document settings in the Administration guide.
4. Everything else (scheduling, job bookkeeping, matching, merging, incidents on failure) is
   handled by `ConnectorRunner`.

### Well-known enrichment tags
`antivirus`, `monitoring_agent`, `backup_agent`, `disk_encryption`, `patch_status`
(`up_to_date`/`compliant`), `internet_facing`, `public_ip` — these feed the compliance, health and
risk engines.

## Adding a compliance control
1. Add the `ControlType` enum member.
2. Add an evaluator case in `ComplianceEngine.EvaluateControl`.
3. Include the control in the relevant policies (portal/API — no code change needed for scoping).

## Adding an event
Add the topic to `EventTopics`, publish via `IEventBus`, document it in `docs/architecture.md` §5.
Consumers bind additional routing keys in `RabbitMqConsumerService.Connect`.

## Testing conventions
- Unit tests: Moq + FluentAssertions; engines are tested against `IUnitOfWork` mocks
  (`MatchingEngineTests`, `ComplianceEngineTests`, `PolicyAndScoringTests`).
- Integration tests: `WebApplicationFactory<Program>` + Testcontainers PostgreSQL; the seeded
  admin password comes from `ESAR_ADMIN_INITIAL_PASSWORD`.

## Database changes
Dev relies on `EnsureCreated` (`Database:AutoMigrate=true`). For production, mirror every model
change in an incremental script under `db/` (see `003_v2_features.sql` for the pattern) — or
switch the team to EF migrations (`dotnet ef migrations add ...`) if preferred.
