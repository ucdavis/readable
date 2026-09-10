using System.Collections;
using System.Text.Json;
using FluentAssertions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas.Parser;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using opendataloader.api;
using server.core.Ingest;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Ingest;

public sealed class PdfProcessorOpenDataLoaderExternalTests
{
    private const string ExternalTestFlag = "READABLE_RUN_EXTERNAL_PDF_TESTS";
    private static readonly PdfName StructParentKey = new("StructParent");

    [ExternalFormAltFact]
    public async Task LabelledForm_WithRealAdobe_PassesHidesAnnotationWithoutRegressions()
    {
        var input = Environment.GetEnvironmentVariable("READABLE_EXTERNAL_FORM_PDF")!;
        File.Exists(input).Should().BeTrue("provide the previously processed PDF with the annotation-alt failure");
        var configuration = BuildConfiguration(FindRepoRoot());
        AdobePdfServices.EnsureCredentialsConfigured(configuration);
        var directory = Path.Combine(Path.GetTempPath(), "readable-tests", $"form-alt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var adobe = new AdobePdfServices(configuration, NullLogger<AdobePdfServices>.Instance,
                new NoopAdobePdfServicesRateLimiter());
            var before = await adobe.RunAccessibilityCheckerAsync(input,
                Path.Combine(directory, "before.checked.pdf"), Path.Combine(directory, "before.json"),
                null, null, CancellationToken.None);
            AdobeRuleStatus(before.ReportJson, "Alternate Text", "Hides annotation").Should().Be("Failed");

            var output = Path.Combine(directory, "form.remediated.pdf");
            using (var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(output)))
            {
                // Isolate this cleanup from unrelated title, AI, or re-tagging changes.
                PdfAnnotationRemediator.RemovePlaceholderAltFromLabelledWidgets(pdf, CancellationToken.None)
                    .Should().BeGreaterThan(0);
                PdfAnnotationRemediator.RemovePlaceholderAltFromLabelledWidgets(pdf, CancellationToken.None)
                    .Should().Be(0, "cleanup should be idempotent");
            }
            var after = await adobe.RunAccessibilityCheckerAsync(output,
                Path.Combine(directory, "after.checked.pdf"), Path.Combine(directory, "after.json"),
                null, null, CancellationToken.None);

