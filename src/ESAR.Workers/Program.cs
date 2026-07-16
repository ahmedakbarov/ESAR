using Esar.Application;
using Esar.Infrastructure;
using Esar.Workers.Jobs;
using Esar.Workers.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.WithProperty("Application", "esar-workers")
    .CreateLogger();
builder.Services.AddSerilog();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Hangfire server backed by PostgreSQL (jobs survive restarts, scale horizontally).
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o =>
        o.UseNpgsqlConnection(builder.Configuration.GetConnectionString("Postgres"))));
builder.Services.AddHangfireServer(o =>
{
    o.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 5);
    o.Queues = new[] { "default", "discovery", "compliance" };
});

builder.Services.AddScoped<DiscoveryJobs>();
builder.Services.AddScoped<ComplianceJobs>();
builder.Services.AddScoped<MaintenanceJobs>();
builder.Services.AddScoped<NotificationJobs>();

builder.Services.AddHostedService<JobSchedulerService>();
builder.Services.AddHostedService<RabbitMqConsumerService>();

var host = builder.Build();

if (builder.Configuration.GetValue("Database:AutoMigrate", false))
    await host.Services.InitializeDatabaseAsync();

await host.RunAsync();
