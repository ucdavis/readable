using FluentAssertions;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Tagging;
using iText.Kernel.Pdf.Xobject;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Bookmarks;
using server.core.Remediate.Figures;
using server.core.Remediate.Rasterize;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfImagePurposeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"image-purpose-{Guid.NewGuid():N}");
    public PdfImagePurposeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task RepeatedImageInParagraph_ClassifiesEachOccurrence_PreservesTextAndParentTree_IsIdempotent()
    {
        var service = new Classifier(new(ImagePurpose.Meaningful, 0.79, "", "Decorative copy"), new(ImagePurpose.Meaningful, 0.80, "Informative symbol", "Independent content"));
        var output = await Process(CreateInput(), service);
        service.Requests.Should().HaveCount(2);
        service.Requests[0].PageRegionPng.Should().NotBeEmpty();
        service.Requests[0].OverlappingText.Should().Contain("After first");
        using (var pdf = new PdfDocument(new PdfReader(output)))
        {
            var page = pdf.GetPage(1);
            PdfTextExtractor.GetTextFromPage(page).Should().Be("Before\nAfter first\nAfter second");
            var images = Images(page);
            images.Should().HaveCount(2);
            images[0].Roles.Should().Equal("Artifact");
            images[0].Mcid.Should().Be(-1);
            images[1].Roles.Should().Equal("Figure");
            var owners = PdfImageOccurrenceEditor.PageContentOwners(page);
            owners[images[1].Mcid].GetAlt().ToUnicodeString().Should().Be("Informative symbol");
            var paragraph = (PdfStructElem)((PdfStructElem)pdf.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
            paragraph.GetKids().Select(k => k is PdfStructElem ? "Figure" : "text").Should().Equal("text", "text", "Figure", "text");
            var parentArray = pdf.GetStructTreeRoot().GetPdfObject().GetAsDictionary(PdfName.ParentTree).GetAsArray(PdfName.Nums).GetAsArray(1);
            foreach (var (mcid, owner) in owners) parentArray.GetAsDictionary(mcid).Should().Be(owner.GetPdfObject());
        }
        var secondService = new Classifier();
        await Process(output, secondService, "second.pdf");
        secondService.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("invalid")]
    [InlineData("missing-alt")]
    [InlineData("disabled")]
    public async Task UnusableClassificationOrDisabled_PreservesOriginalImages(string mode)
    {
        var result = mode == "invalid" ? new ImagePurposeResult(ImagePurpose.Meaningful, double.NaN, "", "Invalid") : new ImagePurposeResult(ImagePurpose.Meaningful, 0.90, "", "No description");
        var service = new Classifier(result, result) { Fail = mode == "failure" };
        var output = await Process(CreateInput(), service, options: new() { ClassifyImagesOutsideFigures = mode != "disabled" });
        using var pdf = new PdfDocument(new PdfReader(output));
        Images(pdf.GetPage(1)).Should().OnlyContain(i => i.Mcid == 0 && i.Roles.SequenceEqual(new[] { "P" }));
        if (mode == "disabled") service.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DescribedComposite_DemotesOnlyUnlabelledVectorLeaf()
    {
        var output = await Process(CreateInput(nested: true), new Classifier());
        using var pdf = new PdfDocument(new PdfReader(output));
        var parent = (PdfStructElem)((PdfStructElem)pdf.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
        parent.GetRole().Should().Be(PdfName.Figure);
        parent.GetAlt().ToUnicodeString().Should().Be("Composite badge");
        var children = parent.GetKids().Cast<PdfStructElem>().ToArray();
        children.Select(c => c.GetRole().GetValue()).Should().Equal("Span", "Figure", "Figure", "Figure");
        children[1].GetAlt().ToUnicodeString().Should().Be("Explicit component");
        children[2].GetActualText().ToUnicodeString().Should().Be("Actual component");
    }

    [Theory]
    [InlineData("Figure")]
    [InlineData("Link")]
    [InlineData("Formula")]
    public async Task RoleMappedSemanticOwners_AreNotClassified(string role)
    {
        var input = CreateInput();
        var protectedPath = Path.Combine(_root, "protected.pdf");
        using (var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(protectedPath)))
        {
            var owner = PdfImageOccurrenceEditor.PageContentOwners(pdf.GetPage(1))[0];
            owner.GetPdfObject().Remove(PdfName.NS);
            owner.GetPdfObject().Put(PdfName.S, new PdfName("CustomOwner"));
            pdf.GetStructTreeRoot().GetRoleMap().Put(new PdfName("CustomOwner"), new PdfName(role));
        }
        var service = new Classifier(new ImagePurposeResult(ImagePurpose.Meaningful, 0.01, "", "Decoration"));
        var output = await Process(protectedPath, service);
        service.Requests.Should().BeEmpty();
        using var result = new PdfDocument(new PdfReader(output));
        Images(result.GetPage(1)).Should().OnlyContain(i => i.Mcid == 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public async Task InvalidThreshold_IsRejected(double threshold)
    {
        var action = () => Process(CreateInput(), new Classifier(), options: new() { ImageMeaningfulConfidenceThreshold = threshold });
        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CancellationPropagatesWithoutApplyingClassification()
    {
        var service = new Classifier { Cancel = true };
        var action = () => Process(CreateInput(), service);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<string> Process(string input, Classifier service, string name = "output.pdf", PdfRemediationOptions? options = null)
    {
        var output = Path.Combine(_root, name);
        var processor = new PdfRemediationProcessor(service, new NoopPdfBookmarkService(), new Rasterizer(),
            new SamplePdfTitleService(), Options.Create(options ?? new()), NullLogger<PdfRemediationProcessor>.Instance);
        await processor.ProcessAsync("test", input, output, CancellationToken.None);
        return output;
    }

    private string CreateInput(bool nested = false)
    {
        var path = Path.Combine(_root, "input.pdf");
        using var pdf = new PdfDocument(new PdfWriter(path));
        pdf.SetTagged(); pdf.GetDocumentInfo().SetTitle("Test");
        var page = pdf.AddNewPage();
        var document = new PdfStructElem(pdf, PdfName.Document); pdf.GetStructTreeRoot().AddKid(document);
        var parent = new PdfStructElem(pdf, nested ? PdfName.Figure : PdfName.P, page); document.AddKid(parent);
        parent.SetNamespace(new PdfNamespace("http://iso.org/pdf2/ssn"));
        var canvas = new PdfCanvas(page);
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        var image = new PdfImageXObject(ImageDataFactory.Create(2, 2, 3, 8, new byte[] { 255,0,0, 0,255,0, 0,0,255, 255,255,0 }, null));
        if (nested)
        {
            parent.SetAlt(new PdfString("Composite badge"));
            for (var i = 0; i < 4; i++)
            {
                var child = new PdfStructElem(pdf, PdfName.Figure, page); parent.AddKid(child);
                if (i == 1) child.SetAlt(new PdfString("Explicit component"));
                if (i == 2) child.SetActualText(new PdfString("Actual component"));
                var id = page.GetNextMcid(); child.AddKid(new PdfMcrNumber(new PdfNumber(id), child));
                canvas.BeginMarkedContent(PdfName.Figure, Props(id));
                if (i == 3) canvas.AddXObjectWithTransformationMatrix(image, 40, 0, 0, 40, 100, 500);
                else canvas.Rectangle(100 + 50 * i, 500, 40, 40).Fill();
                canvas.EndMarkedContent();
            }
        }
        else
        {
            var id = page.GetNextMcid(); parent.AddKid(new PdfMcrNumber(new PdfNumber(id), parent));
            canvas.BeginMarkedContent(PdfName.P, Props(id)); Text("Before", 600);
            canvas.AddXObjectWithTransformationMatrix(image, 100, 0, 0, 20, 100, 550); Text("After first", 550);
            canvas.AddXObjectWithTransformationMatrix(image, 100, 0, 0, 20, 100, 500); Text("After second", 500);
            canvas.EndMarkedContent();
        }
        canvas.Release(); return path;
        void Text(string value, int y) => canvas.BeginText().SetFontAndSize(font, 12).MoveText(100, y).ShowText(value).EndText();
    }
    private static PdfDictionary Props(int mcid) { var result = new PdfDictionary(); result.Put(PdfName.MCID, new PdfNumber(mcid)); return result; }
    private static List<(int Mcid, string[] Roles)> Images(PdfPage page)
    {
        var listener = new ImageListener(); new PdfCanvasProcessor(listener).ProcessPageContent(page); return listener.Images;
    }
    private sealed class ImageListener : IEventListener
    {
        public List<(int Mcid, string[] Roles)> Images { get; } = new();
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is ImageRenderInfo image) Images.Add((image.GetMcid(), image.GetCanvasTagHierarchy().Select(t => t.GetRole().GetValue()).ToArray()));
        }
        public ICollection<EventType> GetSupportedEvents() => [EventType.RENDER_IMAGE];
    }
    private sealed class Classifier(params ImagePurposeResult[] results) : IAltTextService
    {
        public List<ImagePurposeRequest> Requests { get; } = new();
        public bool Fail { get; init; }
        public bool Cancel { get; init; }
        public Task<ImagePurposeResult?> ClassifyImageAsync(ImagePurposeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Cancel) throw new OperationCanceledException();
            if (Fail) throw new IOException("Unavailable");
            return Task.FromResult<ImagePurposeResult?>(results.ElementAtOrDefault(Requests.Count - 1));
        }
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken) => Task.FromResult("");
        public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken) => Task.FromResult("");
        public string GetFallbackAltTextForImage() => "";
        public string GetFallbackAltTextForLink() => "";
    }
    private sealed class Rasterizer : IPdfPageRasterizer, IPdfRasterDocument
    {
        public bool IsAvailable => true;
        public IPdfRasterDocument OpenDocument(string pdfPath, int dpi) => this;
        public BgraBitmap RenderPage(int pageNumber1Based, CancellationToken cancellationToken) => new(new byte[600 * 850 * 4], 600, 850, 2400);
        public void Dispose() { }
    }
}