            var artifactDirectory = Environment.GetEnvironmentVariable("READABLE_EXTERNAL_PDF_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(artifactDirectory))
            {
                Directory.CreateDirectory(artifactDirectory);
                File.Copy(output, Path.Combine(artifactDirectory, "form.remediated.pdf"), overwrite: true);
                await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "form.before.json"), before.ReportJson);
                await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "form.after.json"), after.ReportJson);
            }

            AdobeRuleStatus(after.ReportJson, "Alternate Text", "Hides annotation").Should().Be("Passed");
            using var report = JsonDocument.Parse(before.ReportJson!);
            foreach (var section in report.RootElement.GetProperty("Detailed Report").EnumerateObject())
            {
                foreach (var rule in section.Value.EnumerateArray())
                {
                    if (rule.GetProperty("Status").GetString() == "Passed")
                    {
                        var name = rule.GetProperty("Rule").GetString()!;
                        AdobeRuleStatus(after.ReportJson, section.Name, name).Should().Be("Passed",
                            $"previously passing rule {section.Name}/{name} must not regress");
                    }
                }
            }
            ReadFormWidgetStats(output).Should().Be(ReadFormWidgetStats(input));
            using var originalPdf = new PdfDocument(new PdfReader(input));
            using var outputPdf = new PdfDocument(new PdfReader(output));
            outputPdf.GetNumberOfPages().Should().Be(originalPdf.GetNumberOfPages());
            for (var page = 1; page <= originalPdf.GetNumberOfPages(); page++)
            {
                PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(page)).Should()
                    .Be(PdfTextExtractor.GetTextFromPage(originalPdf.GetPage(page)));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ExternalFormAltFactAttribute : FactAttribute
    {
        public ExternalFormAltFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(ExternalTestFlag) != "1"
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("READABLE_EXTERNAL_FORM_PDF")))
            {
                Skip = $"Set {ExternalTestFlag}=1 and READABLE_EXTERNAL_FORM_PDF to run the Adobe form-alt regression.";
            }
        }
    }

    [ExternalPdfTheory]
    [InlineData("forms.pdf", true)]
    [InlineData("untagged.pdf", false)]
    public async Task Fixture_WithRealOpenDataLoaderAndAdobe_PreservesContentAndTags(
        string fixtureName, bool hasFormFields)
    {
        var repoRoot = FindRepoRoot();
        var inputPdfPath = Path.Combine(repoRoot, "tests", "server.tests", "Fixtures", "pdfs", fixtureName);
        File.Exists(inputPdfPath).Should().BeTrue($"fixture should exist at {inputPdfPath}");

        var configuration = BuildConfiguration(repoRoot);
        AdobePdfServices.EnsureCredentialsConfigured(configuration);

        var odlOptions = OpenDataLoaderOptions.FromConfiguration(configuration);
        RuntimeDependencyProbe.FindOnPath(odlOptions.CommandPath)
            .Should()
            .NotBeNull(
                "external ODL test requires {0} on PATH or ODL_COMMAND_PATH set; " +
                "to avoid installing Java locally, build the worker image and set ODL_COMMAND_PATH=tools/opendataloader-pdf-docker",
                odlOptions.CommandPath);

        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"odl-adobe-{Path.GetFileNameWithoutExtension(fixtureName)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);

        try
        {
            var adobe = new AdobePdfServices(
                configuration,
                NullLogger<AdobePdfServices>.Instance,
                new NoopAdobePdfServicesRateLimiter());
            var useLiveAi = Environment.GetEnvironmentVariable("READABLE_RUN_EXTERNAL_AI_TESTS") == "1";
            IAltTextService altText = useLiveAi
                ? new OpenAIAltTextService(
                    configuration["OPENAI_API_KEY"]!,
                    configuration["OPENAI_ALT_TEXT_MODEL"] ?? "gpt-5-mini",
                    configuration["OPENAI_ENDPOINT"])
                : new FakeAltTextService();
            IPdfTitleService title = useLiveAi
                ? new OpenAIPdfTitleService(
                    configuration["OPENAI_API_KEY"]!,
                    configuration["OPENAI_PDF_TITLE_MODEL"] ?? "gpt-5-mini",
                    configuration["OPENAI_ENDPOINT"])
                : new FakePdfTitleService();
            var remediation = new PdfRemediationProcessor(
                altText,
                new Remediate.NoopPdfBookmarkService(),
                title,
                NullLogger<PdfRemediationProcessor>.Instance);
            var processor = new PdfProcessor(
                adobe,
                remediation,
                Options.Create(new PdfProcessorOptions
                {
                    UseAdobePdfServices = true,
                    UsePdfRemediationProcessor = true,
                    UsePdfBookmarks = false,
                    AutotagTaggedPdfs = false,
                    WorkDirRoot = runRoot,
                }),
                NullLogger<PdfProcessor>.Instance);

            await using var intakeInput = File.OpenRead(inputPdfPath);
            var intake = await processor.PrepareForQueuedAutotagAsync(
                fileId: $"{Path.GetFileNameWithoutExtension(fixtureName)}-fixture-external",
                pdfStream: intakeInput,
                cancellationToken: CancellationToken.None);

            intake.RequiresAutotag.Should().BeTrue("the fixture should follow the ODL retag path");
            if (hasFormFields)
            {
                AdobeRuleStatus(intake.BeforeAccessibilityReportJson, "Forms", "Tagged form fields")
                    .Should().Be("Failed");
            }

            var odlOutputDirectory = Path.Combine(runRoot, "odl-output");
            Directory.CreateDirectory(odlOutputDirectory);
            var odl = new OpenDataLoaderRunner(
                NullLogger<OpenDataLoaderRunner>.Instance,
                odlOptions);

            var odlResult = await odl.ConvertAsync(inputPdfPath, odlOutputDirectory, CancellationToken.None);
            odlResult.ExitCode.Should().Be(0, $"OpenDataLoader stderr: {odlResult.StandardError}");
            odlResult.TaggedPdfPath.Should().NotBeNullOrWhiteSpace("OpenDataLoader should produce a tagged PDF");
            File.Exists(odlResult.TaggedPdfPath!).Should().BeTrue();

            await using var taggedInput = File.OpenRead(odlResult.TaggedPdfPath!);
            var finalized = await processor.FinalizeTaggedPdfAsync(
                fileId: $"{Path.GetFileNameWithoutExtension(fixtureName)}-fixture-external",
                pdfStream: taggedInput,
                context: new PdfFinalizeContext(intake.PageCount, intake.Autotag),
                cancellationToken: CancellationToken.None);

            File.Exists(finalized.OutputPdfPath).Should().BeTrue();
            using var sourcePdf = new PdfDocument(new PdfReader(inputPdfPath));
            using var finalPdf = new PdfDocument(new PdfReader(finalized.OutputPdfPath));
            finalPdf.GetNumberOfPages().Should().Be(sourcePdf.GetNumberOfPages());
            finalPdf.IsTagged().Should().BeTrue();
            finalPdf.GetDocumentInfo().GetTitle().Should().NotBeNullOrWhiteSpace();
            if (useLiveAi && !hasFormFields)
            {
                finalPdf.GetDocumentInfo().GetTitle().Should()
                    .NotBe("fake title").And.NotBe("Untitled PDF document");
            }
            for (var page = 1; page <= sourcePdf.GetNumberOfPages(); page++)
            {
                var sourceText = PdfTextExtractor.GetTextFromPage(sourcePdf.GetPage(page));
                var finalText = PdfTextExtractor.GetTextFromPage(finalPdf.GetPage(page));
                finalText.Should().Be(sourceText, $"page {page} text must survive tagging and remediation");
            }

            if (hasFormFields)
            {
                var sourceStats = ReadFormWidgetStats(inputPdfPath);
                var finalStats = ReadFormWidgetStats(finalized.OutputPdfPath);
                finalStats.AcroFormFieldCount.Should().Be(sourceStats.AcroFormFieldCount).And.BeGreaterThan(0);
                finalStats.WidgetAnnotationCount.Should().Be(sourceStats.WidgetAnnotationCount).And.BeGreaterThan(0);
                finalStats.UnassociatedWidgetAnnotationCount.Should()
                    .Be(0, "all form widget annotations should be associated with the structure parent tree");
                AdobeRuleStatus(finalized.AfterAccessibilityReportJson, "Forms", "Tagged form fields")
                    .Should().Be("Passed");
            }

            // Opt in to retaining outputs for visual inspection after the test.
            var artifactDirectory = Environment.GetEnvironmentVariable("READABLE_EXTERNAL_PDF_ARTIFACT_DIR");
            if (!string.IsNullOrWhiteSpace(artifactDirectory))
            {
                Directory.CreateDirectory(artifactDirectory);
                var stem = Path.GetFileNameWithoutExtension(fixtureName);
                File.Copy(finalized.OutputPdfPath, Path.Combine(artifactDirectory, $"{stem}.remediated.pdf"), overwrite: true);
                await File.WriteAllTextAsync(Path.Combine(artifactDirectory, $"{stem}.before.json"), intake.BeforeAccessibilityReportJson);
                await File.WriteAllTextAsync(Path.Combine(artifactDirectory, $"{stem}.after.json"), finalized.AfterAccessibilityReportJson);
            }
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }
        }
    }

    private sealed record FormWidgetStats(
        int AcroFormFieldCount,
        int WidgetAnnotationCount,
        int UnassociatedWidgetAnnotationCount);

    private sealed class ExternalPdfTheoryAttribute : TheoryAttribute
    {
        public ExternalPdfTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable(ExternalTestFlag) != "1")
            {
                Skip = $"Set {ExternalTestFlag}=1 to run real OpenDataLoader and Adobe checks.";
            }
        }
    }

    private static IConfiguration BuildConfiguration(string repoRoot)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        LoadEnvFile(Path.Combine(repoRoot, "server", ".env"), values);
        LoadEnvFile(Path.Combine(repoRoot, ".env"), values);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                values[key] = entry.Value?.ToString();
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static void LoadEnvFile(string path, Dictionary<string, string?> values)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            var key = line[..equalsIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            values[key] = line[(equalsIndex + 1)..].Trim().Trim('"', '\'');
        }
    }

    private static string? AdobeRuleStatus(string? reportJson, string sectionName, string ruleName)
    {
        if (string.IsNullOrWhiteSpace(reportJson))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(reportJson);
        if (!doc.RootElement.TryGetProperty("Detailed Report", out var detailedReport) ||
            !detailedReport.TryGetProperty(sectionName, out var section) ||
            section.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in section.EnumerateArray())
        {
            if (item.TryGetProperty("Rule", out var rule) &&
                string.Equals(rule.GetString(), ruleName, StringComparison.Ordinal) &&
                item.TryGetProperty("Status", out var status))
            {
                return status.GetString();
            }
        }

        return null;
    }

    private static FormWidgetStats ReadFormWidgetStats(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));

        var parentTree = TryGetParentTree(pdf);
        var widgetAnnotationCount = 0;
        var unassociatedWidgetAnnotationCount = 0;

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            foreach (var annotation in pdf.GetPage(pageNumber).GetAnnotations())
            {
                if (!PdfName.Widget.Equals(annotation.GetSubtype()))
                {
                    continue;
                }

                widgetAnnotationCount++;

                var structParent = annotation.GetPdfObject().GetAsNumber(StructParentKey)?.IntValue();
                if (structParent is null || parentTree is null || !NumberTreeContainsKey(parentTree, structParent.Value))
                {
                    unassociatedWidgetAnnotationCount++;
                }
            }
        }

        return new FormWidgetStats(
            CountAcroFormFields(pdf),
            widgetAnnotationCount,
            unassociatedWidgetAnnotationCount);
    }

    private static int CountAcroFormFields(PdfDocument pdf)
    {
        var acroForm = pdf.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm);
        return acroForm?.GetAsArray(PdfName.Fields)?.Size() ?? 0;
    }

    private static PdfDictionary? TryGetParentTree(PdfDocument pdf)
    {
        var catalogDict = pdf.GetCatalog().GetPdfObject();
        var structTreeRootDict = catalogDict.GetAsDictionary(PdfName.StructTreeRoot);
        return structTreeRootDict?.GetAsDictionary(PdfName.ParentTree);
    }

    private static bool NumberTreeContainsKey(PdfDictionary numberTree, int key)
    {
        var visited = new HashSet<(int objNum, int genNum)>();
        return NumberTreeContainsKeyRecursive(numberTree, key, visited);
    }

    private static bool NumberTreeContainsKeyRecursive(
        PdfDictionary node,
        int key,
        HashSet<(int objNum, int genNum)> visited)
    {
        var nodeRef = node.GetIndirectReference();
        if (nodeRef is not null)
        {
            var refKey = (nodeRef.GetObjNumber(), nodeRef.GetGenNumber());
            if (!visited.Add(refKey))
            {
                return false;
            }
        }

        var nums = node.GetAsArray(PdfName.Nums);
        if (nums is not null)
        {
            for (var i = 0; i + 1 < nums.Size(); i += 2)
            {
                if (nums.GetAsNumber(i)?.IntValue() == key)
                {
                    return true;
                }
            }

            return false;
        }

        var kids = node.GetAsArray(PdfName.Kids);
        if (kids is null)
        {
            return false;
        }

        for (var i = 0; i < kids.Size(); i++)
        {
            var kidObj = Dereference(kids.Get(i));
            if (kidObj is not PdfDictionary kidDict)
            {
                continue;
            }

            var limits = kidDict.GetAsArray(PdfName.Limits);
            if (limits is not null && limits.Size() >= 2)
            {
                var low = limits.GetAsNumber(0)?.IntValue();
                var high = limits.GetAsNumber(1)?.IntValue();
                if (low is not null && high is not null && (key < low.Value || key > high.Value))
                {
                    continue;
                }
            }

            if (NumberTreeContainsKeyRecursive(kidDict, key, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static PdfObject Dereference(PdfObject obj)
    {
        return obj is PdfIndirectReference reference
            ? reference.GetRefersTo(true) ?? new PdfNull()
            : obj;
    }

    private sealed class FakeAltTextService : IAltTextService
    {
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("fake image alt text");
        }

        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("fake link alt text");
        }

        public string GetFallbackAltTextForImage() => "fake image alt text";

        public string GetFallbackAltTextForLink() => "fake link alt text";
    }

    private sealed class FakePdfTitleService : IPdfTitleService
    {
        public Task<string> GenerateTitleAsync(PdfTitleRequest request, CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("fake title");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "app.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("Unable to locate repo root (missing app.sln).");
        }

        return dir.FullName;
    }
}
