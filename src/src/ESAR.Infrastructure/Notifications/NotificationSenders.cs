using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Esar.Infrastructure.Notifications;

public class SmtpOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; } = true;
    public string FromAddress { get; set; } = "esar@example.com";
    public string FromName { get; set; } = "ESAR Platform";
}

public class EmailNotificationSender : INotificationSender
{
    private readonly SmtpOptions _options;
    public EmailNotificationSender(IOptions<SmtpOptions> options) => _options = options.Value;
    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        using var client = new SmtpClient(_options.Host, _options.Port) { EnableSsl = _options.UseSsl };
        if (!string.IsNullOrEmpty(_options.Username))
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = notification.Subject,
            Body = notification.Body
        };
        foreach (var recipient in notification.Recipient.Split(';', StringSplitOptions.RemoveEmptyEntries))
            message.To.Add(recipient.Trim());
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>Posts an Adaptive-Card-style payload to a Teams incoming webhook (recipient = webhook URL).</summary>
public class TeamsNotificationSender : INotificationSender
{
    private readonly IHttpClientFactory _httpFactory;
    public TeamsNotificationSender(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;
    public NotificationChannel Channel => NotificationChannel.MicrosoftTeams;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        var payload = new
        {
            title = notification.Subject,
            text = notification.Body.Replace("\n", "<br/>")
        };
        var client = _httpFactory.CreateClient("notifications");
        var response = await client.PostAsync(notification.Recipient,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Posts to a Slack incoming webhook (recipient = webhook URL).</summary>
public class SlackNotificationSender : INotificationSender
{
    private readonly IHttpClientFactory _httpFactory;
    public SlackNotificationSender(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;
    public NotificationChannel Channel => NotificationChannel.Slack;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        var payload = new { text = $"*{notification.Subject}*\n{notification.Body}" };
        var client = _httpFactory.CreateClient("notifications");
        var response = await client.PostAsync(notification.Recipient,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Generic JSON webhook (recipient = target URL).</summary>
public class WebhookNotificationSender : INotificationSender
{
    private readonly IHttpClientFactory _httpFactory;
    public WebhookNotificationSender(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;
    public NotificationChannel Channel => NotificationChannel.Webhook;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        var payload = new
        {
            source = "esar",
            subject = notification.Subject,
            body = notification.Body,
            relatedEntityType = notification.RelatedEntityType,
            relatedEntityId = notification.RelatedEntityId,
            timestamp = DateTime.UtcNow
        };
        var client = _httpFactory.CreateClient("notifications");
        var response = await client.PostAsync(notification.Recipient,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
    }
}

public class SmsOptions
{
    /// <summary>HTTP SMS gateway endpoint, e.g. an internal SMPP-to-HTTP bridge.</summary>
    public string GatewayUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
}

public class SmsNotificationSender : INotificationSender
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SmsOptions _options;

    public SmsNotificationSender(IHttpClientFactory httpFactory, IOptions<SmsOptions> options)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
    }

    public NotificationChannel Channel => NotificationChannel.Sms;

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.GatewayUrl))
            throw new InvalidOperationException("SMS gateway is not configured.");
        var client = _httpFactory.CreateClient("notifications");
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.GatewayUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                to = notification.Recipient,
                message = $"{notification.Subject}: {notification.Body}"
            }), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_options.ApiKey)) request.Headers.Add("X-Api-Key", _options.ApiKey);
        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
