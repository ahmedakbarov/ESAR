using System.Net;
using System.Text.Json;
using Esar.Application.Auditing;
using Esar.Domain.Enums;
using FluentValidation;

namespace Esar.Api.Middleware;

/// <summary>Converts unhandled exceptions into RFC 7807 problem responses without leaking internals.</summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationException ex)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                title = "Validation failed",
                status = 400,
                errors = ex.Errors.Select(e => new { field = e.PropertyName, message = e.ErrorMessage })
            }));
        }
        catch (ArgumentException ex)
        {
            // Bad user input (e.g. an over-long regex search) → clean 400, not a 500.
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.1",
                title = "Invalid request",
                status = 400,
                detail = ex.Message
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
                title = "An unexpected error occurred",
                status = 500,
                traceId = context.TraceIdentifier
            }));
        }
    }
}

/// <summary>Writes an audit record for every authenticated mutating API call.</summary>
public class ApiAuditMiddleware
{
    private static readonly string[] MutatingMethods = { "POST", "PUT", "PATCH", "DELETE" };
    private readonly RequestDelegate _next;

    public ApiAuditMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IAuditService audit)
    {
        await _next(context);

        if (!MutatingMethods.Contains(context.Request.Method)) return;
        if (context.User.Identity?.IsAuthenticated != true) return;
        if (context.Request.Path.StartsWithSegments("/api/v1/auth")) return; // login handled separately

        try
        {
            await audit.LogAsync(AuditAction.ApiCall, "HttpRequest", context.Request.Path, new
            {
                context.Request.Method,
                Path = context.Request.Path.Value,
                context.Response.StatusCode
            });
        }
        catch
        {
            // Audit failures must never break the request pipeline.
        }
    }
}
