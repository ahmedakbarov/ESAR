using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Esar.Infrastructure.Persistence;

internal static class DatabaseSchemaUpgrader
{
    private static readonly string[] Resources =
    {
        "Esar.Infrastructure.Database.009_match_merge_safety.sql",
        "Esar.Infrastructure.Database.010_asset_groups.sql"
    };

    public static async Task ApplyAsync(EsarDbContext db, ILogger logger, CancellationToken ct)
    {
        var assembly = typeof(DatabaseSchemaUpgrader).Assembly;
        foreach (var resource in Resources)
        {
            await using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded database upgrade '{resource}' was not found.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct);
            await db.Database.ExecuteSqlRawAsync(sql, ct);
            logger.LogInformation("Applied idempotent database upgrade {Upgrade}", resource);
        }
    }
}
