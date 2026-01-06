using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using server.Helpers;
using server.Helpers.Validation;
using server.core.Data;
using server.core.Domain;
using server.core.Upload;

namespace Server.Controllers;

public class UploadController : ApiControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IUploadSasService _uploadSasService;

    public UploadController(AppDbContext dbContext, IUploadSasService uploadSasService)
    {
        _dbContext = dbContext;
        _uploadSasService = uploadSasService;
    }

    /// <summary>
    /// Creates a SAS URL for uploading a PDF file.
    /// </summary>
    [HttpPost("sas")]
    public async Task<ActionResult<CreateUploadSasResponse>> CreateSas(
        [FromBody] CreateUploadSasRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var originalFileName = request.OriginalFileName;
        var contentType = request.ContentType;
        var sizeBytes = request.SizeBytes;

        if (!LooksLikePdf(contentType ?? string.Empty, originalFileName))
        {
            return BadRequest("Only PDF uploads are supported.");
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            contentType = "application/pdf";
        }

        var now = DateTimeOffset.UtcNow;
        var fileRecord = new FileRecord
        {
            FileId = Guid.NewGuid(),
            OwnerUserId = userId.Value,
            OriginalFileName = originalFileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Status = FileRecord.Statuses.Created,
            CreatedAt = now,
            StatusUpdatedAt = now,
        };

        await using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        _dbContext.Files.Add(fileRecord);
        await _dbContext.SaveChangesAsync(cancellationToken);

        UploadSasResult sas;
        try
        {
            // let the SAS url be valid for 30 min to give ample time to upload
            var sasExpirationMinutes = TimeSpan.FromMinutes(30);
            sas = _uploadSasService.CreateIncomingPdfUploadSas(
                fileId: fileRecord.FileId,
                timeToLive: sasExpirationMinutes);
        }

        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        await tx.CommitAsync(cancellationToken);

        return Ok(new CreateUploadSasResponse
        {
            FileId = fileRecord.FileId,
            UploadUrl = sas.UploadUri.ToString(),
            BlobUrl = sas.BlobUri.ToString(),
            ContainerName = sas.ContainerName,
            BlobName = sas.BlobName,
            ExpiresAt = sas.ExpiresAt,
        });
    }

    /// <summary>
    /// Refreshes the SAS URL for an existing PDF upload record.
    /// </summary>
    [HttpPost("{fileId:guid}/sas")]
    public async Task<ActionResult<CreateUploadSasResponse>> RefreshSas(
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

        if (!string.Equals(fileRecord.Status, FileRecord.Statuses.Created, StringComparison.Ordinal))
        {
            return Conflict("SAS URL can only be refreshed while the file is in Created status.");
        }

        UploadSasResult sas;
        try
        {
            // let the SAS url be valid for 30 min to give ample time to upload
            var sasExpirationMinutes = TimeSpan.FromMinutes(30);
            sas = _uploadSasService.CreateIncomingPdfUploadSas(
                fileId: fileRecord.FileId,
                timeToLive: sasExpirationMinutes);
        }

        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        return Ok(new CreateUploadSasResponse
        {
            FileId = fileRecord.FileId,
            UploadUrl = sas.UploadUri.ToString(),
            BlobUrl = sas.BlobUri.ToString(),
            ContainerName = sas.ContainerName,
            BlobName = sas.BlobName,
            ExpiresAt = sas.ExpiresAt,
        });
    }

    private static bool LooksLikePdf(string contentType, string fileName)
    {
        if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class CreateUploadSasRequest
    {
        private string _originalFileName = string.Empty;
        private string? _contentType;

        [NotEmptyOrWhitespace(ErrorMessage = "OriginalFileName is required.")]
        [StringLength(260, ErrorMessage = "OriginalFileName must be 260 characters or fewer.")]
        public string OriginalFileName
        {
            get => _originalFileName;
            set => _originalFileName = (value ?? string.Empty).Trim();
        }

        [MaxLength(128, ErrorMessage = "ContentType must be 128 characters or fewer.")]
        public string? ContentType
        {
            get => _contentType;
            set
            {
                var trimmed = value?.Trim();
                _contentType = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }
        }

        [Range(typeof(long), "1", "9223372036854775807", ErrorMessage = "SizeBytes must be greater than 0.")]
        public long SizeBytes { get; set; }
    }

    public sealed class CreateUploadSasResponse
    {
        public Guid FileId { get; init; }
        public string UploadUrl { get; init; } = string.Empty;
        public string BlobUrl { get; init; } = string.Empty;
        public string ContainerName { get; init; } = string.Empty;
        public string BlobName { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; init; }
    }
}
