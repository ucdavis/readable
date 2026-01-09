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

    private readonly AppDbContext _dbContext;
    private readonly IFileSasService _fileSasService;

    public DownloadController(AppDbContext dbContext, IFileSasService fileSasService)
    {
        _dbContext = dbContext;
        _fileSasService = fileSasService;
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
}

