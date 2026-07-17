using System.Text.Json;
using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;

namespace Esar.Application.Auditing;

public interface IAuditService
{
    Task LogAsync(AuditAction action, string? entityType = null, string? entityId = null, object? details = null,
        CancellationToken ct = default);
}

public class AuditService : IAuditService
{
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUserService _currentUser;

    public AuditService(IUnitOfWork uow, ICurrentUserService currentUser)
    {
        _uow = uow;
        _currentUser = currentUser;
    }

    public async Task LogAsync(AuditAction action, string? entityType = null, string? entityId = null,
        object? details = null, CancellationToken ct = default)
    {
        await _uow.AuditLogs.AddAsync(new AuditLog
        {
            UserId = _currentUser.UserId,
            UserName = _currentUser.UserName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            IpAddress = _currentUser.IpAddress
        }, ct);
        await _uow.SaveChangesAsync(ct);
    }
}
