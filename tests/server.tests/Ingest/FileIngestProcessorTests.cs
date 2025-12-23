using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using server.core.Ingest;

namespace server.tests.Ingest;

public class FileIngestProcessorTests
{
    [Fact]
    public async Task ProcessAsync_CallsBlobOpenerAndPdfProcessor_UploadsProcessedAndDeletesIncoming()
    {
        var opener = new FakeBlobStreamOpener();
        var pdf = new FakePdfProcessor();
        var blobStorage = new FakeBlobStorage();
        var configuration = new ConfigurationBuilder().Build();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(
            opener,
            pdf,
            blobStorage,
            configuration,
            loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/abc123.pdf"),
            "incoming",
            "abc123.pdf",
            "abc123");

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
        pdf.SeenFileId.Should().Be("abc123");
        blobStorage.UploadedTo.Should().Be(new Uri("https://example.blob.core.windows.net/processed/abc123.pdf"));
        blobStorage.Deleted.Should().Be(request.BlobUri);
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
        public int Calls { get; private set; }
        public string? SeenFileId { get; private set; }
        private readonly string _outputPath = Path.Combine(Path.GetTempPath(), $"readable-test-{Guid.NewGuid():N}.pdf");

        public async Task<PdfProcessResult> ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
        {
            Calls++;
            SeenFileId = fileId;

            await using (var output = File.Create(_outputPath))
            {
                await output.WriteAsync("%PDF-1.7\n%final"u8.ToArray(), cancellationToken);
            }

            return new PdfProcessResult(_outputPath);
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
