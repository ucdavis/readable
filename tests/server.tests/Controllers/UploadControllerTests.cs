using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Controllers;
using server.Helpers;
using server.core.Data;
using server.core.Domain;
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

        var controller = new UploadController(ctx, new UnusedFileSasService());
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

    private static ClaimsPrincipal BuildUser(long userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimsPrincipalExtensions.AppUserIdClaimType, userId.ToString()) },
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private sealed class UnusedFileSasService : IFileSasService
    {
        public UploadSasResult CreateIncomingPdfUploadSas(Guid fileId, TimeSpan timeToLive) =>
            throw new InvalidOperationException("Not used by this test.");

        public DownloadSasResult CreateProcessedPdfDownloadSas(Guid fileId, string fileName, TimeSpan timeToLive) =>
            throw new InvalidOperationException("Not used by this test.");
    }
}
