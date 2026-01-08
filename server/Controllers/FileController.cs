using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using server.Helpers;
using server.core.Data;

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

    public sealed class AccessibilityReportListItemDto
    {
        public long ReportId { get; init; }
        public Guid FileId { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string Tool { get; init; } = string.Empty;
        public DateTimeOffset GeneratedAt { get; init; }
        public int? IssueCount { get; init; }
    }
}
