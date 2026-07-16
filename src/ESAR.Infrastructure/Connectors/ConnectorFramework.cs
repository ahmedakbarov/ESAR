using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Connectors;

/// <summary>
/// Base class for HTTP-based connectors: token-bucket rate limiting,
/// exponential-backoff retry and JSON helpers.
/// </summary>
public abstract class RestConnectorBase : IConnector
{
    protected readonly IHttpClientFactory HttpFactory;
    protected readonly ILogger Logger;

    protected RestConnectorBase(IHttpClientFactory httpFactory, ILogger logger)
    {
        HttpFactory = httpFactory;
        Logger = logger;
    }

    public abstract ConnectorType Type { get; }
    public abstract Task<ConnectorHealth> CheckHealthAsync(ConnectorSettings settings, CancellationToken ct = default);
    public abstract IAsyncEnumerable<Application.Contracts.DiscoveredAsset> DiscoverAsync(ConnectorSettings settings,
        SyncContext context, CancellationToken ct = default);

    protected HttpClient CreateClient() => HttpFactory.CreateClient($"connector:{Type}");

    /// <summary>Sends a request with retry on 429/5xx/transport errors (exponential backoff + Retry-After).</summary>
    protected async Task<HttpResponseMessage> SendWithRetryAsync(HttpClient client,
        Func<HttpRequestMessage> requestFactory, int maxRetries = 3, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode) return response;

                var retryable = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
                if (!retryable || attempt >= maxRetries)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    response.Dispose();
                    throw new HttpRequestException(
                        $"{Type} connector request failed: {(int)response.StatusCode} {body[..Math.Min(body.Length, 500)]}");
                }

                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                response.Dispose();
                Logger.LogWarning("{Type} connector got retryable status, retrying in {Delay}s (attempt {Attempt})",
                    Type, delay.TotalSeconds, attempt + 1);
                await Task.Delay(delay, ct);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                response?.Dispose();
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                Logger.LogWarning("{Type} connector transport error, retrying in {Delay}s", Type, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>Simple client-side rate limiter honoring the configured requests/minute.</summary>
    protected static async Task RateLimitAsync(SyncContext context, CancellationToken ct)
    {
        if (context.RateLimitPerMinute <= 0) return;
        var delayMs = 60_000 / context.RateLimitPerMinute;
        await Task.Delay(delayMs, ct);
    }

    protected static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}

/// <summary>Resolves connector implementations registered in DI by their <see cref="ConnectorType"/>.</summary>
public class ConnectorFactory : IConnectorFactory
{
    private readonly Dictionary<ConnectorType, IConnector> _map;

    public ConnectorFactory(IEnumerable<IConnector> connectors)
        => _map = connectors.ToDictionary(c => c.Type);

    public IReadOnlyCollection<ConnectorType> SupportedTypes => _map.Keys;

    public IConnector Resolve(ConnectorType type)
        => _map.TryGetValue(type, out var connector)
            ? connector
            : throw new NotSupportedException($"No connector implementation registered for {type}.");
}
