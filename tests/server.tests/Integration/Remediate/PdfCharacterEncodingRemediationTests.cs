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
                new PdfCharacterEncodingRepairProposal("*", "*", "/", 0.49, "Weak evidence."),
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
    public async Task ProcessAsync_MissingToUnicodeOnly_FillsConsistentObservedTextWithoutAi()
    {
        var runRoot = CreateRunRoot("char-encoding-missing-only");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "414243",
                mappings: new Dictionary<string, string>
                {
                    ["41"] = "0041",
                    ["43"] = "0043",
                });

            var fakeRepairService = new FakeCharacterEncodingRepairService(
                new PdfCharacterEncodingRepairProposal("*", "*", "B", 0.99, "Should not be requested."));

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor(fakeRepairService, useAi: true).ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            fakeRepairService.Requests.Should().BeEmpty();

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            ReadFirstToUnicodeCMap(outputPdf).Should().Contain("<42> <0042>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_BfRangeToUnicodeMappings_AreNotClassifiedAsMissingCoverage()
    {
        var runRoot = CreateRunRoot("char-encoding-bfrange");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "414243",
                mappings: new Dictionary<string, string>(),
                toUnicodeCMap: BuildToUnicodeBfRangeCMap("<41> <43> <0041>"));

            var fakeRepairService = new FakeCharacterEncodingRepairService(
                new PdfCharacterEncodingRepairProposal("*", "*", "X", 0.99, "Should not be requested."));

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor(fakeRepairService, useAi: true).ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            fakeRepairService.Requests.Should().BeEmpty();

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy())
                .Should()
                .Contain("ABC");
            ReadFirstToUnicodeCMap(outputPdf).Should().NotContain("beginbfchar");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_WingdingsPrivateUse_PatchesKnownUnicodeSymbol()
    {
        var runRoot = CreateRunRoot("char-encoding-wingdings");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "99",
                mappings: new Dictionary<string, string>
                {
                    ["99"] = "F076",
                },
                baseFontName: "Wingdings");

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor().ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            PdfTextExtractor.GetTextFromPage(outputPdf.GetPage(1), new SimpleTextExtractionStrategy())
                .Should()
                .Contain("\u2756");

            ReadFirstToUnicodeCMap(outputPdf).Should().Contain("<99> <2756>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_TrueTypeSubsetWithoutEncoding_AddsEncodingDictionaryFromObservedText()
    {
        var runRoot = CreateRunRoot("char-encoding-simple-font-encoding");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithCustomToUnicode(
                inputPdfPath,
                renderedHex: "212223",
                mappings: new Dictionary<string, string>
                {
                    ["21"] = "0041",
                    ["22"] = "0020",
                    ["23"] = "0031",
                },
                includeEncoding: false,
                baseFontName: "AAAAAA+TimesNewRomanPSMT",
                fontSubtype: new PdfName("TrueType"));

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor().ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var font = outputPdf.GetPage(1)
                .GetPdfObject()
                .GetAsDictionary(PdfName.Resources)!
                .GetAsDictionary(PdfName.Font)!
                .GetAsDictionary(new PdfName("F1"))!;

            var encoding = font.GetAsDictionary(PdfName.Encoding);
            encoding.Should().NotBeNull();
            encoding!.GetAsName(new PdfName("BaseEncoding")).Should().Be(PdfName.WinAnsiEncoding);
            encoding.GetAsArray(new PdfName("Differences"))!.ToString()
                .Should()
                .Contain("/A")
                .And.Contain("/space")
                .And.Contain("/one");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_CidFontType2WithoutCidToGidMap_AddsIdentityMap()
    {
        var runRoot = CreateRunRoot("char-encoding-cid-to-gid");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithType0CidFont(inputPdfPath);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor().ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var font = outputPdf.GetPage(1)
                .GetPdfObject()
                .GetAsDictionary(PdfName.Resources)!
                .GetAsDictionary(PdfName.Font)!
                .GetAsDictionary(new PdfName("F1"))!;
            var descendant = font.GetAsArray(PdfName.DescendantFonts)!.GetAsDictionary(0)!;
            descendant.GetAsName(new PdfName("CIDToGIDMap")).Should().Be(PdfName.Identity);
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_AiActualTextRepair_AddsActualTextToMalformedMarkedContent()
    {
        await Task.CompletedTask;

        var patched = AddOrReplaceActualTextForMcid(
            "/P <</MCID 7>> BDC BT /F1 12 Tf 72 720 Td <61> Tj ET EMC",
            7,
            "applicable");

        patched.Should().Contain("/ActualText <FEFF006100700070006C0069006300610062006C0065>");
    }

    [Fact]
    public async Task AddOrReplaceActualTextForMcid_ReplacesHexActualTextInDictionary()
    {
        await Task.CompletedTask;

        var patched = AddOrReplaceActualTextForMcid(
            "/P <</Lang <656E2D5553> /MCID 7 /ActualText <FEFFFFFFD>>> BDC BT /F1 12 Tf 72 720 Td <61> Tj ET EMC",
            7,
            "applicable");

        patched.Should().Contain("/Lang <656E2D5553>");
        patched.Should().Contain("/ActualText <FEFF006100700070006C0069006300610062006C0065>");
        patched.Should().NotContain("/ActualText <FEFFFFFFD>");
    }

    [Fact]
    public void ParseExplicitBfCharMappings_IncludesBfRangeMappings()
    {
        var mappings = ParseExplicitBfCharMappings(
            """
            begincmap
            2 beginbfrange
            <41> <43> <0041>
            <61> <63> [<0061> <00620062> <0063>]
            endbfrange
            endcmap
            """);

        mappings.Should().Contain("41", "A");
        mappings.Should().Contain("42", "B");
        mappings.Should().Contain("43", "C");
        mappings.Should().Contain("61", "a");
        mappings.Should().Contain("62", "bb");
        mappings.Should().Contain("63", "c");
    }

    [Fact]
    public async Task ProcessAsync_MalformedExistingActualText_ReplacesHexActualText()
    {
        var runRoot = CreateRunRoot("char-encoding-existing-actualtext");
        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithMarkedContentText(
                inputPdfPath,
                renderedHex: "6170706C696361626C65",
                mappings: BuildAsciiMappingsExcept("00"),
                mcid: 7,
                dictionarySuffix: " /ActualText <FEFF006100700070006CFFFD006300610062006C0065>");

            var fakeRepairService = FakeCharacterEncodingRepairService.WithActualTextRepairs(
                new PdfCharacterEncodingActualTextRepairProposal(1, 7, "applicable", 0.99, "Clean replacement."));

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor(fakeRepairService, useAi: true).ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            fakeRepairService.ActualTextRequests.Should().ContainSingle();

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var content = Encoding.Latin1.GetString(outputPdf.GetPage(1).GetContentBytes());
            content.Should().Contain("/ActualText <FEFF006100700070006C0069006300610062006C0065>");
            content.Should().NotContain("/ActualText <FEFF006100700070006CFFFD006300610062006C0065>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public void ActualTextRepairValidation_RejectsBadReplacementText()
    {
        IsSafeActualTextReplacement("applicable").Should().BeTrue();
        IsSafeActualTextReplacement("\uFFFD").Should().BeFalse();
        IsSafeActualTextReplacement("bad\u0019text").Should().BeFalse();
    }

    [Fact]
    public void OpenAiCharacterEncodingRepairPrompt_IncludesBeforeAndAfterContext()
    {
        var prompt = BuildOpenAiCharacterEncodingRepairPrompt(
            new PdfCharacterEncodingRepairRequest(
                [
                    new PdfCharacterEncodingAnomalyGroup(
                        "7 0 R",
                        "F1",
                        "12",
                        "invalid_control",
                        [
                            new PdfCharacterEncodingAnomalyContext(
                                1,
                                3,
                                "testing<?>monitoring",
                                "before signal",
                                "after signal"),
                        ]),
                ],
                "en-US"));

        prompt.Should().Contain("Page 1 mcid=3: testing<?>monitoring");
        prompt.Should().Contain("contextBefore: before signal");
        prompt.Should().Contain("contextAfter: after signal");
    }

    [Fact]
    public async Task ProcessAsync_AnimalChecklistRegression_RetriesOmittedActualTextRepairForQaRodac()
    {
        var runRoot = CreateRunRoot("char-encoding-animal-regression");
        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithMultipleMalformedMarkedContentWords(inputPdfPath);

            var fakeRepairService = FakeCharacterEncodingRepairService.WithActualTextRepairResponses(
                [
                    new PdfCharacterEncodingActualTextRepairProposal(1, 136, "Quarterly", 0.95, "First batch proposal."),
                    new PdfCharacterEncodingActualTextRepairProposal(1, 152, "if applicable", 0.99, "First batch proposal."),
                    new PdfCharacterEncodingActualTextRepairProposal(1, 304, "monitoring", 0.90, "First batch proposal."),
                ],
                [
                    new PdfCharacterEncodingActualTextRepairProposal(1, 227, "QA/RODAC", 0.90, "Focused retry proposal."),
                ]);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor(fakeRepairService, useAi: true).ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            fakeRepairService.ActualTextRequests
                .SelectMany(r => r.Issues)
                .Should()
                .Contain(i => i.Mcid == 227 && i.RawText.Contains("odac", StringComparison.OrdinalIgnoreCase));
            fakeRepairService.ActualTextRequests.Should().HaveCount(2);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            Encoding.Latin1.GetString(outputPdf.GetPage(1).GetContentBytes())
                .Should()
                .Contain("/ActualText <FEFF00510041002F0052004F004400410043>");
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessAsync_AnimalChecklistRegression_SanitizesQaRodacWhenAiOmitsIt()
    {
        var runRoot = CreateRunRoot("char-encoding-animal-sanitize");
        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            CreatePdfWithMultipleMalformedMarkedContentWords(inputPdfPath);

            var fakeRepairService = FakeCharacterEncodingRepairService.WithActualTextRepairResponses(
                [
                    new PdfCharacterEncodingActualTextRepairProposal(1, 136, "Quarterly", 0.95, "First batch proposal."),
                    new PdfCharacterEncodingActualTextRepairProposal(1, 152, "if applicable", 0.99, "First batch proposal."),
                    new PdfCharacterEncodingActualTextRepairProposal(1, 304, "monitoring", 0.90, "First batch proposal."),
                ],
                []);

            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            await CreateProcessor(fakeRepairService, useAi: true).ProcessAsync("fixture", inputPdfPath, outputPdfPath, CancellationToken.None);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            Encoding.Latin1.GetString(outputPdf.GetPage(1).GetContentBytes())
                .Should()
                .Contain("/ActualText <FEFF002C0020006F006400610063>");
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
        bool tagged = false,
        string baseFontName = "Helvetica",
        bool includeEncoding = true,
        PdfName? fontSubtype = null,
        string? toUnicodeCMap = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();

        var font = new PdfDictionary();
        font.MakeIndirect(pdf);
        font.Put(PdfName.Type, PdfName.Font);
        font.Put(PdfName.Subtype, fontSubtype ?? PdfName.Type1);
        font.Put(PdfName.BaseFont, new PdfName(baseFontName));
        if (new PdfName("TrueType").Equals(fontSubtype))
        {
            var bytes = Convert.FromHexString(renderedHex);
            var firstChar = bytes.Min(b => (int)b);
            var lastChar = bytes.Max(b => (int)b);
            font.Put(PdfName.FirstChar, new PdfNumber(firstChar));
            font.Put(PdfName.LastChar, new PdfNumber(lastChar));

            var widths = new PdfArray();
            for (var code = firstChar; code <= lastChar; code++)
            {
                widths.Add(new PdfNumber(500));
            }

            font.Put(PdfName.Widths, widths);
        }

        if (includeEncoding)
        {
            font.Put(PdfName.Encoding, PdfName.WinAnsiEncoding);
        }

        var toUnicode = new PdfStream(Encoding.ASCII.GetBytes(toUnicodeCMap ?? BuildToUnicodeCMap(mappings)));
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

    private static void CreatePdfWithMarkedContentText(
        string outputPath,
        string renderedHex,
        IReadOnlyDictionary<string, string> mappings,
        int mcid,
        string dictionarySuffix = "")
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

        var content = $"BT /P <</MCID {mcid}{dictionarySuffix}>> BDC /F1 12 Tf 72 720 Td <{renderedHex}> Tj EMC ET";
        var contentStream = new PdfStream(Encoding.ASCII.GetBytes(content));
        contentStream.MakeIndirect(pdf);
        page.GetPdfObject().Put(PdfName.Contents, contentStream);

        AddTagTreeForMarkedContent(pdf, page.GetPdfObject(), mcid);
    }

    private static void CreatePdfWithMultipleMalformedMarkedContentWords(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();

        var mappings = BuildAsciiMappingsExcept("A0");
        for (var code = 0xA0; code <= 0xAF; code++)
        {
            mappings[code.ToString("X2")] = "FFFD";
        }

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

        var content = string.Join(
            "\n",
            "BT /F1 12 Tf 72 720 Td",
            "/P <</MCID 136>> BDC <A0A171727465726C79> Tj EMC",
            "0 -16 Td /P <</MCID 152>> BDC <6966206170706C696361A26C65> Tj EMC",
            "0 -16 Td /P <</MCID 227>> BDC <2C20A3A4A5A66F646163> Tj EMC",
            "0 -16 Td /P <</MCID 304>> BDC <A7A8A9AAABACADAE> Tj EMC",
            "ET");
        var contentStream = new PdfStream(Encoding.ASCII.GetBytes(content));
        contentStream.MakeIndirect(pdf);
        page.GetPdfObject().Put(PdfName.Contents, contentStream);
    }

    private static void AddTagTreeForMarkedContent(PdfDocument pdf, PdfDictionary pageDict, int mcid)
    {
        var catalog = pdf.GetCatalog().GetPdfObject();

        var structTreeRoot = new PdfDictionary();
        structTreeRoot.MakeIndirect(pdf);
        structTreeRoot.Put(PdfName.Type, PdfName.StructTreeRoot);

        var parentTree = new PdfDictionary();
        parentTree.MakeIndirect(pdf);
        structTreeRoot.Put(PdfName.ParentTree, parentTree);

        var documentElem = new PdfDictionary();
        documentElem.MakeIndirect(pdf);
        documentElem.Put(PdfName.Type, new PdfName("StructElem"));
        documentElem.Put(PdfName.S, new PdfName("Document"));
        documentElem.Put(PdfName.P, structTreeRoot);

        var paragraphElem = new PdfDictionary();
        paragraphElem.MakeIndirect(pdf);
        paragraphElem.Put(PdfName.Type, new PdfName("StructElem"));
        paragraphElem.Put(PdfName.S, PdfName.P);
        paragraphElem.Put(PdfName.P, documentElem);
        paragraphElem.Put(PdfName.Pg, pageDict);

        var markedContentRef = new PdfDictionary();
        markedContentRef.Put(PdfName.Type, new PdfName("MCR"));
        markedContentRef.Put(PdfName.Pg, pageDict);
        markedContentRef.Put(PdfName.MCID, new PdfNumber(mcid));
        paragraphElem.Put(PdfName.K, markedContentRef);

        var kids = new PdfArray();
        kids.Add(paragraphElem);
        documentElem.Put(PdfName.K, kids);
        structTreeRoot.Put(PdfName.K, documentElem);

        var markInfo = new PdfDictionary();
        markInfo.MakeIndirect(pdf);
        markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(true));

        catalog.Put(PdfName.MarkInfo, markInfo);
        catalog.Put(PdfName.StructTreeRoot, structTreeRoot);
    }

    private static void CreatePdfWithType0CidFont(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();

        var descriptor = new PdfDictionary();
        descriptor.MakeIndirect(pdf);
        descriptor.Put(PdfName.Type, PdfName.FontDescriptor);
        descriptor.Put(PdfName.FontName, new PdfName("AAAAAA+TestCIDFont"));
        descriptor.Put(PdfName.Flags, new PdfNumber(4));
        descriptor.Put(PdfName.FontBBox, new PdfArray(new[] { new PdfNumber(0), new PdfNumber(0), new PdfNumber(1000), new PdfNumber(1000) }));
        descriptor.Put(PdfName.Ascent, new PdfNumber(800));
        descriptor.Put(PdfName.Descent, new PdfNumber(-200));
        descriptor.Put(PdfName.CapHeight, new PdfNumber(700));
        descriptor.Put(PdfName.StemV, new PdfNumber(80));

        var descendant = new PdfDictionary();
        descendant.MakeIndirect(pdf);
        descendant.Put(PdfName.Type, PdfName.Font);
        descendant.Put(PdfName.Subtype, new PdfName("CIDFontType2"));
        descendant.Put(PdfName.BaseFont, new PdfName("AAAAAA+TestCIDFont"));
        var cidSystemInfo = new PdfDictionary();
        cidSystemInfo.Put(PdfName.Registry, new PdfString("Adobe"));
        cidSystemInfo.Put(PdfName.Ordering, new PdfString("Identity"));
        cidSystemInfo.Put(PdfName.Supplement, new PdfNumber(0));
        descendant.Put(PdfName.CIDSystemInfo, cidSystemInfo);
        descendant.Put(PdfName.FontDescriptor, descriptor);

        var toUnicode = new PdfStream(Encoding.ASCII.GetBytes(BuildToUnicodeCMap(new Dictionary<string, string>
        {
            ["0041"] = "0041",
        })));
        toUnicode.MakeIndirect(pdf);

        var font = new PdfDictionary();
        font.MakeIndirect(pdf);
        font.Put(PdfName.Type, PdfName.Font);
        font.Put(PdfName.Subtype, PdfName.Type0);
        font.Put(PdfName.BaseFont, new PdfName("AAAAAA+TestCIDFont"));
        font.Put(PdfName.Encoding, PdfName.IdentityH);
        var descendants = new PdfArray();
        descendants.Add(descendant);
        font.Put(PdfName.DescendantFonts, descendants);
        font.Put(PdfName.ToUnicode, toUnicode);

        var fonts = new PdfDictionary();
        fonts.Put(new PdfName("F1"), font);

        var resources = new PdfDictionary();
        resources.Put(PdfName.Font, fonts);
        page.GetPdfObject().Put(PdfName.Resources, resources);

        var content = "BT /F1 12 Tf 72 720 Td <0041> Tj ET";
        var contentStream = new PdfStream(Encoding.ASCII.GetBytes(content));
        contentStream.MakeIndirect(pdf);
        page.GetPdfObject().Put(PdfName.Contents, contentStream);
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

    private static string BuildToUnicodeBfRangeCMap(params string[] ranges)
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
        sb.AppendLine($"{ranges.Length} beginbfrange");
        foreach (var range in ranges)
        {
            sb.AppendLine(range);
        }

        sb.AppendLine("endbfrange");
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

    private static string AddOrReplaceActualTextForMcid(string content, int mcid, string actualText)
        => (string)GetCharacterEncodingRemediatorMethod(nameof(AddOrReplaceActualTextForMcid))
            .Invoke(null, [content, mcid, actualText])!;

    private static IReadOnlyDictionary<string, string> ParseExplicitBfCharMappings(string cmap)
        => (IReadOnlyDictionary<string, string>)GetCharacterEncodingRemediatorMethod(nameof(ParseExplicitBfCharMappings))
            .Invoke(null, [cmap])!;

    private static bool IsSafeActualTextReplacement(string actualText)
        => (bool)GetCharacterEncodingRemediatorMethod(nameof(IsSafeActualTextReplacement))
            .Invoke(null, [actualText])!;

    private static System.Reflection.MethodInfo GetCharacterEncodingRemediatorMethod(string name)
    {
        var type = typeof(PdfRemediationProcessor).Assembly.GetType("server.core.Remediate.PdfCharacterEncodingRemediator")!;
        return type.GetMethod(
            name,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    }

    private static string BuildOpenAiCharacterEncodingRepairPrompt(PdfCharacterEncodingRepairRequest request)
    {
        var type = typeof(OpenAIPdfCharacterEncodingRepairService);
        return (string)type.GetMethod(
            "BuildPrompt",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [request])!;
    }

    private sealed class FakeCharacterEncodingRepairService : IPdfCharacterEncodingRepairService
    {
        private readonly IReadOnlyList<PdfCharacterEncodingRepairProposal> _repairs;
        private readonly IReadOnlyList<PdfCharacterEncodingActualTextRepairProposal> _actualTextRepairs;
        private readonly Queue<IReadOnlyList<PdfCharacterEncodingActualTextRepairProposal>> _actualTextRepairResponses;

        public FakeCharacterEncodingRepairService(params PdfCharacterEncodingRepairProposal[] repairs)
            : this(repairs, Array.Empty<PdfCharacterEncodingActualTextRepairProposal>())
        {
        }

        private FakeCharacterEncodingRepairService(
            IReadOnlyList<PdfCharacterEncodingRepairProposal> repairs,
            IReadOnlyList<PdfCharacterEncodingActualTextRepairProposal> actualTextRepairs)
        {
            _repairs = repairs;
            _actualTextRepairs = actualTextRepairs;
            _actualTextRepairResponses = new Queue<IReadOnlyList<PdfCharacterEncodingActualTextRepairProposal>>();
        }

        public static FakeCharacterEncodingRepairService WithActualTextRepairs(
            params PdfCharacterEncodingActualTextRepairProposal[] repairs)
            => new(Array.Empty<PdfCharacterEncodingRepairProposal>(), repairs);

        public static FakeCharacterEncodingRepairService WithActualTextRepairResponses(
            params IReadOnlyList<PdfCharacterEncodingActualTextRepairProposal>[] responses)
        {
            var service = new FakeCharacterEncodingRepairService(
                Array.Empty<PdfCharacterEncodingRepairProposal>(),
                Array.Empty<PdfCharacterEncodingActualTextRepairProposal>());
            foreach (var response in responses)
            {
                service._actualTextRepairResponses.Enqueue(response);
            }

            return service;
        }

        public List<PdfCharacterEncodingRepairRequest> Requests { get; } = new();

        public List<PdfCharacterEncodingActualTextRepairRequest> ActualTextRequests { get; } = new();

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

        public Task<PdfCharacterEncodingActualTextRepairResponse> ProposeActualTextRepairsAsync(
            PdfCharacterEncodingActualTextRepairRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActualTextRequests.Add(request);
            var repairs = _actualTextRepairResponses.Count > 0
                ? _actualTextRepairResponses.Dequeue()
                : _actualTextRepairs;
            return Task.FromResult(new PdfCharacterEncodingActualTextRepairResponse(repairs));
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
