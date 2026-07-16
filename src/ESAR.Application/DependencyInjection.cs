using Esar.Application.Abstractions;
using Esar.Application.Auditing;
using Esar.Application.Behaviors;
using Esar.Application.Compliance;
using Esar.Application.Incidents;
using Esar.Application.Ingestion;
using Esar.Application.Lifecycle;
using Esar.Application.Matching;
using Esar.Application.Merging;
using Esar.Application.Normalization;
using Esar.Application.Notifications;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Esar.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(DependencyInjection).Assembly));
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));

        services.AddSingleton<INormalizationService, NormalizationService>();
        services.AddScoped<IMatchingEngine, MatchingEngine>();
        services.AddScoped<ISourcePriorityEngine, SourcePriorityEngine>();
        services.AddScoped<IMergeEngine, MergeEngine>();
        services.AddScoped<IAssetIngestionService, AssetIngestionService>();
        services.AddScoped<IComplianceEngine, ComplianceEngine>();
        services.AddScoped<IIncidentService, IncidentService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ILifecycleService, LifecycleService>();
        return services;
    }
}
