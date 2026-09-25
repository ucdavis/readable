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
    [InlineData("objr", ImagePurpose.Decorative)]
    [InlineData("objr", ImagePurpose.Meaningful)]
    [InlineData("objr-without-hook", ImagePurpose.Decorative)]
    [InlineData("objr-without-hook", ImagePurpose.Meaningful)]
    [InlineData("multiple-objr", ImagePurpose.Decorative)]
    [InlineData("multiple-objr", ImagePurpose.Meaningful)]
    [InlineData("duplicate-mcid", ImagePurpose.Decorative)]
    [InlineData("duplicate-mcid", ImagePurpose.Meaningful)]
    public async Task ClaimedOrAmbiguousImages_ArePreservedBeforeClassification(string mode, ImagePurpose purpose)
    {
        var input = CreateInput();
        var claimed = Path.Combine(_root, "claimed.pdf");
        using (var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(claimed)))
        {
            var page = pdf.GetPage(1);
            var document = (PdfStructElem)pdf.GetStructTreeRoot().GetKids()[0];
            if (mode == "duplicate-mcid")
            {
                var duplicate = new PdfStructElem(pdf, PdfName.P, page);
                document.AddKid(duplicate);
                duplicate.AddKid(new PdfMcrNumber(new PdfNumber(0), duplicate));
            }
            else
            {
                var resources = page.GetResources().GetResource(PdfName.XObject);
                var image = resources.GetAsStream(resources.KeySet().First());
                image.Put(PdfName.StructParent, new PdfNumber(10));
                for (var i = 0; i < (mode == "multiple-objr" ? 2 : 1); i++)
                {
                    var figure = new PdfStructElem(pdf, PdfName.Figure, page);
                    figure.SetAlt(new PdfString("Existing image description"));
                    document.AddKid(figure);
                    var reference = new PdfDictionary();
                    reference.Put(PdfName.Type, PdfName.OBJR);
                    reference.Put(PdfName.Obj, image);
                    reference.Put(PdfName.Pg, page.GetPdfObject());
                    figure.AddKid(new PdfObjRef(reference, figure));
                }
                if (mode == "objr-without-hook") image.Remove(PdfName.StructParent);
            }
        }
        byte[] originalContent;
        using (var pdf = new PdfDocument(new PdfReader(claimed))) originalContent = pdf.GetPage(1).GetContentBytes();
        var classification = new ImagePurposeResult(purpose, 0.95, "Replacement description", "Visual evidence");
        var service = new Classifier(classification, classification);
        var output = await Process(claimed, service);
        service.Requests.Should().BeEmpty();
        using var result = new PdfDocument(new PdfReader(output));
        result.GetPage(1).GetContentBytes().Should().Equal(originalContent);
        Images(result.GetPage(1)).Should().OnlyContain(i => i.Mcid == 0 && i.Roles.SequenceEqual(new[] { "P" }));
        var root = (PdfStructElem)result.GetStructTreeRoot().GetKids()[0];
        var figures = root.GetKids().OfType<PdfStructElem>().Where(e => e.GetRole().Equals(PdfName.Figure)).ToArray();
        figures.Should().HaveCount(mode == "duplicate-mcid" ? 0 : mode == "multiple-objr" ? 2 : 1);
        foreach (var figure in figures)
        {
            figure.GetAlt().ToUnicodeString().Should().Be("Existing image description");
            figure.GetKids().Should().ContainSingle().Which.Should().BeOfType<PdfObjRef>();
        }
    }

    [Theory]
    [InlineData("inline-alt")]
    [InlineData("inline-actualtext")]
    [InlineData("named-alt")]
    [InlineData("named-actualtext")]
    [InlineData("form-image")]
    [InlineData("form-text")]
    [InlineData("form-vector")]
    [InlineData("nested-image")]
    [InlineData("nested-text")]
    [InlineData("nested-vector")]
    [InlineData("repeated-mcid")]
    [InlineData("ambiguous-owner")]
    public async Task CompositeComponents_WithUnprovenOrDescribedContent_KeepFigureRole(string mode)
    {
        var input = CreateComponentInput(mode);
        var service = new Classifier();
        var output = await Process(input, service);
        service.Requests.Should().BeEmpty();
        using var pdf = new PdfDocument(new PdfReader(output));
        var parent = (PdfStructElem)((PdfStructElem)pdf.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
        parent.GetAlt().ToUnicodeString().Should().Be("Composite badge");
        var children = parent.GetKids().Cast<PdfStructElem>().ToArray();
        children[0].GetRole().Should().Be(PdfName.Span);
        children[1].GetRole().Should().Be(PdfName.Figure);
        using var original = new PdfDocument(new PdfReader(input));
        pdf.GetPage(1).GetContentBytes().Should().Equal(original.GetPage(1).GetContentBytes());
        PdfTextExtractor.GetTextFromPage(pdf.GetPage(1)).Should().Be(PdfTextExtractor.GetTextFromPage(original.GetPage(1)));
    }

    private string CreateComponentInput(string mode)
    {
        var path = Path.Combine(_root, "component.pdf");
        using var pdf = new PdfDocument(new PdfWriter(path));
        pdf.SetTagged(); pdf.GetDocumentInfo().SetTitle("Test");
        var page = pdf.AddNewPage();
        var document = new PdfStructElem(pdf, PdfName.Document); pdf.GetStructTreeRoot().AddKid(document);
        var parent = new PdfStructElem(pdf, PdfName.Figure, page); document.AddKid(parent);
        parent.SetAlt(new PdfString("Composite badge"));
        var canvas = new PdfCanvas(page);
        for (var i = 0; i < 2; i++)
        {
            var child = new PdfStructElem(pdf, PdfName.Figure, page); parent.AddKid(child);
            var id = i == 0 ? 1 : 5;
            child.AddKid(new PdfMcrNumber(new PdfNumber(id), child));
            var properties = Props(id);
            if (i == 1 && (mode.StartsWith("inline-") || mode.StartsWith("named-")))
                properties.Put(mode.EndsWith("actualtext") ? PdfName.ActualText : PdfName.Alt, new PdfString("Inline description"));
            if (i == 1 && mode.StartsWith("named-")) properties.MakeIndirect(pdf);
            canvas.BeginMarkedContent(PdfName.Figure, properties);
            canvas.Rectangle(100 + i * 60, 500, 40, 40).Fill();
            if (i == 1 && mode.StartsWith("form-"))
            {
                var form = new PdfFormXObject(new iText.Kernel.Geom.Rectangle(0, 0, 40, 40));
                var inner = new PdfCanvas(form, pdf);
                PaintNested(inner);
                inner.Release();
                canvas.AddXObjectWithTransformationMatrix(form, 1, 0, 0, 1, 160, 500);
            }
            if (i == 1 && mode.StartsWith("nested-")) PaintNested(canvas);
            canvas.EndMarkedContent();
            if (i == 1 && mode == "repeated-mcid")
                canvas.BeginMarkedContent(PdfName.Figure, Props(id)).Rectangle(220, 500, 40, 40).Fill().EndMarkedContent();
            if (i == 1 && mode == "ambiguous-owner")
            {
                var other = new PdfStructElem(pdf, PdfName.Span, page); parent.AddKid(other);
                other.AddKid(new PdfMcrNumber(new PdfNumber(id), other));
                child.AddKid(new PdfMcrNumber(new PdfNumber(6), child));
                canvas.BeginMarkedContent(PdfName.Figure, Props(6)).Rectangle(220, 500, 40, 40).Fill().EndMarkedContent();
            }
        }
        canvas.Release();
        return path;

        void PaintNested(PdfCanvas inner)
        {
            inner.BeginMarkedContent(PdfName.Span, Props(0));
            if (mode.EndsWith("image"))
            {
                var image = new PdfImageXObject(ImageDataFactory.Create(1, 1, 3, 8, new byte[] { 255, 0, 0 }, null));
                inner.AddXObjectWithTransformationMatrix(image, 20, 0, 0, 20, 0, 0);
            }
            else if (mode.EndsWith("text"))
                inner.BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12).MoveText(0, 10).ShowText("Content").EndText();
            else inner.Rectangle(0, 0, 20, 20).Fill();
            inner.EndMarkedContent();
        }
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
