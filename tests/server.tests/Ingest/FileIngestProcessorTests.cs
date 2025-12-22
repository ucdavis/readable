using FluentAssertions;
using Microsoft.Extensions.Logging;
using server.core.Ingest;

namespace server.tests.Ingest;

public class FileIngestProcessorTests
{
    [Fact]
    public async Task ProcessAsync_CallsBlobOpenerAndPdfProcessor()
    {
        var opener = new FakeBlobStreamOpener();
        var pdf = new FakePdfProcessor();
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var sut = new FileIngestProcessor(opener, pdf, loggerFactory.CreateLogger<FileIngestProcessor>());

        var request = new BlobIngestRequest(
            new Uri("https://example.blob.core.windows.net/incoming/abc123.pdf"),
            "incoming",
            "abc123.pdf",
            "abc123");

        await sut.ProcessAsync(request, CancellationToken.None);

        opener.Seen.Should().Be(request.BlobUri);
        pdf.Calls.Should().Be(1);
        pdf.SeenFileId.Should().Be("abc123");
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

        public Task ProcessAsync(string fileId, Stream pdfStream, CancellationToken cancellationToken)
        {
            Calls++;
            SeenFileId = fileId;
            return Task.CompletedTask;
        }
    }
}

