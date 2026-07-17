using System.Text;
using ClosedXML.Excel;
using Esar.Application.Abstractions;
using Esar.Domain.Entities;
using Esar.Domain.Enums;
using Esar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Esar.Infrastructure.Reporting;

public class ReportGenerator : IReportGenerator
{
    private readonly EsarDbContext _db;
    private readonly ILogger<ReportGenerator> _logger;

    static ReportGenerator() => QuestPDF.Settings.License = LicenseType.Community;

    public ReportGenerator(EsarDbContext db, ILogger<ReportGenerator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<string> GenerateAsync(Report report, CancellationToken ct = default)
    {
        var outputDir = (await _db.Settings.FirstOrDefaultAsync(s => s.Key == "reports.outputDirectory", ct))?.Value
                        ?? Path.Combine(Path.GetTempPath(), "esar-reports");
        Directory.CreateDirectory(outputDir);

        var (headers, rows, title) = await BuildDataAsync(report.Type, ct);
        var extension = report.Format switch
        {
            ReportFormat.Pdf => "pdf",
            ReportFormat.Excel => "xlsx",
            _ => "csv"
        };
        var filePath = Path.Combine(outputDir,
            $"{report.Type}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{report.Id:N}.{extension}");

        switch (report.Format)
        {
            case ReportFormat.Csv:
                await WriteCsvAsync(filePath, headers, rows, ct);
                break;
            case ReportFormat.Excel:
                WriteExcel(filePath, title, headers, rows);
                break;
            case ReportFormat.Pdf:
                WritePdf(filePath, title, headers, rows);
                break;
        }

        _logger.LogInformation("Report {Type} ({Format}) generated at {Path}", report.Type, report.Format, filePath);
        return filePath;
    }

    private async Task<(string[] Headers, List<string?[]> Rows, string Title)> BuildDataAsync(
        ReportType type, CancellationToken ct)
    {
        switch (type)
        {
            case ReportType.AssetInventory:
            {
                var assets = await _db.Assets.Include(a => a.IpAddresses).AsSplitQuery()
                    .OrderBy(a => a.Hostname).Take(50_000).ToListAsync(ct);
                return (new[] { "Hostname", "FQDN", "OS", "Type", "Environment", "Criticality", "Status",
                        "Owner", "Business Unit", "Primary IP", "Compliance", "Last Seen" },
                    assets.Select(a => new[]
                    {
                        a.Hostname, a.Fqdn, a.OperatingSystem, a.AssetType.ToString(), a.Environment.ToString(),
                        a.Criticality.ToString(), a.Status.ToString(), a.OwnerName, a.BusinessUnit,
                        a.IpAddresses.FirstOrDefault()?.IpAddress, a.ComplianceStatus.ToString(),
                        a.LastSeen.ToString("yyyy-MM-dd HH:mm")
                    }).ToList(), "Asset Inventory");
            }
            case ReportType.Compliance:
            {
                var records = await _db.AssetCompliance.Include(c => c.Asset)
                    .Where(c => c.Status == ComplianceStatus.NonCompliant)
                    .OrderBy(c => c.Asset!.Hostname).Take(50_000).ToListAsync(ct);
                return (new[] { "Hostname", "Control", "Status", "Details", "Checked At" },
                    records.Select(c => new[]
                    {
                        c.Asset?.Hostname, c.Control.ToString(), c.Status.ToString(), c.Details,
                        c.CheckedAt.ToString("yyyy-MM-dd HH:mm")
                    }).ToList(), "Non-Compliant Controls");
            }
            case ReportType.MissingSiem:
            case ReportType.MissingEdr:
            {
                var control = type == ReportType.MissingSiem ? ControlType.SiemLogSource : ControlType.Edr;
                var records = await _db.AssetCompliance.Include(c => c.Asset)
                    .Where(c => c.Control == control && c.Status == ComplianceStatus.NonCompliant)
                    .Take(50_000).ToListAsync(ct);
                return (new[] { "Hostname", "OS", "Environment", "Criticality", "Owner", "Details" },
                    records.Where(c => c.Asset is not null).Select(c => new[]
                    {
                        c.Asset!.Hostname, c.Asset.OperatingSystem, c.Asset.Environment.ToString(),
                        c.Asset.Criticality.ToString(), c.Asset.OwnerName, c.Details
                    }).ToList(), type == ReportType.MissingSiem ? "Assets Missing SIEM" : "Assets Missing EDR");
            }
            case ReportType.InactiveAssets:
            {
                var cutoff = DateTime.UtcNow.AddDays(-30);
                var assets = await _db.Assets.Where(a => a.LastSeen < cutoff)
                    .OrderBy(a => a.LastSeen).Take(50_000).ToListAsync(ct);
                return (new[] { "Hostname", "OS", "Status", "Last Seen", "Owner" },
                    assets.Select(a => new[]
                    {
                        a.Hostname, a.OperatingSystem, a.Status.ToString(),
                        a.LastSeen.ToString("yyyy-MM-dd"), a.OwnerName
                    }).ToList(), "Inactive Assets (30+ days)");
            }
            case ReportType.CloudAssets:
            {
                var assets = await _db.Assets.Where(a => a.CloudProvider != null)
                    .OrderBy(a => a.CloudProvider).ThenBy(a => a.Hostname).Take(50_000).ToListAsync(ct);
                return (new[] { "Hostname", "Provider", "Region", "Subscription/Account", "Resource ID", "Compliance" },
                    assets.Select(a => new[]
                    {
                        a.Hostname, a.CloudProvider, a.CloudRegion,
                        a.CloudSubscriptionId ?? a.CloudAccountId, a.CloudResourceId, a.ComplianceStatus.ToString()
                    }).ToList(), "Cloud Assets");
            }
            case ReportType.DuplicateAssets:
            {
                var records = await _db.MatchRecords
                    .Where(m => m.Decision == MatchDecision.QueuedForReview)
                    .OrderByDescending(m => m.ConfidenceScore).Take(10_000).ToListAsync(ct);
                return (new[] { "Candidate Hostname", "Source", "External ID", "Score", "Queued At" },
                    records.Select(m => new[]
                    {
                        m.CandidateHostname, m.SourceConnector.ToString(), m.ExternalId,
                        m.ConfidenceScore.ToString("0.00"), m.CreatedAt.ToString("yyyy-MM-dd HH:mm")
                    }).ToList(), "Potential Duplicate Assets");
            }
            case ReportType.AssetChanges:
            {
                var since = DateTime.UtcNow.AddDays(-7);
                var history = await _db.AssetHistories.Include(h => h.Asset)
                    .Where(h => h.ChangedAt >= since)
                    .OrderByDescending(h => h.ChangedAt).Take(50_000).ToListAsync(ct);
                return (new[] { "Hostname", "Field", "Old Value", "New Value", "Changed By", "Changed At" },
                    history.Select(h => new[]
                    {
                        h.Asset?.Hostname, h.FieldName, h.OldValue, h.NewValue, h.ChangedBy,
                        h.ChangedAt.ToString("yyyy-MM-dd HH:mm")
                    }).ToList(), "Asset Changes (7 days)");
            }
            case ReportType.AssetOwners:
            case ReportType.BusinessUnits:
            {
                var grouped = await _db.Assets
                    .GroupBy(a => type == ReportType.AssetOwners ? (a.OwnerName ?? "(unassigned)")
                        : (a.BusinessUnit ?? "(unassigned)"))
                    .Select(g => new { Name = g.Key, Count = g.Count(),
                        NonCompliant = g.Count(a => a.ComplianceStatus == ComplianceStatus.NonCompliant) })
                    .OrderByDescending(g => g.Count).ToListAsync(ct);
                return (new[] { type == ReportType.AssetOwners ? "Owner" : "Business Unit", "Assets", "Non-Compliant" },
                    grouped.Select(g => new[] { g.Name, g.Count.ToString(), g.NonCompliant.ToString() })
                        .Cast<string?[]>().ToList(),
                    type == ReportType.AssetOwners ? "Assets by Owner" : "Assets by Business Unit");
            }
            case ReportType.ExecutiveSummary:
            default:
            {
                var total = await _db.Assets.CountAsync(ct);
                var rows = new List<string?[]>
                {
                    new[] { "Total assets", total.ToString() },
                    new[] { "Active", (await _db.Assets.CountAsync(a => a.Status == AssetStatus.Active, ct)).ToString() },
                    new[] { "Critical", (await _db.Assets.CountAsync(a => a.Criticality == CriticalityLevel.Critical, ct)).ToString() },
                    new[] { "Cloud", (await _db.Assets.CountAsync(a => a.CloudProvider != null, ct)).ToString() },
                    new[] { "Compliant", (await _db.Assets.CountAsync(a => a.ComplianceStatus == ComplianceStatus.Compliant, ct)).ToString() },
                    new[] { "Non-compliant", (await _db.Assets.CountAsync(a => a.ComplianceStatus == ComplianceStatus.NonCompliant, ct)).ToString() },
                    new[] { "Open incidents", (await _db.Incidents.CountAsync(i => i.Status == IncidentStatus.Open, ct)).ToString() },
                    new[] { "Pending match reviews", (await _db.MatchRecords.CountAsync(m => m.Decision == MatchDecision.QueuedForReview, ct)).ToString() }
                };
                return (new[] { "Metric", "Value" }, rows, "Executive Summary");
            }
        }
    }

    private static async Task WriteCsvAsync(string path, string[] headers, List<string?[]> rows, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row.Select(Escape)));
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct);

        static string Escape(string? value)
        {
            value ??= string.Empty;
            return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }
    }

    private static void WriteExcel(string path, string title, string[] headers, List<string?[]> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(title.Length > 31 ? title[..31] : title);
        for (var i = 0; i < headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = headers[i];
            sheet.Cell(1, i + 1).Style.Font.Bold = true;
            sheet.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1F2937");
            sheet.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
        }
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < headers.Length && c < rows[r].Length; c++)
                sheet.Cell(r + 2, c + 1).Value = rows[r][c] ?? string.Empty;
        sheet.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 100));
        sheet.SheetView.FreezeRows(1);
        workbook.SaveAs(path);
    }

    private static void WritePdf(string path, string title, string[] headers, List<string?[]> rows)
    {
        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.Header().Text($"ESAR — {title}").FontSize(16).Bold();
                page.Content().PaddingTop(12).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        foreach (var _ in headers) columns.RelativeColumn();
                    });
                    table.Header(header =>
                    {
                        foreach (var h in headers)
                            header.Cell().Background("#1F2937").Padding(4)
                                .Text(h).FontColor("#FFFFFF").FontSize(8).Bold();
                    });
                    foreach (var row in rows.Take(2000))
                        foreach (var cell in row.Take(headers.Length))
                            table.Cell().BorderBottom(0.5f).BorderColor("#DDDDDD").Padding(4)
                                .Text(cell ?? string.Empty).FontSize(7);
                });
                page.Footer().AlignRight()
                    .Text($"Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC — {rows.Count} rows").FontSize(7);
            });
        }).GeneratePdf(path);
    }
}
