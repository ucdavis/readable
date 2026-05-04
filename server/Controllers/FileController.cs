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
            .Where(f => f.OwnerUserId == userId.Value && !f.IsArchived)
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
                LatestFailureReason = f.ProcessingAttempts
                    .Where(a => a.Outcome == FileProcessingAttempt.Outcomes.Failed)
                    .OrderByDescending(a => a.AttemptNumber)
                    .Select(a => a.ErrorMessage)
                    .FirstOrDefault(),
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
            .Include(f => f.ProcessingAttempts)
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
            LatestFailureReason = file.ProcessingAttempts
                .Where(a => a.Outcome == FileProcessingAttempt.Outcomes.Failed)
                .OrderByDescending(a => a.AttemptNumber)
                .Select(a => a.ErrorMessage)
                .FirstOrDefault(),
            AccessibilityReports = reports,
            AccessibilityReportWarnings = GetAccessibilityReportWarnings(file.ProcessingAttempts),
        });
    }

    // Create an endpoint for archiving files. Pass an array if fileIds to archive, verify that they belong to the user, and set IsArchived to true. This will exclude them from the list endpoint but keep them in the database for record-keeping and potential future features like an "Archived Files" view or restore functionality.
    [HttpPost("archive")]
    public async Task<ActionResult<List<Guid>>> ArchiveFiles(
       [FromBody] Guid[] fileIds,
       CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }
        var filesToArchive = await _dbContext.Files
            .Where(f => fileIds.Contains(f.FileId) && f.OwnerUserId == userId.Value)
            .ToListAsync(cancellationToken);
        if (filesToArchive.Count == 0)
        {
            return NotFound("No files found to archive.");
        }
        foreach (var file in filesToArchive)
        {
            file.IsArchived = true;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        //Return a list of the archived fileIds in the response body so the client can remove them from the UI immediately without needing to refetch the entire list.
        var archivedFileIds = filesToArchive.Select(f => f.FileId).ToList();
        return Ok(archivedFileIds);
    }

    [HttpPost("undelete")]
    public async Task<ActionResult<List<Guid>>> UnDeleteFiles(
       [FromBody] Guid[] fileIds,
       CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }
        var filesToUnDelete = await _dbContext.Files
            .Where(f => fileIds.Contains(f.FileId) && f.OwnerUserId == userId.Value)
            .ToListAsync(cancellationToken);
        if (filesToUnDelete.Count == 0)
        {
            return NotFound("No files found to undelete.");
        }
        foreach (var file in filesToUnDelete)
        {
            file.IsArchived = false;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        var undeletedFileIds = filesToUnDelete.Select(f => f.FileId).ToList();
        return Ok(undeletedFileIds);
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

    private static List<AccessibilityReportWarningDto> GetAccessibilityReportWarnings(
        IEnumerable<FileProcessingAttempt> attempts)
    {
        var metadataJson = attempts
            .Where(a => !string.IsNullOrWhiteSpace(a.MetadataJson))
            .OrderByDescending(a => a.AttemptNumber)
            .Select(a => a.MetadataJson)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("accessibilityReports", out var accessibilityReports) ||
                !accessibilityReports.TryGetProperty("warnings", out var warnings) ||
                warnings.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<AccessibilityReportWarningDto>();
            foreach (var warning in warnings.EnumerateArray())
            {
                if (warning.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var stage = GetStringProperty(warning, "stage");
                var code = GetStringProperty(warning, "code");
                var message = GetStringProperty(warning, "message");
                if (string.IsNullOrWhiteSpace(stage) ||
                    string.IsNullOrWhiteSpace(code) ||
                    string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                result.Add(new AccessibilityReportWarningDto
                {
                    Stage = stage,
                    Code = code,
                    Message = message,
                });
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
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
        public string? LatestFailureReason { get; init; }
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
        public string? LatestFailureReason { get; init; }
        public List<AccessibilityReportDetailsDto> AccessibilityReports { get; set; } = [];
        public List<AccessibilityReportWarningDto> AccessibilityReportWarnings { get; set; } = [];
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

    public sealed class AccessibilityReportWarningDto
    {
        public string Stage { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }
}
