using System.IO.Compression;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using server.Helpers;
using server.core.Data;
using server.core.Domain;
using server.core.Storage;

namespace Server.Controllers;

public class DownloadController : ApiControllerBase
{
    private static readonly TimeSpan SasTimeToLive = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum number of files allowed in a single zip download request.
    /// </summary>
    private const int MaxZipFiles = 50;

    private readonly AppDbContext _dbContext;
    private readonly IFileSasService _fileSasService;
    private readonly IProcessedPdfBlobService _processedPdfBlobService;

    public DownloadController(
        AppDbContext dbContext,
        IFileSasService fileSasService,
        IProcessedPdfBlobService processedPdfBlobService)
    {
        _dbContext = dbContext;
        _fileSasService = fileSasService;
        _processedPdfBlobService = processedPdfBlobService;
    }

    [HttpGet("processed/{fileId:guid}")]
    public async Task<IActionResult> DownloadProcessedPdf(
        [FromRoute] Guid fileId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var fileRecord = await _dbContext.Files
            .SingleOrDefaultAsync(
                x => x.FileId == fileId && x.OwnerUserId == userId.Value,
                cancellationToken);

        if (fileRecord is null)
        {
            return NotFound();
        }

        if (!string.Equals(fileRecord.Status, FileRecord.Statuses.Completed, StringComparison.Ordinal))
        {
            return Conflict("File is not completed.");
        }

        DownloadSasResult sas;
        try
        {
            sas = _fileSasService.CreateProcessedPdfDownloadSas(
                fileRecord.FileId,
                fileRecord.OriginalFileName,
                SasTimeToLive);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        return Redirect(sas.DownloadUri.ToString());
    }

    /// <summary>
    /// Streams a zip archive containing multiple processed PDFs.
    /// The zip is built on-the-fly so the server never buffers the entire archive in memory.
    /// </summary>
    [HttpPost("zip")]
    public async Task<IActionResult> DownloadZip(
        [FromBody] Guid[] fileIds,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        if (fileIds is null || fileIds.Length == 0)
        {
            return BadRequest("At least one file ID is required.");
        }

        var uniqueIds = fileIds.Distinct().ToArray();

        if (uniqueIds.Length > MaxZipFiles)
        {
            return BadRequest($"You can download at most {MaxZipFiles} files at once.");
        }

        // Fetch only completed files owned by this user
        var fileRecords = await _dbContext.Files
            .AsNoTracking()
            .Where(f => uniqueIds.Contains(f.FileId)
                        && f.OwnerUserId == userId.Value
                        && f.Status == FileRecord.Statuses.Completed)
            .Select(f => new { f.FileId, f.OriginalFileName })
            .ToListAsync(cancellationToken);

        if (fileRecords.Count == 0)
        {
            return NotFound("No completed files found for the given IDs.");
        }

        // De-duplicate file names so the zip doesn't have collisions
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(Guid FileId, string EntryName)>(fileRecords.Count);

        foreach (var record in fileRecords)
        {
            var name = SanitizeFileName(record.OriginalFileName);
            name = MakeUnique(name, usedNames);
            usedNames.Add(name);
            entries.Add((record.FileId, name));
        }

        // Stream the zip directly to the response
        Response.ContentType = "application/zip";
        Response.Headers.Append("Content-Disposition", "attachment; filename=\"readable-files.zip\"");

        // ZipArchive.Dispose() writes the central directory record synchronously.
        // Kestrel disallows sync I/O by default, so we opt in for this request only.
        var syncIoFeature = HttpContext.Features.Get<IHttpBodyControlFeature>();
        if (syncIoFeature is not null)
        {
            syncIoFeature.AllowSynchronousIO = true;
        }

        var bodyStream = Response.Body;

        using var archive = new ZipArchive(bodyStream, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var (fileId, entryName) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            await using var blobStream = await _processedPdfBlobService.OpenReadAsync(fileId, cancellationToken);
            await using var entryStream = entry.Open();
            await blobStream.CopyToAsync(entryStream, cancellationToken);
        }

        // ZipArchive.Dispose writes the central directory record; after that we're done.
        // Return an empty result so MVC doesn't try to write anything else.
        return new EmptyResult();
    }

    /// <summary>
    /// Ensures the file name is safe for zip entry use.
    /// </summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = (fileName ?? string.Empty).Trim();
        name = Path.GetFileName(name); // strip any path separators

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "document.pdf";
        }
        else if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            name += ".pdf";
        }

        return name;
    }

    /// <summary>
    /// Appends " (2)", " (3)", etc. until the name is unique in the set.
    /// </summary>
    private static string MakeUnique(string name, HashSet<string> used)
    {
        if (!used.Contains(name))
        {
            return name;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var counter = 2;

        string candidate;
        do
        {
            candidate = $"{nameWithoutExt} ({counter}){ext}";
            counter++;
        } while (used.Contains(candidate));

        return candidate;
    }
}

