using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Server.Controllers;
using server.Helpers;
using server.core.Data;
using server.core.Domain;
using Server.Tests;

namespace server.tests.Controllers;

public class FileControllerTests
{
    [Fact]
    public async Task GetById_when_not_authenticated_returns_unauthorized()
    {
        using AppDbContext ctx = TestDbContextFactory.CreateInMemory();

        var controller = new FileController(ctx);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity()),
            },
        };

        var result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);
        result.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task GetById_when_file_not_owned_returns_not_found()
    {
        using AppDbContext ctx = TestDbContextFactory.CreateInMemory();

        var owner = new User
        {
            UserId = 1,
            EntraObjectId = Guid.NewGuid(),
            Email = "owner@example.com",
            DisplayName = "Owner",
        };
        var otherUser = new User
        {
            UserId = 2,
            EntraObjectId = Guid.NewGuid(),
            Email = "other@example.com",
            DisplayName = "Other",
        };
        ctx.Users.AddRange(owner, otherUser);

        var fileId = Guid.NewGuid();
        ctx.Files.Add(new FileRecord
        {
            FileId = fileId,
            OwnerUserId = owner.UserId,
            OwnerUser = owner,
            OriginalFileName = "test.pdf",
            ContentType = "application/pdf",
            SizeBytes = 123,
            Status = FileRecord.Statuses.Completed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            StatusUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        });

        await ctx.SaveChangesAsync();

        var controller = new FileController(ctx);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = BuildUser(otherUser.UserId),
            },
        };

        var result = await controller.GetById(fileId, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetById_when_owned_returns_file_and_reports()
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
            Status = FileRecord.Statuses.Completed,
            CreatedAt = createdAt,
            StatusUpdatedAt = createdAt,
        });

        ctx.AccessibilityReports.AddRange(
            new AccessibilityReport
            {
                FileId = fileId,
                Stage = AccessibilityReport.Stages.Before,
                Tool = "TestTool",
                GeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                IssueCount = 2,
                ReportJson =
                    "{\"Summary\":{\"Description\":\"Before\",\"Passed\":1,\"Failed\":1},\"Detailed Report\":{\"Document\":[{\"Rule\":\"Title\",\"Status\":\"Failed\",\"Description\":\"Document title is showing in title bar\"}]}}",
            },
            new AccessibilityReport
            {
                FileId = fileId,
                Stage = AccessibilityReport.Stages.After,
                Tool = "TestTool",
                GeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                IssueCount = 1,
                ReportJson =
                    "{\"Summary\":{\"Description\":\"After\",\"Passed\":2,\"Failed\":0},\"Detailed Report\":{\"Document\":[{\"Rule\":\"Title\",\"Status\":\"Passed\",\"Description\":\"Document title is showing in title bar\"}]}}",
            });

        await ctx.SaveChangesAsync();

        var controller = new FileController(ctx);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = BuildUser(user.UserId),
            },
        };

        var result = await controller.GetById(fileId, CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();

        var ok = (OkObjectResult)result.Result!;
        ok.Value.Should().BeAssignableTo<FileController.FileDetailsDto>();

        var dto = (FileController.FileDetailsDto)ok.Value!;
        dto.FileId.Should().Be(fileId);
        dto.OriginalFileName.Should().Be("test.pdf");
        dto.AccessibilityReports.Should().HaveCount(2);

        dto.AccessibilityReports.Select(r => r.Stage).Should().Contain(new[]
        {
            AccessibilityReport.Stages.Before,
            AccessibilityReport.Stages.After,
        });

        var before = dto.AccessibilityReports.Single(r => r.Stage == AccessibilityReport.Stages.Before);
        before.ReportJson.GetProperty("Summary").GetProperty("Description").GetString().Should().Be("Before");

        var after = dto.AccessibilityReports.Single(r => r.Stage == AccessibilityReport.Stages.After);
        after.ReportJson.GetProperty("Summary").GetProperty("Passed").GetInt32().Should().Be(2);
    }

    private static ClaimsPrincipal BuildUser(long userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimsPrincipalExtensions.AppUserIdClaimType, userId.ToString()) },
            authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }
}

