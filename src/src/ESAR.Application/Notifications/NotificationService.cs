using System.Text.RegularExpressions;
using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Esar.Application.Notifications;

public interface INotificationService
{
    /// <summary>Queues a notification rendered from a named template.</summary>
    Task QueueFromTemplateAsync(string templateName, IDictionary<string, string> model,
        string? recipientOverride = null, string? relatedType = null, string? relatedId = null,
        CancellationToken ct = default);
    /// <summary>Queues a raw notification.</summary>
    Task QueueAsync(NotificationChannel channel, string recipient, string subject, string body,
        string? relatedType = null, string? relatedId = null, CancellationToken ct = default);
    /// <summary>Sends one pending notification via the matching channel transport.</summary>
    Task<bool> DispatchAsync(Notification notification, CancellationToken ct = default);
}

public class NotificationService : INotificationService
{
    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);
    private readonly IUnitOfWork _uow;
    private readonly IEventBus _events;
    private readonly IEnumerable<INotificationSender> _senders;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(IUnitOfWork uow, IEventBus events, IEnumerable<INotificationSender> senders,
        ILogger<NotificationService> logger)
    {
        _uow = uow;
        _events = events;
        _senders = senders;
        _logger = logger;
    }

    public async Task QueueFromTemplateAsync(string templateName, IDictionary<string, string> model,
        string? recipientOverride = null, string? relatedType = null, string? relatedId = null,
        CancellationToken ct = default)
    {
        var template = await _uow.NotificationTemplates.FirstOrDefaultAsync(
            t => t.Name == templateName && t.Enabled, ct);
        if (template is null)
        {
            _logger.LogWarning("Notification template '{Template}' not found or disabled", templateName);
            return;
        }

        var recipient = recipientOverride;
        if (string.IsNullOrWhiteSpace(recipient) && model.TryGetValue("recipient", out var fromModel))
            recipient = fromModel;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            var fallback = await _uow.Settings.FirstOrDefaultAsync(s => s.Key == "notifications.defaultRecipient", ct);
            recipient = fallback?.Value ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(recipient)) return;

        await QueueAsync(template.Channel, recipient,
            Render(template.SubjectTemplate, model), Render(template.BodyTemplate, model), relatedType, relatedId, ct);
    }

    public async Task QueueAsync(NotificationChannel channel, string recipient, string subject, string body,
        string? relatedType = null, string? relatedId = null, CancellationToken ct = default)
    {
        var notification = new Notification
        {
            Channel = channel,
            Recipient = recipient,
            Subject = subject,
            Body = body,
            RelatedEntityType = relatedType,
            RelatedEntityId = relatedId
        };
        await _uow.Notifications.AddAsync(notification, ct);
        await _uow.SaveChangesAsync(ct);
        await _events.PublishAsync(EventTopics.NotificationQueued, new { NotificationId = notification.Id }, ct);
    }

    public async Task<bool> DispatchAsync(Notification notification, CancellationToken ct = default)
    {
        var sender = _senders.FirstOrDefault(s => s.Channel == notification.Channel);
        if (sender is null)
        {
            notification.Status = NotificationStatus.Failed;
            notification.Error = $"No transport registered for channel {notification.Channel}";
            _uow.Notifications.Update(notification);
            await _uow.SaveChangesAsync(ct);
            return false;
        }

        try
        {
            await sender.SendAsync(notification, ct);
            notification.Status = NotificationStatus.Sent;
            notification.SentAt = DateTime.UtcNow;
            notification.Error = null;
        }
        catch (Exception ex)
        {
            notification.RetryCount++;
            notification.Status = notification.RetryCount >= 5 ? NotificationStatus.Failed : NotificationStatus.Retrying;
            notification.Error = ex.Message;
            _logger.LogError(ex, "Notification {Id} via {Channel} failed (attempt {Attempt})",
                notification.Id, notification.Channel, notification.RetryCount);
        }

        _uow.Notifications.Update(notification);
        await _uow.SaveChangesAsync(ct);
        return notification.Status == NotificationStatus.Sent;
    }

    private static string Render(string template, IDictionary<string, string> model)
        => Placeholder.Replace(template, m => model.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}
