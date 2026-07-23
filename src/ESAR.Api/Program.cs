using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Esar.Api.Middleware;
using Esar.Application;
using Esar.Application.Auth;
using Esar.Infrastructure;
using Esar.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ---------- Logging (Serilog, structured) ----------
builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "esar-api"));

// ---------- Services ----------
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<IAuthService, AuthService>();

builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.AssumeDefaultVersionWhenUnspecified = true;
    o.ReportApiVersions = true;
    o.ApiVersionReader = new UrlSegmentApiVersionReader();
}).AddApiExplorer(o =>
{
    o.GroupNameFormat = "'v'VVV";
    o.SubstituteApiVersionInUrl = true;
});

// ---------- Authentication: ESAR-issued JWT only ----------
// Entra ID (Azure AD SSO) and AD (LDAP) login are handled by AuthController's token-exchange
// endpoints (/auth/login/entra, /auth/login/ldap) — the browser authenticates against the
// external IdP once, then gets back a normal ESAR "Local"-scheme JWT identical to a password
// login's, with roles/permissions already baked in. There is deliberately no second bearer
// scheme here: an Entra ID-issued token accepted directly would carry Microsoft's claims, not
// ESAR's DB-driven "permission" claims, so it could authenticate a request but never authorize one.
var jwtKey = builder.Configuration["Jwt:SigningKey"] ?? string.Empty;

builder.Services.AddAuthentication(o => o.DefaultScheme = "Local")
    .AddJwtBearer("Local", o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "esar",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "esar-clients",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder("Local")
        .RequireAuthenticatedUser().Build();
    options.FallbackPolicy = options.DefaultPolicy;

    string[] permissions =
    {
        "assets.read", "assets.write", "assets.delete", "assets.merge", "assets.import",
        "compliance.read", "compliance.manage", "matching.read", "matching.review",
        "connectors.read", "connectors.manage", "incidents.read", "incidents.manage",
        "reports.read", "reports.generate", "audit.read", "users.manage", "roles.manage",
        "settings.manage", "notifications.manage", "policies.manage", "relationships.manage",
        "approvals.decide"
    };
    foreach (var permission in permissions)
    {
        options.AddPolicy(permission, policy => policy
            .AddAuthenticationSchemes("Local")
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx =>
                ctx.User.HasClaim("permission", permission) || ctx.User.IsInRole("Administrator")));
    }
});

// ---------- Rate limiting (OWASP API4) ----------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:PermitLimit", 300),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// ---------- Swagger / OpenAPI ----------
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ESAR — Enterprise Security Asset Registry API",
        Version = "v1",
        Description = "Single source of truth for enterprise cyber assets: discovery, correlation, " +
                      "matching, enrichment, compliance and lifecycle management."
    });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT access token (obtain via POST /api/v1/auth/login)."
    });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

// ---------- CORS (web portal) ----------
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(o => o.AddPolicy("portal", p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

// The production Compose overlay keeps the API private and places Caddy in
// front of it. Only in that topology is it safe to accept forwarded headers
// from the Docker network rather than a fixed proxy IP.
var trustForwardedHeaders = builder.Configuration.GetValue("ReverseProxy:TrustForwardedHeaders", false);
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

// ---------- Health checks ----------
builder.Services.AddHealthChecks()
    .AddDbContextCheck<EsarDbContext>("postgres");

var app = builder.Build();

// ---------- Pipeline ----------
if (trustForwardedHeaders)
    app.UseForwardedHeaders();

app.UseSerilogRequestLogging();
app.UseMiddleware<ExceptionHandlingMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
// Swagger exposes the full API surface — restrict to development, or opt in
// per-environment with Swagger:Enabled=true. Off in production by default.
if (app.Environment.IsDevelopment() || app.Configuration.GetValue("Swagger:Enabled", false))
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "ESAR API v1");
        o.RoutePrefix = "swagger";
    });
}
app.UseCors("portal");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiAuditMiddleware>();

app.MapControllers();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

// ---------- Database init (dev/single-node; production uses SQL migration scripts) ----------
if (app.Configuration.GetValue("Database:AutoMigrate", true))
    await app.Services.InitializeDatabaseAsync();

app.Run();

public partial class Program; // exposed for integration tests
