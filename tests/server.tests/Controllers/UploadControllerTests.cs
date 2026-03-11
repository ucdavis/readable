using System.Security.Claims;
using FluentAssertions;
using iText.Kernel.Pdf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Server.Controllers;
using server.Helpers;
using server.core.Data;
using server.core.Domain;
using server.core.Ingest;
using server.core.Storage;
using Server.Tests;

namespace server.tests.Controllers;

public class UploadControllerTests
{
    [Fact]
    public async Task MarkUploaded_when_created_sets_queued()
    {
        using AppDbContext ctx = TestDbContextFactory.CreateInMemory();

        var user = new User
        {
            UserId = 1,
            EntraObjectId = Guid.NewGuid(),
            Email = "test@example.com",
            DisplayName = "Test User",
        };
        ctx.Users.Add(user);

        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var fileId = Guid.NewGuid();
        ctx.Files.Add(new FileRecord
        {
            FileId = fileId,
            OwnerUserId = user.UserId,
            OwnerUser = user,
            OriginalFileName = "test.pdf",
            ContentType = "application/pdf",
            SizeBytes = 123,
            Status = FileRecord.Statuses.Created,
            CreatedAt = createdAt,
            StatusUpdatedAt = createdAt,
        });

        await ctx.SaveChangesAsync();

        var controller = new UploadController(
            ctx,
            new UnusedFileSasService(),
            new UnusedBlobUploadService(),
            BuildConfiguration());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = BuildUser(user.UserId),
            },
        };

        var result = await controller.MarkUploaded(fileId, CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();

        var updated = await ctx.Files.SingleAsync(x => x.FileId == fileId);
        updated.Status.Should().Be(FileRecord.Statuses.Queued);
        updated.StatusUpdatedAt.Should().BeAfter(createdAt);
    }

    [Fact]
    public async Task DirectUpload_WhenPdfExceedsConfiguredPageLimit_ReturnsBadRequest()
    {
        using AppDbContext ctx = TestDbContextFactory.CreateInMemory();

        var user = new User
        {
            UserId = 1,
            EntraObjectId = Guid.NewGuid(),
            Email = "test@example.com",
            DisplayName = "Test User",
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();

        var blobUpload = new RecordingBlobUploadService();
        var controller = new UploadController(
            ctx,
            new UnusedFileSasService(),
            blobUpload,
            BuildConfiguration(("Ingest:PdfMaxPageCount", "25")));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = BuildUser(user.UserId),
            },
        };

        var pdfBytes = CreatePdfBytes(pageCount: 26);
        IFormFile file = new FormFile(new MemoryStream(pdfBytes), 0, pdfBytes.Length, "file", "too-many-pages.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };

        var result = await controller.DirectUpload(file, CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value.Should().Be(PdfPageLimitExceededException.BuildMessage(26, 25));
        blobUpload.Calls.Should().Be(0);
        ctx.Files.Count().Should().Be(0);
    }

    private static ClaimsPrincipal BuildUser(long userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimsPrincipalExtensions.AppUserIdClaimType, userId.ToString()) },
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values)
    {
        IEnumerable<KeyValuePair<string, string?>> data = values
            .Select(x => new KeyValuePair<string, string?>(x.Key, x.Value));

        return new ConfigurationBuilder()
            .AddInMemoryCollection(data)
            .Build();
    }

    private static byte[] CreatePdfBytes(int pageCount)
    {
        using var stream = new MemoryStream();
        var writer = new PdfWriter(stream);
        writer.SetCloseStream(false);

        using (var pdf = new PdfDocument(writer))
        {
            for (var i = 0; i < pageCount; i++)
            {
                pdf.AddNewPage();
            }
        }

        return stream.ToArray();
    }

    private sealed class UnusedFileSasService : IFileSasService
    {
        public UploadSasResult CreateIncomingPdfUploadSas(Guid fileId, TimeSpan timeToLive) =>
            throw new InvalidOperationException("Not used by this test.");

        public DownloadSasResult CreateProcessedPdfDownloadSas(Guid fileId, string fileName, TimeSpan timeToLive) =>
            throw new InvalidOperationException("Not used by this test.");
    }

    private sealed class UnusedBlobUploadService : IIncomingBlobUploadService
    {
        public Task UploadAsync(Guid fileId, Stream content, string contentType, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by this test.");
    }

    private sealed class RecordingBlobUploadService : IIncomingBlobUploadService
    {
        public int Calls { get; private set; }

        public Task UploadAsync(Guid fileId, Stream content, string contentType, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}


