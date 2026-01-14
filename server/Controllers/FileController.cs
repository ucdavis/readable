using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using server.Helpers;
using server.core.Data;
using server.core.Domain;

namespace Server.Controllers;

public class FileController : ApiControllerBase
{
    private readonly AppDbContext _dbContext;

    public FileController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<List<FileListItemDto>>> List(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var files = await _dbContext.Files
            .AsNoTracking()
            .Where(f => f.OwnerUserId == userId.Value)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FileListItemDto
            {
                FileId = f.FileId,
                OriginalFileName = f.OriginalFileName,
                ContentType = f.ContentType,
                SizeBytes = f.SizeBytes,
                Status = f.Status,
                CreatedAt = f.CreatedAt,
                StatusUpdatedAt = f.StatusUpdatedAt,
                AccessibilityReports = f.AccessibilityReports
                    .OrderByDescending(r => r.GeneratedAt)
                    .Select(r => new AccessibilityReportListItemDto
                    {
                        ReportId = r.ReportId,
                        FileId = r.FileId,
                        Stage = r.Stage,
                        Tool = r.Tool,
                        GeneratedAt = r.GeneratedAt,
                        IssueCount = r.IssueCount,
                        // don't include JSON content for the list
                    })
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return Ok(files);
    }

    [HttpGet("{fileId:guid}")]
    public async Task<ActionResult<FileDetailsDto>> GetById(
        [FromRoute] Guid fileId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var file = await _dbContext.Files
            .AsNoTracking()
            .Include(f => f.AccessibilityReports)
            .SingleOrDefaultAsync(
                f => f.FileId == fileId && f.OwnerUserId == userId.Value,
                cancellationToken);

        if (file is null)
        {
            return NotFound();
        }

        var reports = new List<AccessibilityReportDetailsDto>(file.AccessibilityReports.Count);
        foreach (var report in file.AccessibilityReports
                     .OrderBy(r => StageSortOrder(r.Stage))
                     .ThenByDescending(r => r.GeneratedAt))
        {
            JsonElement reportJson;
            try
            {
                using var doc = JsonDocument.Parse(report.ReportJson);
                reportJson = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Problem(
                    "Accessibility report JSON is invalid.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            reports.Add(new AccessibilityReportDetailsDto
            {
                ReportId = report.ReportId,
                FileId = report.FileId,
                Stage = report.Stage,
                Tool = report.Tool,
                GeneratedAt = report.GeneratedAt,
                IssueCount = report.IssueCount,
                ReportJson = reportJson,
            });
        }

        return Ok(new FileDetailsDto
        {
            FileId = file.FileId,
            OriginalFileName = file.OriginalFileName,
            ContentType = file.ContentType,
            SizeBytes = file.SizeBytes,
            Status = file.Status,
            CreatedAt = file.CreatedAt,
            StatusUpdatedAt = file.StatusUpdatedAt,
            AccessibilityReports = reports,
        });
    }

    private static int StageSortOrder(string stage)
    {
        return stage switch
        {
            AccessibilityReport.Stages.Before => 0,
            AccessibilityReport.Stages.After => 1,
            _ => 2,
        };
    }

    public sealed class FileListItemDto
    {
        public Guid FileId { get; init; }
        public string OriginalFileName { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset StatusUpdatedAt { get; init; }
        public List<AccessibilityReportListItemDto> AccessibilityReports { get; set; } = [];
    }

    public sealed class FileDetailsDto
    {
        public Guid FileId { get; init; }
        public string OriginalFileName { get; init; } = string.Empty;
        public string ContentType { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset StatusUpdatedAt { get; init; }
        public List<AccessibilityReportDetailsDto> AccessibilityReports { get; set; } = [];
    }

    public sealed class AccessibilityReportListItemDto
    {
        public long ReportId { get; init; }
        public Guid FileId { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string Tool { get; init; } = string.Empty;
        public DateTimeOffset GeneratedAt { get; init; }
        public int? IssueCount { get; init; }
    }

    public sealed class AccessibilityReportDetailsDto
    {
        public long ReportId { get; init; }
        public Guid FileId { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string Tool { get; init; } = string.Empty;
        public DateTimeOffset GeneratedAt { get; init; }
        public int? IssueCount { get; init; }
        public JsonElement ReportJson { get; init; }
    }
}
