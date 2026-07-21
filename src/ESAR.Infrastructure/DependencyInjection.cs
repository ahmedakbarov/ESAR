using Esar.Application.Abstractions;
using Esar.Infrastructure.Caching;
using Esar.Infrastructure.Connectors;
using Esar.Infrastructure.Messaging;
using Esar.Infrastructure.Notifications;
using Esar.Infrastructure.Persistence;
using Esar.Infrastructure.Queries;
using Esar.Infrastructure.Reporting;
using Esar.Infrastructure.Security;
using Esar.Infrastructure.Ticketing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Esar.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // --- Persistence ---
        services.AddDbContext<EsarDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("Postgres"),
                npgsql => npgsql.EnableRetryOnFailure(3)));
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IDashboardQueries, DashboardQueries>();

        // --- Caching: Redis with in-memory fallback for dev/test ---
        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(redisConnection + ",abortConnect=false"));
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        else
        {
            services.AddSingleton<ICacheService, MemoryCacheService>();
        }

        // --- Messaging: RabbitMQ with null fallback ---
        if (configuration.GetSection("RabbitMq").Exists() &&
            !string.IsNullOrWhiteSpace(configuration["RabbitMq:Host"]))
        {
            services.Configure<RabbitMqOptions>(configuration.GetSection("RabbitMq"));
            services.AddSingleton<IEventBus, RabbitMqEventBus>();
        }
        else
        {
            services.AddSingleton<IEventBus, NullEventBus>();
        }

        // --- Security ---
        services.Configure<JwtOptions>(configuration.GetSection("Jwt"));
        services.Configure<SecretProtectorOptions>(configuration.GetSection("Security"));
        services.AddSingleton<IJwtTokenService, JwtTokenService>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ISecretProtector, AesSecretProtector>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        // --- Notifications & ticketing ---
        services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("ticketing", c => c.Timeout = TimeSpan.FromSeconds(30));
        services.Configure<SmtpOptions>(configuration.GetSection("Smtp"));
        services.Configure<SmsOptions>(configuration.GetSection("Sms"));
        services.Configure<ServiceNowOptions>(configuration.GetSection("ServiceNow"));
        services.Configure<JiraOptions>(configuration.GetSection("Jira"));
        services.AddScoped<INotificationSender, EmailNotificationSender>();
        services.AddScoped<INotificationSender, TeamsNotificationSender>();
        services.AddScoped<INotificationSender, SlackNotificationSender>();
        services.AddScoped<INotificationSender, WebhookNotificationSender>();
        services.AddScoped<INotificationSender, SmsNotificationSender>();
        services.AddScoped<ITicketingClient, ServiceNowTicketingClient>();
        services.AddScoped<ITicketingClient, JiraTicketingClient>();

        // --- Reporting ---
        services.AddScoped<IReportGenerator, ReportGenerator>();

        // --- Connector framework ---
        services.AddHttpClient(); // per-connector named clients resolved from the default factory
        services.AddScoped<IConnector, AzureConnector>();
        services.AddScoped<IConnector, EntraIdConnector>();
        services.AddScoped<IConnector, IntuneConnector>();
        services.AddScoped<IConnector, MicrosoftDefenderConnector>();
        services.AddScoped<IConnector, ActiveDirectoryConnector>();
        services.AddScoped<IConnector, VmwareVCenterConnector>();
        services.AddScoped<IConnector, CrowdStrikeConnector>();
        services.AddScoped<IConnector, SentinelOneConnector>();
        services.AddScoped<IConnector, CortexXdrConnector>();
        services.AddScoped<IConnector, TenableConnector>();
        services.AddScoped<IConnector, QualysConnector>();
        services.AddScoped<IConnector, ServiceNowCmdbConnector>();
        services.AddScoped<IConnector, GenericRestConnector>();
        services.AddScoped<IConnectorFactory, ConnectorFactory>();
        services.AddScoped<IConnectorRunner, ConnectorRunner>();

        return services;
    }

    /// <summary>Applies pending schema and seeds reference data. Call on startup.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider provider, CancellationToken ct = default)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EsarDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()
            .CreateLogger("DbInit");
        await db.Database.EnsureCreatedAsync(ct);
        await DbSeeder.SeedAsync(db, hasher, logger, ct);
    }
}
