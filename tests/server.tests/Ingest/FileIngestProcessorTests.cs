using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using server.core.Data;
using server.core.Domain;
using server.core.Ingest;

namespace server.tests.Ingest;

public class FileIngestProcessorTests
{
    [Fact]
    public async Task ProcessAsync_CallsBlobOpenerAndPdfProcessor_UploadsProcessedAndDeletesIncoming()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Database.EnsureCreated();

            var user = new User
            {
                UserId = 1,
                EntraObjectId = Guid.NewGuid(),
                Email = "test@example.com",
                DisplayName = "Test User",
            };

            seed.Users.Add(user);
            seed.Files.Add(new FileRecord
            {
                FileId = fileId,
                OwnerUserId = user.UserId,
                OwnerUser = user,
                OriginalFileName = "abc123.pdf",
                ContentType = "application/pdf",
                SizeBytes = 123,
                Status = FileRecord.Statuses.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                StatusUpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var opener = new FakeBlobStreamOpener();
        var pdf = new FakePdfProcessor(dbContextFactory, fileId);
        var blobStorage = new FakeBlobStorage();
        var configuration = new ConfigurationBuilder().Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            opener,
            pdf,
            blobStorage,
            configuration,
            Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = true,
                UsePdfRemediationProcessor = true,
                UsePdfBookmarks = true,
                AutotagTaggedPdfs = false,
                MaxPagesPerChunk = 200,
                MaxUploadPages = 25,
            }),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/abc123.pdf"),
            "incoming",
            "abc123.pdf",
            fileId.ToString());

        try
        {
            await sut.ProcessAsync(request, CancellationToken.None);
        }
        finally
        {
            pdf.Cleanup();
        }

        opener.Seen.Should().Be(request.BlobUri);
        pdf.Calls.Should().Be(1);
        pdf.SeenFileId.Should().Be(fileId.ToString());
        blobStorage.UploadedTo.Should().Be(new Uri("https://example.blob.core.windows.net/processed/abc123.pdf"));
        blobStorage.Deleted.Should().Be(request.BlobUri);

        await using var verify = new AppDbContext(options);
        var file = await verify.Files.SingleAsync(x => x.FileId == fileId);
        file.Status.Should().Be(FileRecord.Statuses.Completed);
        file.PageCount.Should().Be(pdf.PageCount);

        var beforeReport = await verify.AccessibilityReports.SingleOrDefaultAsync(
            x => x.FileId == fileId
                && x.Stage == AccessibilityReport.Stages.Before
                && x.Tool == "AdobePdfServices");
        beforeReport.Should().NotBeNull();
        beforeReport!.ReportJson.Should().Be(pdf.BeforeAccessibilityReportJson);

        var afterReport = await verify.AccessibilityReports.SingleOrDefaultAsync(
            x => x.FileId == fileId
                && x.Stage == AccessibilityReport.Stages.After
                && x.Tool == "AdobePdfServices");
        afterReport.Should().NotBeNull();
        afterReport!.ReportJson.Should().Be(pdf.AfterAccessibilityReportJson);

        var attempts = await verify.FileProcessingAttempts
            .Where(x => x.FileId == fileId)
            .OrderBy(x => x.AttemptNumber)
            .ToListAsync();

        attempts.Should().HaveCount(1);
        attempts[0].AttemptNumber.Should().Be(1);
        attempts[0].Trigger.Should().Be(FileProcessingAttempt.Triggers.Upload);
        attempts[0].Outcome.Should().Be(FileProcessingAttempt.Outcomes.Succeeded);
        attempts[0].StartedAt.Should().NotBe(default);
        attempts[0].FinishedAt.Should().NotBeNull();
        attempts[0].ErrorCode.Should().BeNull();
        attempts[0].ErrorMessage.Should().BeNull();
        attempts[0].ErrorDetails.Should().BeNull();
        attempts[0].MetadataJson.Should().NotBeNullOrWhiteSpace();
        using var metadata = JsonDocument.Parse(attempts[0].MetadataJson!);
        var root = metadata.RootElement;
        root.GetProperty("configuration").GetProperty("autotagProviderConfigured").GetString().Should().Be("Adobe");
        root.GetProperty("processing").GetProperty("pageCount").GetInt32().Should().Be(7);
        root.GetProperty("autotag").GetProperty("provider").GetString().Should().Be("OpenDataLoader");
    }

    [Fact]
    public async Task ProcessAsync_WithOpenDataLoaderProvider_EnqueuesAutotagAndLeavesAttemptOpen()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await SeedFileAsync(options, fileId, "queued.pdf");

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var opener = new FakeBlobStreamOpener();
        var pdf = new FakePipelineProcessor
        {
            IntakeResult = new PdfIntakeResult(
                RequiresAutotag: true,
                PageCount: 5,
                BeforeAccessibilityReportJson: "{\"Summary\":{\"Failed\":1}}",
                BeforeAccessibilityReportPath: null,
                Autotag: new PdfAutotagMetadata(
                    FileIngestOptions.AutotagProviders.OpenDataLoader,
                    Required: true,
                    SkippedReason: null,
                    ChunkCount: 1,
                    LocalReportPaths: []))
        };
        var queue = new FakeIngestQueueClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["INGEST_AUTOTAG_PROVIDER"] = FileIngestOptions.AutotagProviders.OpenDataLoader,
                ["Storage__TempContainer"] = "temp",
                ["Storage__ReportsContainer"] = "reports",
            })
            .Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            opener,
            pdf,
            new FakeBlobStorage(),
            configuration,
            queue,
            new IngestQueueOptions("files", "autotag-odl", "pdf-finalize", "pdf-failed"),
            Options.Create(new PdfProcessorOptions
            {
                UseAdobePdfServices = true,
                UsePdfRemediationProcessor = true,
                MaxPagesPerChunk = 200,
                MaxUploadPages = 25,
            }),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/queued.pdf"),
            "incoming",
            "queued.pdf",
            fileId.ToString());

        await sut.ProcessAsync(request, CancellationToken.None);

        pdf.IntakeCalls.Should().Be(1);
        queue.AutotagJobs.Should().ContainSingle();
        queue.FinalizeMessages.Should().BeEmpty();
        queue.AutotagJobs[0].SourceBlobUri.Should().Be(request.BlobUri);
        queue.AutotagJobs[0].OutputTaggedPdfBlobUri.ToString().Should().Contain("/temp/");
        queue.AutotagJobs[0].OutputReportBlobUri.ToString().Should().Contain("/reports/");

        await using var verify = new AppDbContext(options);
        var file = await verify.Files.SingleAsync(x => x.FileId == fileId);
        file.Status.Should().Be(FileRecord.Statuses.Processing);
        file.PageCount.Should().Be(0);

        var attempt = await verify.FileProcessingAttempts.SingleAsync(x => x.FileId == fileId);
        attempt.Outcome.Should().BeNull();
        attempt.FinishedAt.Should().BeNull();

        var beforeReport = await verify.AccessibilityReports.SingleOrDefaultAsync(
            x => x.FileId == fileId
                && x.Stage == AccessibilityReport.Stages.Before
                && x.Tool == "AdobePdfServices");
        beforeReport.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessAsync_WithOpenDataLoaderProvider_WhenPdfDoesNotNeedAutotag_EnqueuesFinalize()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await SeedFileAsync(options, fileId, "already-tagged.pdf");

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var pdf = new FakePipelineProcessor
        {
            IntakeResult = new PdfIntakeResult(
                RequiresAutotag: false,
                PageCount: 5,
                BeforeAccessibilityReportJson: "{\"Summary\":{\"Failed\":0}}",
                BeforeAccessibilityReportPath: null,
                Autotag: new PdfAutotagMetadata(
                    FileIngestOptions.AutotagProviders.None,
                    Required: false,
                    SkippedReason: "already-tagged",
                    ChunkCount: 0,
                    LocalReportPaths: []))
        };
        var queue = new FakeIngestQueueClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["INGEST_AUTOTAG_PROVIDER"] = FileIngestOptions.AutotagProviders.OpenDataLoader,
            })
            .Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            new FakeBlobStreamOpener(),
            pdf,
            new FakeBlobStorage(),
            configuration,
            queue,
            new IngestQueueOptions("files", "autotag-odl", "pdf-finalize", "pdf-failed"),
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/already-tagged.pdf"),
            "incoming",
            "already-tagged.pdf",
            fileId.ToString());

        await sut.ProcessAsync(request, CancellationToken.None);

        queue.AutotagJobs.Should().BeEmpty();
        queue.FinalizeMessages.Should().ContainSingle();
        queue.FinalizeMessages[0].PdfToFinalizeBlobUri.Should().Be(request.BlobUri);
        queue.FinalizeMessages[0].Autotag.Provider.Should().Be(FileIngestOptions.AutotagProviders.None);
        queue.FinalizeMessages[0].Autotag.SkippedReason.Should().Be("already-tagged");

        await using var verify = new AppDbContext(options);
        var file = await verify.Files.SingleAsync(x => x.FileId == fileId);
        file.Status.Should().Be(FileRecord.Statuses.Processing);

        var attempt = await verify.FileProcessingAttempts.SingleAsync(x => x.FileId == fileId);
        attempt.Outcome.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_WithOpenDataLoaderProvider_WhenFilesMessageIsDuplicated_ReusesOpenAttempt()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await SeedFileAsync(options, fileId, "queued.pdf");

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var pdf = new FakePipelineProcessor
        {
            IntakeResult = new PdfIntakeResult(
                RequiresAutotag: true,
                PageCount: 5,
                BeforeAccessibilityReportJson: "{\"Summary\":{\"Failed\":1}}",
                BeforeAccessibilityReportPath: null,
                Autotag: new PdfAutotagMetadata(
                    FileIngestOptions.AutotagProviders.OpenDataLoader,
                    Required: true,
                    SkippedReason: null,
                    ChunkCount: 1,
                    LocalReportPaths: []))
        };
        var queue = new FakeIngestQueueClient();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["INGEST_AUTOTAG_PROVIDER"] = FileIngestOptions.AutotagProviders.OpenDataLoader,
                ["Storage__TempContainer"] = "temp",
                ["Storage__ReportsContainer"] = "reports",
            })
            .Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            new FakeBlobStreamOpener(),
            pdf,
            new FakeBlobStorage(),
            configuration,
            queue,
            new IngestQueueOptions("files", "autotag-odl", "pdf-finalize", "pdf-failed"),
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/queued.pdf"),
            "incoming",
            "queued.pdf",
            fileId.ToString());

        await sut.ProcessAsync(request, CancellationToken.None);
        await sut.ProcessAsync(request, CancellationToken.None);

        queue.AutotagJobs.Should().HaveCount(2);
        queue.AutotagJobs.Select(x => x.AttemptId).Distinct().Should().ContainSingle();
        queue.AutotagJobs.Select(x => x.OutputTaggedPdfBlobUri).Distinct().Should().ContainSingle();
        queue.AutotagJobs.Select(x => x.OutputReportBlobUri).Distinct().Should().ContainSingle();

        await using var verify = new AppDbContext(options);
        var attempts = await verify.FileProcessingAttempts.Where(x => x.FileId == fileId).ToListAsync();
        attempts.Should().ContainSingle();

        var beforeReports = await verify.AccessibilityReports
            .Where(x => x.FileId == fileId && x.Stage == AccessibilityReport.Stages.Before)
            .ToListAsync();
        beforeReports.Should().ContainSingle();
    }

    [Fact]
    public async Task FinalizeAsync_CompletesQueuedAttemptAndDeletesIncomingBlob()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await SeedFileAsync(options, fileId, "queued.pdf");
        long attemptId;
        await using (var db = new AppDbContext(options))
        {
            var seededFile = await db.Files.SingleAsync(x => x.FileId == fileId);
            seededFile.Status = FileRecord.Statuses.Processing;
            var seededAttempt = new FileProcessingAttempt
            {
                FileId = fileId,
                AttemptNumber = 1,
                Trigger = FileProcessingAttempt.Triggers.Upload,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.FileProcessingAttempts.Add(seededAttempt);
            await db.SaveChangesAsync();
            attemptId = seededAttempt.AttemptId;
        }

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var opener = new FakeBlobStreamOpener();
        var pdf = new FakePipelineProcessor
        {
            FinalizeResult = new PdfProcessResult(
                OutputPdfPath: CreateTempPdf(),
                AfterAccessibilityReportJson: "{\"Summary\":{\"Failed\":0}}",
                PageCount: 5,
                Autotag: new PdfAutotagMetadata(
                    FileIngestOptions.AutotagProviders.OpenDataLoader,
                    Required: true,
                    SkippedReason: null,
                    ChunkCount: 1,
                    LocalReportPaths: ["https://example.blob.core.windows.net/reports/report.json"]))
        };
        var blobStorage = new FakeBlobStorage();
        var configuration = new ConfigurationBuilder().Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            opener,
            pdf,
            blobStorage,
            configuration,
            new FakeIngestQueueClient(),
            new IngestQueueOptions("files", "autotag-odl", "pdf-finalize", "pdf-failed"),
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var message = new FinalizePdfMessage(
            FileId: fileId.ToString(),
            AttemptId: attemptId,
            OriginalBlobUri: new Uri("https://example.blob.core.windows.net/incoming/queued.pdf"),
            OriginalContainerName: "incoming",
            OriginalBlobName: "queued.pdf",
            PdfToFinalizeBlobUri: new Uri("https://example.blob.core.windows.net/temp/tagged.pdf"),
            PageCount: 5,
            Autotag: new PdfAutotagMessageMetadata(
                FileIngestOptions.AutotagProviders.OpenDataLoader,
                Required: true,
                SkippedReason: null,
                ChunkCount: 1,
                ReportUris: ["https://example.blob.core.windows.net/reports/report.json"]),
            CorrelationId: Guid.NewGuid().ToString("N"),
            EnqueuedAt: DateTimeOffset.UtcNow);

        try
        {
            await sut.FinalizeAsync(message, CancellationToken.None);
        }
        finally
        {
            File.Delete(pdf.FinalizeResult!.OutputPdfPath);
        }

        pdf.FinalizeCalls.Should().Be(1);
        blobStorage.UploadedTo.Should().Be(new Uri("https://example.blob.core.windows.net/processed/queued.pdf"));
        blobStorage.Deleted.Should().Be(message.OriginalBlobUri);

        await using var verify = new AppDbContext(options);
        var file = await verify.Files.SingleAsync(x => x.FileId == fileId);
        file.Status.Should().Be(FileRecord.Statuses.Completed);
        file.PageCount.Should().Be(5);

        var attempt = await verify.FileProcessingAttempts.SingleAsync(x => x.FileId == fileId);
        attempt.Outcome.Should().Be(FileProcessingAttempt.Outcomes.Succeeded);
        attempt.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task FinalizeAsync_WhenMessageIsDuplicated_UpsertsReportsAndOverwritesProcessedBlob()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await SeedFileAsync(options, fileId, "queued.pdf");
        long attemptId;
        await using (var db = new AppDbContext(options))
        {
            var seededFile = await db.Files.SingleAsync(x => x.FileId == fileId);
            seededFile.Status = FileRecord.Statuses.Processing;
            var seededAttempt = new FileProcessingAttempt
            {
                FileId = fileId,
                AttemptNumber = 1,
                Trigger = FileProcessingAttempt.Triggers.Upload,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.FileProcessingAttempts.Add(seededAttempt);
            await db.SaveChangesAsync();
            attemptId = seededAttempt.AttemptId;
        }

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var outputPath = CreateTempPdf();
        var pdf = new FakePipelineProcessor
        {
            FinalizeResult = new PdfProcessResult(
                OutputPdfPath: outputPath,
                AfterAccessibilityReportJson: "{\"Summary\":{\"Failed\":0}}",
                PageCount: 5,
                Autotag: new PdfAutotagMetadata(
                    FileIngestOptions.AutotagProviders.OpenDataLoader,
                    Required: true,
                    SkippedReason: null,
                    ChunkCount: 1,
                    LocalReportPaths: ["https://example.blob.core.windows.net/reports/report.json"]))
        };
        var blobStorage = new FakeBlobStorage();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            new FakeBlobStreamOpener(),
            pdf,
            blobStorage,
            new ConfigurationBuilder().Build(),
            new FakeIngestQueueClient(),
            new IngestQueueOptions("files", "autotag-odl", "pdf-finalize", "pdf-failed"),
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var message = new FinalizePdfMessage(
            FileId: fileId.ToString(),
            AttemptId: attemptId,
            OriginalBlobUri: new Uri("https://example.blob.core.windows.net/incoming/queued.pdf"),
            OriginalContainerName: "incoming",
            OriginalBlobName: "queued.pdf",
            PdfToFinalizeBlobUri: new Uri("https://example.blob.core.windows.net/temp/tagged.pdf"),
            PageCount: 5,
            Autotag: new PdfAutotagMessageMetadata(
                FileIngestOptions.AutotagProviders.OpenDataLoader,
                Required: true,
                SkippedReason: null,
                ChunkCount: 1,
                ReportUris: ["https://example.blob.core.windows.net/reports/report.json"]),
            CorrelationId: Guid.NewGuid().ToString("N"),
            EnqueuedAt: DateTimeOffset.UtcNow);

        try
        {
            await sut.FinalizeAsync(message, CancellationToken.None);
            await sut.FinalizeAsync(message, CancellationToken.None);
        }
        finally
        {
            File.Delete(outputPath);
        }

        pdf.FinalizeCalls.Should().Be(2);
        blobStorage.Uploads.Should().HaveCount(2);
        blobStorage.Uploads.Distinct().Should().ContainSingle()
            .Which.Should().Be(new Uri("https://example.blob.core.windows.net/processed/queued.pdf"));
        blobStorage.Deletes.Should().HaveCount(2);

        await using var verify = new AppDbContext(options);
        var reports = await verify.AccessibilityReports
            .Where(x => x.FileId == fileId && x.Stage == AccessibilityReport.Stages.After)
            .ToListAsync();
        reports.Should().ContainSingle();

        var attempts = await verify.FileProcessingAttempts.Where(x => x.FileId == fileId).ToListAsync();
        attempts.Should().ContainSingle();
        attempts[0].Outcome.Should().Be(FileProcessingAttempt.Outcomes.Succeeded);
    }

    [Fact]
    public async Task FailAsync_MarksQueuedAttemptAndFileFailed()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await SeedFileAsync(options, fileId, "queued.pdf");
        long attemptId;
        await using (var db = new AppDbContext(options))
        {
            var seededFile = await db.Files.SingleAsync(x => x.FileId == fileId);
            seededFile.Status = FileRecord.Statuses.Processing;
            var seededAttempt = new FileProcessingAttempt
            {
                FileId = fileId,
                AttemptNumber = 1,
                Trigger = FileProcessingAttempt.Triggers.Upload,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.FileProcessingAttempts.Add(seededAttempt);
            await db.SaveChangesAsync();
            attemptId = seededAttempt.AttemptId;
        }

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["INGEST_AUTOTAG_PROVIDER"] = FileIngestOptions.AutotagProviders.OpenDataLoader,
            })
            .Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            new FakeBlobStreamOpener(),
            new FakePipelineProcessor(),
            new FakeBlobStorage(),
            configuration,
            new FakeIngestQueueClient(),
            new IngestQueueOptions("files", "autotag-odl", "pdf-finalize", "pdf-failed"),
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var message = new AutotagFailedMessage(
            FileId: fileId.ToString(),
            AttemptId: attemptId,
            OriginalBlobUri: new Uri("https://example.blob.core.windows.net/incoming/queued.pdf"),
            OriginalContainerName: "incoming",
            OriginalBlobName: "queued.pdf",
            Provider: FileIngestOptions.AutotagProviders.OpenDataLoader,
            ErrorCode: "InvalidOperationException",
            ErrorMessage: "OpenDataLoader completed without producing a tagged PDF artifact.",
            ErrorDetails: "details",
            DeliveryCount: 10,
            CorrelationId: Guid.NewGuid().ToString("N"),
            FailedAt: DateTimeOffset.UtcNow);

        await sut.FailAsync(message, CancellationToken.None);

        await using var verify = new AppDbContext(options);
        var file = await verify.Files.SingleAsync(x => x.FileId == fileId);
        file.Status.Should().Be(FileRecord.Statuses.Failed);

        var attempt = await verify.FileProcessingAttempts.SingleAsync(x => x.FileId == fileId);
        attempt.Outcome.Should().Be(FileProcessingAttempt.Outcomes.Failed);
        attempt.FinishedAt.Should().NotBeNull();
        attempt.ErrorCode.Should().Be("InvalidOperationException");
        attempt.ErrorMessage.Should().Contain("without producing a tagged PDF");
        attempt.ErrorDetails.Should().Contain("details");
        attempt.MetadataJson.Should().NotBeNullOrWhiteSpace();
        using var metadata = JsonDocument.Parse(attempt.MetadataJson!);
        metadata.RootElement.GetProperty("failure").GetProperty("provider").GetString()
            .Should().Be(FileIngestOptions.AutotagProviders.OpenDataLoader);
    }

    [Fact]
    public async Task ProcessAsync_WhenPdfProcessorThrows_MarksAttemptAndFileFailed()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Database.EnsureCreated();

            var user = new User
            {
                UserId = 1,
                EntraObjectId = Guid.NewGuid(),
            };

            seed.Users.Add(user);
            seed.Files.Add(new FileRecord
            {
                FileId = fileId,
                OwnerUserId = user.UserId,
                OwnerUser = user,
                OriginalFileName = "abc123.pdf",
                ContentType = "application/pdf",
                SizeBytes = 123,
                Status = FileRecord.Statuses.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                StatusUpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var opener = new FakeBlobStreamOpener();
        var pdf = new ThrowingPdfProcessor();
        var blobStorage = new FakeBlobStorage();
        var configuration = new ConfigurationBuilder().Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            opener,
            pdf,
            blobStorage,
            configuration,
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/abc123.pdf"),
            "incoming",
            "abc123.pdf",
            fileId.ToString());

        Func<Task> act = () => sut.ProcessAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = new AppDbContext(options);
        var file = await verify.Files.SingleAsync(x => x.FileId == fileId);
        file.Status.Should().Be(FileRecord.Statuses.Failed);

        var attempt = await verify.FileProcessingAttempts.SingleAsync(x => x.FileId == fileId);
        attempt.Trigger.Should().Be(FileProcessingAttempt.Triggers.Upload);
        attempt.Outcome.Should().Be(FileProcessingAttempt.Outcomes.Failed);
        attempt.FinishedAt.Should().NotBeNull();
        attempt.ErrorCode.Should().Be(nameof(InvalidOperationException));
        attempt.ErrorMessage.Should().Contain("boom");
        attempt.ErrorDetails.Should().Contain("https://example.blob.core.windows.net/incoming/abc123.pdf");
    }

    [Fact]
    public async Task ProcessAsync_WhenFileRecordMissing_ThrowsBeforeOpeningBlob()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Database.EnsureCreated();
        }

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var opener = new FakeBlobStreamOpener();
        var pdf = new ThrowingPdfProcessor();
        var blobStorage = new FakeBlobStorage();
        var configuration = new ConfigurationBuilder().Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            opener,
            pdf,
            blobStorage,
            configuration,
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/abc123.pdf"),
            "incoming",
            "abc123.pdf",
            Guid.NewGuid().ToString());

        Func<Task> act = () => sut.ProcessAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        opener.Seen.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_WhenPdfExceedsPageLimit_MarksAttemptFailedAndPersistsPageCount()
    {
        var fileId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ingest_{Guid.NewGuid():N}")
            .EnableSensitiveDataLogging()
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Database.EnsureCreated();

            var user = new User
            {
                UserId = 1,
                EntraObjectId = Guid.NewGuid(),
                Email = "test@example.com",
                DisplayName = "Test User",
            };

            seed.Users.Add(user);
            seed.Files.Add(new FileRecord
            {
                FileId = fileId,
                OwnerUserId = user.UserId,
                OwnerUser = user,
                OriginalFileName = "too-many-pages.pdf",
                ContentType = "application/pdf",
                SizeBytes = 123,
                Status = FileRecord.Statuses.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                StatusUpdatedAt = DateTimeOffset.UtcNow,
            });

            await seed.SaveChangesAsync();
        }

        var dbContextFactory = new InMemoryDbContextFactory(options);
        var opener = new FakeBlobStreamOpener();
        var pdf = new PageLimitThrowingPdfProcessor();
        var blobStorage = new FakeBlobStorage();
        var configuration = new ConfigurationBuilder().Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            dbContextFactory,
            opener,
            pdf,
            blobStorage,
            configuration,
            Options.Create(new PdfProcessorOptions()),
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/abc123.pdf"),
            "incoming",
            "abc123.pdf",
            fileId.ToString());

        Func<Task> act = () => sut.ProcessAsync(request, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<PdfPageLimitExceededException>();
        ex.Which.ActualPageCount.Should().Be(pdf.ActualPageCount);
        ex.Which.MaxAllowedPages.Should().Be(pdf.MaxAllowedPages);

        await using var verify = new AppDbContext(options);
        var file = await verify.Files.SingleAsync(x => x.FileId == fileId);
        file.Status.Should().Be(FileRecord.Statuses.Failed);
        file.PageCount.Should().Be(pdf.ActualPageCount);

        var attempt = await verify.FileProcessingAttempts.SingleAsync(x => x.FileId == fileId);
        attempt.Outcome.Should().Be(FileProcessingAttempt.Outcomes.Failed);
        attempt.ErrorCode.Should().Be(nameof(PdfPageLimitExceededException));
        attempt.ErrorMessage.Should().Be($"PDF has {pdf.ActualPageCount} pages; maximum allowed is {pdf.MaxAllowedPages}.");
    }

    private static async Task SeedFileAsync(
        DbContextOptions<AppDbContext> options,
        Guid fileId,
        string fileName)
    {
        await using var seed = new AppDbContext(options);
        seed.Database.EnsureCreated();

        var user = new User
        {
            UserId = 1,
            EntraObjectId = Guid.NewGuid(),
            Email = "test@example.com",
            DisplayName = "Test User",
        };

        seed.Users.Add(user);
        seed.Files.Add(new FileRecord
        {
            FileId = fileId,
            OwnerUserId = user.UserId,
            OwnerUser = user,
            OriginalFileName = fileName,
            ContentType = "application/pdf",
            SizeBytes = 123,
            Status = FileRecord.Statuses.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            StatusUpdatedAt = DateTimeOffset.UtcNow,
        });

        await seed.SaveChangesAsync();
    }

    private static string CreateTempPdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"readable-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, "%PDF-1.7\n%final"u8.ToArray());
        return path;
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext()
        {
            return new AppDbContext(_options);
        }
    }

    private sealed class FakeBlobStreamOpener : IBlobStreamOpener
    {
        public Uri? Seen { get; private set; }

        public Task<Stream> OpenReadAsync(Uri blobUri, CancellationToken cancellationToken)
        {
            Seen = blobUri;
            return Task.FromResult<Stream>(new MemoryStream("%PDF-1.7"u8.ToArray()));
        }
    }

    private sealed class FakePdfProcessor : IPdfProcessor
    {
        private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
        private readonly Guid _fileId;

        public int Calls { get; private set; }
        public string? SeenFileId { get; private set; }
        public int PageCount { get; } = 7;
        public string BeforeAccessibilityReportJson { get; } = "{\"kind\":\"test\",\"stage\":\"before\",\"issues\":[]}";
        public string AfterAccessibilityReportJson { get; } = "{\"kind\":\"test\",\"stage\":\"after\",\"issues\":[]}";
        private readonly string _outputPath = Path.Combine(Path.GetTempPath(), $"readable-test-{Guid.NewGuid():N}.pdf");

        public FakePdfProcessor(IDbContextFactory<AppDbContext> dbContextFactory, Guid fileId)
        {
            _dbContextFactory = dbContextFactory;
            _fileId = fileId;
        }

        public async Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
        {
            Calls++;
            SeenFileId = fileId;

            await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
            {
                var file = await db.Files.SingleAsync(x => x.FileId == _fileId, cancellationToken);
                file.Status.Should().Be(FileRecord.Statuses.Processing);

                var attempt = await db.FileProcessingAttempts
                    .Where(x => x.FileId == _fileId)
                    .OrderByDescending(x => x.AttemptNumber)
                    .FirstAsync(cancellationToken);

                attempt.Trigger.Should().Be(FileProcessingAttempt.Triggers.Upload);
                attempt.Outcome.Should().BeNull();
            }

            await using (var output = File.Create(_outputPath))
            {
                await output.WriteAsync("%PDF-1.7\n%final"u8.ToArray(), cancellationToken);
            }

            return new PdfProcessResult(
                _outputPath,
                BeforeAccessibilityReportJson: BeforeAccessibilityReportJson,
                AfterAccessibilityReportJson: AfterAccessibilityReportJson,
                PageCount: PageCount,
                Autotag: new PdfAutotagMetadata(
                    Provider: FileIngestOptions.AutotagProviders.OpenDataLoader,
                    Required: true,
                    SkippedReason: null,
                    ChunkCount: 1,
                    LocalReportPaths: [Path.Combine(Path.GetTempPath(), "autotag-report.json")]));
        }

        public void Cleanup()
        {
            try
            {
                if (File.Exists(_outputPath))
                {
                    File.Delete(_outputPath);
                }
            }
            catch
            {
                // best-effort test cleanup
            }
        }
    }

    private sealed class ThrowingPdfProcessor : IPdfProcessor
    {
        public Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class PageLimitThrowingPdfProcessor : IPdfProcessor
    {
        public int ActualPageCount { get; } = 31;
        public int MaxAllowedPages { get; } = 25;

        public Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
        {
            throw new PdfPageLimitExceededException(ActualPageCount, MaxAllowedPages);
        }
    }

    private sealed class FakePipelineProcessor : IPdfPipelineProcessor
    {
        public PdfIntakeResult? IntakeResult { get; init; }
        public PdfProcessResult? FinalizeResult { get; init; }
        public int IntakeCalls { get; private set; }
        public int FinalizeCalls { get; private set; }

        public Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Legacy processing should not be called in this test.");
        }

        public Task<PdfIntakeResult> PrepareForQueuedAutotagAsync(
            string fileId,
            Stream pdfStream,
            CancellationToken cancellationToken)
        {
            IntakeCalls++;
            return Task.FromResult(IntakeResult ?? throw new InvalidOperationException("No intake result configured."));
        }

        public Task<PdfProcessResult> FinalizeTaggedPdfAsync(
            string fileId,
            Stream pdfStream,
            PdfFinalizeContext context,
            CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            return Task.FromResult(FinalizeResult ?? throw new InvalidOperationException("No finalize result configured."));
        }
    }

    private sealed class FakeIngestQueueClient : IIngestQueueClient
    {
        public List<AutotagJobMessage> AutotagJobs { get; } = [];
        public List<FinalizePdfMessage> FinalizeMessages { get; } = [];

        public Task EnqueueAutotagJobAsync(AutotagJobMessage message, CancellationToken cancellationToken)
        {
            AutotagJobs.Add(message);
            return Task.CompletedTask;
        }

        public Task EnqueueFinalizePdfAsync(FinalizePdfMessage message, CancellationToken cancellationToken)
        {
            FinalizeMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBlobStorage : IBlobStorage
    {
        public Uri? UploadedTo { get; private set; }
        public Uri? Deleted { get; private set; }
        public List<Uri> Uploads { get; } = [];
        public List<Uri> Deletes { get; } = [];

        public async Task UploadAsync(Uri destinationBlobUri, Stream content, string contentType, CancellationToken cancellationToken)
        {
            UploadedTo = destinationBlobUri;
            Uploads.Add(destinationBlobUri);
            contentType.Should().Be("application/pdf");

            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            ms.Length.Should().BeGreaterThan(0);
        }

        public Task DeleteIfExistsAsync(Uri blobUri, CancellationToken cancellationToken)
        {
            Deleted = blobUri;
            Deletes.Add(blobUri);
            return Task.CompletedTask;
        }
    }
}
