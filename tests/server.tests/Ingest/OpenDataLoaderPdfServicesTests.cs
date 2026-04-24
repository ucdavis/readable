using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Ingest;

namespace server.tests.Ingest;

public sealed class OpenDataLoaderPdfServicesTests
{
    [Fact]
    public async Task AutotagPdfAsync_PostsPdfAndWritesTaggedOutputAndReport()
    {
        var taggedBytes = Encoding.ASCII.GetBytes("%PDF-tagged");
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(taggedBytes)
        });
        var sut = CreateSut(handler);
        var root = CreateTempRoot();

        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            var reportPath = Path.Combine(root, "report.json");
            await File.WriteAllBytesAsync(inputPath, Encoding.ASCII.GetBytes("%PDF-source"));

            var result = await sut.AutotagPdfAsync(inputPath, outputPath, reportPath, CancellationToken.None);

            result.TaggedPdfPath.Should().Be(outputPath);
            var outputBytes = await File.ReadAllBytesAsync(outputPath);
            outputBytes.Should().BeEquivalentTo(taggedBytes);
            var report = await File.ReadAllTextAsync(reportPath);
            report.Should().Contain("\"tool\": \"OpenDataLoader\"");
            report.Should().Contain("\"status\": \"succeeded\"");
            handler.Requests.Should().ContainSingle();
            handler.Requests[0].RequestUri!.PathAndQuery.Should().Be("/convert");
            handler.Requests[0].Headers.GetValues("X-Api-Key").Should().ContainSingle("localsecret");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AutotagPdfAsync_RetriesAfterTooManyRequests()
    {
        var calls = 0;
        var handler = new CapturingHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("try later")
                };
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-tagged"))
            };
        });
        var sut = CreateSut(handler);
        var root = CreateTempRoot();

        try
        {
            var inputPath = Path.Combine(root, "input.pdf");
            var outputPath = Path.Combine(root, "output.pdf");
            var reportPath = Path.Combine(root, "report.json");
            await File.WriteAllBytesAsync(inputPath, Encoding.ASCII.GetBytes("%PDF-source"));

            await sut.AutotagPdfAsync(inputPath, outputPath, reportPath, CancellationToken.None);

            handler.Requests.Should().HaveCount(2);
            File.Exists(outputPath).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OpenDataLoaderPdfServices CreateSut(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ODL_BASE_URL"] = "http://localhost:8082/",
                ["ODL_API_KEY"] = "localsecret",
                ["ODL_MAX_RETRIES"] = "2",
                ["ODL_BASE_DELAY_MS"] = "1",
                ["ODL_TIMEOUT_SECONDS"] = "30",
                ["PDF_SERVICES_CLIENT_ID"] = "unused-for-autotag",
                ["PDF_SERVICES_CLIENT_SECRET"] = "unused-for-autotag"
            })
            .Build();

        return new OpenDataLoaderPdfServices(
            new HttpClient(handler),
            configuration,
            NullLogger<OpenDataLoaderPdfServices>.Instance);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "readable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));
            return Task.FromResult(_handler(request));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
