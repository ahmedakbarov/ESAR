using System.Text;
using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Domain.Enums;
using Esar.Infrastructure.Messaging;
using Esar.Workers.Jobs;
using Hangfire;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Esar.Workers.Services;

/// <summary>
/// Registers/refreshes Hangfire recurring jobs: one discovery job per enabled connector
/// (from its cron), plus platform housekeeping jobs.
/// </summary>
public class JobSchedulerService : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly IRecurringJobManager _recurringJobs;
    private readonly ILogger<JobSchedulerService> _logger;

    public JobSchedulerService(IServiceProvider provider, IRecurringJobManager recurringJobs,
        ILogger<JobSchedulerService> logger)
    {
        _provider = provider;
        _recurringJobs = recurringJobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Platform jobs (static schedules).
        _recurringJobs.AddOrUpdate<ComplianceJobs>("compliance-evaluate-all",
            j => j.EvaluateAllAsync(CancellationToken.None), "0 */2 * * *");
        _recurringJobs.AddOrUpdate<MaintenanceJobs>("lifecycle-stale-assets",
            j => j.ProcessLifecycleAsync(CancellationToken.None), "30 * * * *");
        _recurringJobs.AddOrUpdate<MaintenanceJobs>("cleanup",
            j => j.CleanupAsync(CancellationToken.None), "0 3 * * *");
        _recurringJobs.AddOrUpdate<MaintenanceJobs>("incident-escalation",
            j => j.EscalateIncidentsAsync(CancellationToken.None), "*/15 * * * *");
        _recurringJobs.AddOrUpdate<MaintenanceJobs>("connector-health-monitor",
            j => j.MonitorConnectorHealthAsync(CancellationToken.None), "*/30 * * * *");
        _recurringJobs.AddOrUpdate<NotificationJobs>("notification-dispatch",
            j => j.DispatchPendingAsync(CancellationToken.None), "* * * * *");
        _recurringJobs.AddOrUpdate<AssetScoringJobs>("asset-scoring",
            j => j.RecalculateAllAsync(CancellationToken.None), "15 */6 * * *");
        _recurringJobs.AddOrUpdate<AssetScoringJobs>("duplicate-ip-detection",
            j => j.DetectDuplicateIpsAsync(CancellationToken.None), "45 */12 * * *");

        // Connector jobs follow DB configuration; refresh periodically so edits apply without restart.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncConnectorSchedulesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync connector schedules");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task SyncConnectorSchedulesAsync(CancellationToken ct)
    {
        using var scope = _provider.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var connectors = await uow.Connectors.ListAsync(null, ct);
        foreach (var connector in connectors)
        {
            var jobId = $"connector-{connector.Id:N}";
            if (connector.Enabled && !string.IsNullOrWhiteSpace(connector.CronSchedule))
            {
                var id = connector.Id;
                _recurringJobs.AddOrUpdate<DiscoveryJobs>(jobId,
                    j => j.RunConnectorAsync(id, CancellationToken.None), connector.CronSchedule);
            }
            else
            {
                _recurringJobs.RemoveIfExists(jobId);
            }
        }
    }
}

/// <summary>
/// Consumes control/events from RabbitMQ:
///  - esar.connector.run → enqueues an on-demand discovery job
///  - esar.notification.queued → immediate dispatch
///  - esar.asset.* → compliance re-evaluation for the affected asset
/// </summary>
public class RabbitMqConsumerService : BackgroundService
{
    private readonly IServiceProvider _provider;
    private readonly RabbitMqOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RabbitMqConsumerService> _logger;
    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqConsumerService(IServiceProvider provider, IOptions<RabbitMqOptions> options,
        IConfiguration configuration, ILogger<RabbitMqConsumerService> logger)
    {
        _provider = provider;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration["RabbitMq:Host"]))
        {
            _logger.LogInformation("RabbitMQ not configured — consumer disabled");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Connect();
                _logger.LogInformation("RabbitMQ consumer connected to {Host}", _options.Host);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RabbitMQ consumer error — reconnecting in 15s");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private void Connect()
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.Username,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost,
            DispatchConsumersAsync = true,
            AutomaticRecoveryEnabled = true
        };
        _connection = factory.CreateConnection("esar-workers");
        _channel = _connection.CreateModel();
        _channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true);

        // Dead-letter infrastructure: rejected messages land in esar.dead-letter for inspection/replay.
        _channel.ExchangeDeclare("esar.dlx", ExchangeType.Fanout, durable: true);
        _channel.QueueDeclare("esar.dead-letter", durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind("esar.dead-letter", "esar.dlx", string.Empty);

        var queueArgs = new Dictionary<string, object> { ["x-dead-letter-exchange"] = "esar.dlx" };
        var queue = _channel.QueueDeclare("esar.workers", durable: true, exclusive: false, autoDelete: false,
            arguments: queueArgs).QueueName;
        _channel.QueueBind(queue, _options.Exchange, "esar.connector.run");
        _channel.QueueBind(queue, _options.Exchange, EventTopics.NotificationQueued);
        _channel.BasicQos(0, 20, false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += async (_, ea) =>
        {
            try
            {
                await HandleMessageAsync(ea.RoutingKey, Encoding.UTF8.GetString(ea.Body.ToArray()));
                _channel!.BasicAck(ea.DeliveryTag, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed handling message {RoutingKey}", ea.RoutingKey);
                _channel!.BasicNack(ea.DeliveryTag, false, requeue: false);
            }
        };
        _channel.BasicConsume(queue, autoAck: false, consumer);
    }

    private async Task HandleMessageAsync(string routingKey, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        switch (routingKey)
        {
            case "esar.connector.run":
            {
                var connectorId = root.GetProperty("ConnectorId").GetGuid();
                SyncMode? mode = root.TryGetProperty("Mode", out var m) && m.ValueKind == JsonValueKind.String &&
                                 Enum.TryParse<SyncMode>(m.GetString(), true, out var parsed) ? parsed : null;
                var triggeredBy = root.TryGetProperty("TriggeredBy", out var t) ? t.GetString() ?? "api" : "api";
                BackgroundJob.Enqueue<DiscoveryJobs>(j => j.RunConnectorAsync(connectorId, CancellationToken.None));
                _logger.LogInformation("Enqueued on-demand run for connector {Id} (mode {Mode}, by {By})",
                    connectorId, mode, triggeredBy);
                break;
            }
            case EventTopics.NotificationQueued:
            {
                using var scope = _provider.CreateScope();
                var jobs = scope.ServiceProvider.GetRequiredService<NotificationJobs>();
                await jobs.DispatchPendingAsync(CancellationToken.None);
                break;
            }
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
