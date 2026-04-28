using System.Text;
using FluentAssertions;
using iText.IO.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Rasterize;
using server.core.Remediate.Table;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfCharacterEncodingRemediationTests
{
    [Fact]
    public async Task ProcessAsync_FillerReplacementCharacter_PatchesOnlyUsedSourceCode()
    {
        var runRoot = CreateRunRoot("char-encoding-filler");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "612D",
                mappings: new Dictionary<string, string>
                {
                    ["00"] = "FFFD",
                    ["61"] = "FFFD",
                    ["2D"] = "002D",
                });

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor().ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy())
                .Should()
                .Contain("--");

            var cmap = ReadFirstToUnicodeCMap(outputPdf);
            cmap.Should().Contain("<61> <002D>");
            cmap.Should().Contain("<00> <FFFD>", "unused fallback replacement mappings must not be rewritten");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_TimestampInvalidControl_PatchesMissingSourceCodeAsColon()
    {
        var runRoot = CreateRunRoot("char-encoding-timestamp");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "35193139414D",
                mappings: new Dictionary<string, string>
                {
                    ["35"] = "0035",
                    ["31"] = "0031",
                    ["39"] = "0039",
                    ["41"] = "0041",
                    ["4D"] = "004D",
                });

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor().ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy())
                .Should()
                .Contain("5:19AM");

            ReadFirstToUnicodeCMap(outputPdf).Should().Contain("<19> <003A>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_ReplacementCharacterInsideProse_LeavesMappingUnchanged()
    {
        var runRoot = CreateRunRoot("char-encoding-guard");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "506179726F6C6C",
                mappings: new Dictionary<string, string>
                {
                    ["50"] = "0050",
                    ["61"] = "FFFD",
                    ["79"] = "0079",
                    ["72"] = "0072",
                    ["6F"] = "006F",
                    ["6C"] = "006C",
                });

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor().ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy())
                .Should()
                .Contain("P�yroll");

            ReadFirstToUnicodeCMap(outputPdf).Should().Contain("<61> <FFFD>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_AiOptIn_AppliesHighConfidenceRepair()
    {
        var runRoot = CreateRunRoot("char-encoding-ai");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "74657374696E67126D6F6E69746F72696E67",
                mappings: BuildAsciiMappingsExcept("12"));

            var fakeRepairService = new FakeCharacterEncodingRepairService();

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor(fakeRepairService, useAi: true).ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            fakeRepairService.Requests.Should().ContainSingle();

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy())
                .Should()
                .Contain("testing/monitoring");

            ReadFirstToUnicodeCMap(outputPdf).Should().Contain("<12> <002F>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_AiRepairRejected_LeavesMappingUnchanged()
    {
        var runRoot = CreateRunRoot("char-encoding-ai-reject");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "74657374696E67126D6F6E69746F72696E67",
                mappings: BuildAsciiMappingsExcept("12"));

            var fakeRepairService = new FakeCharacterEncodingRepairService(
                new PdfCharacterEncodingRepairProposal("*", "*", "/", 0.50, "Weak evidence."),
                new PdfCharacterEncodingRepairProposal("*", "*", "\u0019", 0.99, "Invalid replacement."));

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor(fakeRepairService, useAi: true).ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var extracted = PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy());
            extracted.Should().Contain("\u0012");
            extracted.Should().NotContain("testing/monitoring");
            ReadFirstToUnicodeCMap(outputPdf).Should().NotContain("<12> <002F>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_CharacterEncodingRunsBeforeUntaggedReturn_AndPreservesTagTree()
    {
        var runRoot = CreateRunRoot("char-encoding-tagged");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "612D",
                mappings: new Dictionary<string, string>
                {
                    ["61"] = "FFFD",
                    ["2D"] = "002D",
                },
                tagged: true);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor().ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            outputPdf.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.StructTreeRoot)
                .Should()
                .NotBeNull();
            PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy())
                .Should()
                .Contain("--");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    private static PdfRemediationProcessor CreateProcessor(
        IPdfCharacterEncodingRepairService? repairService = null,
        bool useAi = false)
        => new(
            new FakeAltTextService(),
            new NoopPdfBookmarkService(),
            NoopPdfPageRasterizer.Instance,
            new SamplePdfTableClassificationService(),
            repairService ?? NoopPdfCharacterEncodingRepairService.Instance,
            new FakePdfTitleService(),
            Options.Create(new PdfRemediationOptions { UseAiCharacterEncodingRepair = useAi }),
            NullLogger<PdfRemediationProcessor>.Instance);

    private static string CreateRunRoot(string name)
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        return runRoot;
    }

    private static Dictionary<string, string> BuildAsciiMappingsExcept(string excludedHex)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var code = 0x20; code <= 0x7E; code++)
        {
            var source = code.ToString("X2");
            if (string.Equals(source, excludedHex, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            mappings[source] = code.ToString("X4");
        }

        return mappings;
    }

    private static void CreatePdfWithCustomToUnicode(
        string outputPath,
        string renderedHex,
        IReadOnlyDictionary<string, string> mappings,
        bool tagged = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();

        var font = new PdfDictionary();
        font.MakeIndirect(pdf);
        font.Put(PdfName.Type, PdfName.Font);
        font.Put(PdfName.Subtype, PdfName.Type1);
        font.Put(PdfName.BaseFont, new PdfName("Helvetica"));
        font.Put(PdfName.Encoding, PdfName.WinAnsiEncoding);

        var toUnicode = new PdfStream(Encoding.ASCII.GetBytes(BuildToUnicodeCMap(mappings)));
        toUnicode.MakeIndirect(pdf);
        font.Put(PdfName.ToUnicode, toUnicode);

        var fonts = new PdfDictionary();
        fonts.Put(new PdfName("F1"), font);

        var resources = new PdfDictionary();
        resources.Put(PdfName.Font, fonts);
        page.GetPdfObject().Put(PdfName.Resources, resources);

        var content = $"BT /F1 12 Tf 72 720 Td <{renderedHex}> Tj ET";
        var contentStream = new PdfStream(Encoding.ASCII.GetBytes(content));
        contentStream.MakeIndirect(pdf);
        page.GetPdfObject().Put(PdfName.Contents, contentStream);

        if (tagged)
        {
            AddMinimalTagTree(pdf);
        }
    }

    private static void AddMinimalTagTree(PdfDocument pdf)
    {
        var catalog = pdf.GetCatalog().GetPdfObject();

        var structTreeRoot = new PdfDictionary();
        structTreeRoot.MakeIndirect(pdf);
        structTreeRoot.Put(PdfName.Type, PdfName.StructTreeRoot);

        var documentElem = new PdfDictionary();
        documentElem.MakeIndirect(pdf);
        documentElem.Put(PdfName.Type, new PdfName("StructElem"));
        documentElem.Put(PdfName.S, new PdfName("Document"));
        documentElem.Put(PdfName.P, structTreeRoot);

        structTreeRoot.Put(PdfName.K, documentElem);

        var markInfo = new PdfDictionary();
        markInfo.MakeIndirect(pdf);
        markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(true));

        catalog.Put(PdfName.MarkInfo, markInfo);
        catalog.Put(PdfName.StructTreeRoot, structTreeRoot);
    }

    private static string BuildToUnicodeCMap(IReadOnlyDictionary<string, string> mappings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/CIDInit /ProcSet findresource begin");
        sb.AppendLine("12 dict begin");
        sb.AppendLine("begincmap");
        sb.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def");
        sb.AppendLine("/CMapName /ReadableTest def");
        sb.AppendLine("/CMapType 2 def");
        sb.AppendLine("1 begincodespacerange");
        sb.AppendLine("<00> <FF>");
        sb.AppendLine("endcodespacerange");
        sb.AppendLine($"{mappings.Count} beginbfchar");
        foreach (var (source, destination) in mappings.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append('<');
            sb.Append(source.ToUpperInvariant());
            sb.Append("> <");
            sb.Append(destination.ToUpperInvariant());
            sb.AppendLine(">");
        }

        sb.AppendLine("endbfchar");
        sb.AppendLine("endcmap");
        sb.AppendLine("CMapName currentdict /CMap defineresource pop");
        sb.AppendLine("end");
        sb.AppendLine("end");
        return sb.ToString();
    }

    private static string ReadFirstToUnicodeCMap(PdfDocument pdf)
    {
        var resources = pdf.GetPage(1).GetPdfObject().GetAsDictionary(PdfName.Resources);
        var fonts = resources!.GetAsDictionary(PdfName.Font);
        var font = fonts!.GetAsDictionary(new PdfName("F1"));
        var toUnicode = font!.GetAsStream(PdfName.ToUnicode);
        return Encoding.ASCII.GetString(toUnicode!.GetBytes());
    }

    private sealed class FakeCharacterEncodingRepairService : IPdfCharacterEncodingRepairService
    {
        private readonly IReadOnlyList<PdfCharacterEncodingRepairProposal> _repairs;

        public FakeCharacterEncodingRepairService(params PdfCharacterEncodingRepairProposal[] repairs)
        {
            _repairs = repairs;
        }

        public List<PdfCharacterEncodingRepairRequest> Requests { get; } = new();

        public Task<PdfCharacterEncodingRepairResponse> ProposeRepairsAsync(
            PdfCharacterEncodingRepairRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            var repairs = _repairs.Count == 0 && request.Groups.Count > 0
                ? [new PdfCharacterEncodingRepairProposal(request.Groups[0].FontObjectId, request.Groups[0].SourceCode, "/", 0.93, "Fake repair.")]
                : _repairs
                    .Select(r => request.Groups.Count == 0 || (r.FontObjectId != "*" && r.SourceCode != "*")
                        ? r
                        : r with
                        {
                            FontObjectId = request.Groups[0].FontObjectId,
                            SourceCode = request.Groups[0].SourceCode,
                        })
                    .ToArray();

            return Task.FromResult(new PdfCharacterEncodingRepairResponse(repairs));
        }
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
}
