using FluentAssertions;
using opendataloader.api;

namespace server.tests.Workers;

public sealed class OpenDataLoaderRunnerTests
{
    [Fact]
    public void BuildArguments_WithoutHybrid_UsesTaggedPdfAndQuiet()
    {
        var options = new OpenDataLoaderOptions
        {
            OutputFormat = "tagged-pdf",
            CommandPath = "opendataloader-pdf"
        };

        var args = OpenDataLoaderRunner.BuildArguments(options, "/tmp/input.pdf", "/tmp/output");

        args.Should().Equal(
            "/tmp/input.pdf",
            "--output-dir",
            "/tmp/output",
            "--format",
            "tagged-pdf",
            "--quiet");
    }

    [Fact]
    public void BuildArguments_WithHybridUrl_AddsHybridFlags()
    {
        var options = new OpenDataLoaderOptions
        {
            OutputFormat = "tagged-pdf",
            HybridBackend = "docling-fast",
            HybridUrl = "http://127.0.0.1:5002"
        };

        var args = OpenDataLoaderRunner.BuildArguments(options, "/tmp/input.pdf", "/tmp/output");

        args.Should().Equal(
            "/tmp/input.pdf",
            "--output-dir",
            "/tmp/output",
            "--format",
            "tagged-pdf",
            "--quiet",
            "--hybrid",
            "docling-fast",
            "--hybrid-url",
            "http://127.0.0.1:5002");
    }

    [Fact]
    public void FindTaggedPdfPath_FallsBackToTaggedArtifactForCurrentInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "readable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            var taggedPath = Path.Combine(outputDir, "input_custom_name_tagged.pdf");
            File.WriteAllBytes(taggedPath, []);

            var resolved = OpenDataLoaderRunner.FindTaggedPdfPath(
                Path.Combine(root, "input.pdf"),
                outputDir);

            resolved.Should().Be(taggedPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void FindTaggedPdfPath_IgnoresTaggedArtifactForDifferentInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "readable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var outputDir = Path.Combine(root, "output");
            Directory.CreateDirectory(outputDir);

            File.WriteAllBytes(Path.Combine(outputDir, "stale_tagged.pdf"), []);

            var resolved = OpenDataLoaderRunner.FindTaggedPdfPath(
                Path.Combine(root, "input.pdf"),
                outputDir);

            resolved.Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
