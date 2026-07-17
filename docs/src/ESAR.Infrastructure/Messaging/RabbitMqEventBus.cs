using System.Text;
using System.Text.Json;
using Esar.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Esar.Infrastructure.Messaging;

public class RabbitMqOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string Exchange { get; set; } = "esar.events";
    public string VirtualHost { get; set; } = "/";
}

/// <summary>Publishes domain events to a RabbitMQ topic exchange. Thread-safe, lazy connection.</summary>
public class RabbitMqEventBus : IEventBus, IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventBus> _logger;
    private readonly object _lock = new();
    private IConnection? _connection;

    public RabbitMqEventBus(IOptions<RabbitMqOptions> options, ILogger<RabbitMqEventBus> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task PublishAsync(string topic, object payload, CancellationToken ct = default)
    {
        try
        {
            var connection = GetConnection();
            using var channel = connection.CreateModel();
            channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            var props = channel.CreateBasicProperties();
            props.Persistent = true;
            props.ContentType = "application/json";
            props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            channel.BasicPublish(_options.Exchange, routingKey: topic, basicProperties: props, body: body);
        }
        catch (Exception ex)
        {
            // Event delivery is best-effort from the publisher's perspective;
            // consumers reconcile via scheduled jobs, so a publish failure is logged, not thrown.
            _logger.LogError(ex, "Failed to publish event {Topic}", topic);
        }
        return Task.CompletedTask;
    }

    private IConnection GetConnection()
    {
        if (_connection is { IsOpen: true }) return _connection;
        lock (_lock)
        {
            if (_connection is { IsOpen: true }) return _connection;
            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.Username,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true
            };
            _connection = factory.CreateConnection("esar-publisher");
            return _connection;
        }
    }

    public void Dispose() => _connection?.Dispose();
}

/// <summary>No-op bus for tests and single-node deployments without RabbitMQ.</summary>
public class NullEventBus : IEventBus
{
    public Task PublishAsync(string topic, object payload, CancellationToken ct = default) => Task.CompletedTask;
}
