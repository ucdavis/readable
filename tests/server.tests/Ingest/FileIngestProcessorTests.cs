using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

        var report = await verify.AccessibilityReports.SingleOrDefaultAsync(
            x => x.FileId == fileId
                && x.Stage == AccessibilityReport.Stages.After
                && x.Tool == "AdobePdfServices");
        report.Should().NotBeNull();
        report!.ReportJson.Should().Be(pdf.AccessibilityReportJson);

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
        public string AccessibilityReportJson { get; } = "{\"kind\":\"test\",\"issues\":[]}";
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

            return new PdfProcessResult(_outputPath, AccessibilityReportJson: AccessibilityReportJson);
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

    private sealed class FakeBlobStorage : IBlobStorage
    {
        public Uri? UploadedTo { get; private set; }
        public Uri? Deleted { get; private set; }

        public async Task UploadAsync(Uri destinationBlobUri, Stream content, string contentType, CancellationToken cancellationToken)
        {
            UploadedTo = destinationBlobUri;
            contentType.Should().Be("application/pdf");

            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            ms.Length.Should().BeGreaterThan(0);
        }

        public Task DeleteIfExistsAsync(Uri blobUri, CancellationToken cancellationToken)
        {
            Deleted = blobUri;
            return Task.CompletedTask;
        }
    }
}
