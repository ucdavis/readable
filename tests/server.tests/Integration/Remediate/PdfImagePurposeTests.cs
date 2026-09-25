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
    [InlineData(ImagePurpose.Decorative, false)]
    [InlineData(ImagePurpose.Meaningful, false)]
    [InlineData(ImagePurpose.Decorative, true)]
    [InlineData(ImagePurpose.Meaningful, true)]
    public async Task SharedFormAndPageMcid_PreservesStructureReadingOrder(ImagePurpose purpose, bool dictionaryPageReference)
    {
        var input = CreateInput();
        var mixed = Path.Combine(_root, "mixed.pdf");
        byte[] formContent;
        using (var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(mixed)))
        {
            var page = pdf.GetPage(1);
            var owner = PdfImageOccurrenceEditor.PageContentOwners(page)[0];
            if (dictionaryPageReference)
            {
                owner.RemoveKid(0);
                var reference = Props(0);
                reference.Put(PdfName.Type, PdfName.MCR);
                reference.Put(PdfName.Pg, page.GetPdfObject());
                owner.AddKid(new PdfMcrDictionary(reference, owner));
            }
            var form = new PdfFormXObject(new iText.Kernel.Geom.Rectangle(0, 0, 100, 30));
            form.GetPdfObject().Put(PdfName.StructParents, new PdfNumber(50));
            form.GetPdfObject().MakeIndirect(pdf);
            var inner = new PdfCanvas(form, pdf);
            inner.BeginMarkedContent(PdfName.Span, Props(0)).BeginText()
                .SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
                .MoveText(0, 10).ShowText("Form").EndText().EndMarkedContent();
            inner.Release();
            formContent = form.GetPdfObject().GetBytes();
            var formReference = Props(0);
            formReference.Put(PdfName.Type, PdfName.MCR);
            formReference.Put(PdfName.Pg, page.GetPdfObject());
            formReference.Put(PdfName.Stm, form.GetPdfObject());
            owner.AddKid(0, new PdfMcrDictionary(formReference, owner));
            var canvas = new PdfCanvas(page.NewContentStreamBefore(), page.GetResources(), pdf);
            canvas.AddXObjectWithTransformationMatrix(form, 1, 0, 0, 1, 100, 650);
            canvas.Release();
        }
        var direct = Path.Combine(_root, "direct.pdf");
        using (var pdf = new PdfDocument(new PdfReader(mixed), new PdfWriter(direct)))
        {
            var editor = new PdfImageOccurrenceEditor(pdf.GetPage(1));
            editor.Draws.Should().HaveCount(2);
            foreach (var draw in editor.Draws)
                editor.Add(draw, purpose == ImagePurpose.Meaningful ? "Informative symbol" : null);
            editor.Apply();
            AssertOrder(pdf);
        }
        using (var reopened = new PdfDocument(new PdfReader(direct))) AssertOrder(reopened);
        var classification = new ImagePurposeResult(purpose, 0.95, "Informative symbol", "Visual evidence");
        var service = new Classifier(classification, classification);
        var output = await Process(mixed, service);
        service.Requests.Should().HaveCount(2);
        using var result = new PdfDocument(new PdfReader(output));
        AssertOrder(result);

        void AssertOrder(PdfDocument pdf)
        {
            var page = pdf.GetPage(1);
            var owner = PdfImageOccurrenceEditor.PageContentOwners(page)[0];
            var text = new ParagraphTextListener();
            new PdfCanvasProcessor(text).ProcessPageContent(page);
            var order = owner.GetKids().Select(k =>
            {
                if (k is PdfStructElem figure)
                {
                    figure.GetRole().Should().Be(PdfName.Figure);
                    figure.GetAlt().ToUnicodeString().Should().Be("Informative symbol");
                    return "Figure";
                }
                var mcr = (PdfMcr)k;
                if (mcr.GetPdfObject() is PdfDictionary d && d.GetAsStream(PdfName.Stm) is { } stream)
                {
                    mcr.GetMcid().Should().Be(0);
                    mcr.GetPageObject().Should().Be(page.GetPdfObject());
                    stream.GetBytes().Should().Equal(formContent);
                    return "Form";
                }
                return text.ByMcid[mcr.GetMcid()];
            });
            order.Should().Equal(purpose == ImagePurpose.Meaningful
                ? new[] { "Form", "Before", "Figure", "After first", "Figure", "After second" }
                : new[] { "Form", "Before", "After first", "After second" });
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DescribedComposite_DemotesOnlyUnlabelledVectorLeaf(bool mappedParent)
    {
        var input = CreateInput(nested: true);
        if (mappedParent)
        {
            var mapped = Path.Combine(_root, "mapped.pdf");
            using (var document = new PdfDocument(new PdfReader(input), new PdfWriter(mapped)))
            {
                var composite = (PdfStructElem)((PdfStructElem)document.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
                composite.GetPdfObject().Put(PdfName.S, new PdfName("Badge"));
                composite.GetPdfObject().Remove(PdfName.NS);
                document.GetStructTreeRoot().AddRoleMapping("Badge", "Figure");
            }
            input = mapped;
        }
        var service = new Classifier { ImageAlt = "Generated component description" };
        var output = await Process(input, service);
        using var pdf = new PdfDocument(new PdfReader(output));
        var parent = (PdfStructElem)((PdfStructElem)pdf.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
        PdfImageOccurrenceEditor.ResolveRole(parent.GetPdfObject(), pdf).Should().Be(PdfName.Figure);
        parent.GetAlt().ToUnicodeString().Should().Be("Composite badge");
        var children = parent.GetKids().Cast<PdfStructElem>().ToArray();
        children.Select(c => c.GetRole().GetValue()).Should().Equal("Span", "Figure", "Figure", "Figure");
        children[1].GetAlt().ToUnicodeString().Should().Be("Explicit component");
        children[2].GetActualText().ToUnicodeString().Should().Be("Actual component");
    }

    [Fact]
    public async Task NewlyDescribedComposite_NormalizesComponentsAfterAltGeneration()
    {
        var input = CreateInput(nested: true);
        var undescribed = Path.Combine(_root, "undescribed.pdf");
        using (var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(undescribed)))
        {
            var parent = (PdfStructElem)((PdfStructElem)pdf.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
            parent.GetPdfObject().Remove(PdfName.Alt);
        }
        var output = await Process(undescribed, new Classifier { ImageAlt = "Generated composite description" });
        using var result = new PdfDocument(new PdfReader(output));
        var composite = (PdfStructElem)((PdfStructElem)result.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
        composite.GetAlt().ToUnicodeString().Should().Be("Generated composite description");
        ((PdfStructElem)composite.GetKids()[0]).GetRole().Should().Be(PdfName.Span);
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

    [Theory]
    [InlineData("fill", "image", false)]
    [InlineData("fill", "image", true)]
    [InlineData("fill", "text", false)]
    [InlineData("fill", "text", true)]
    [InlineData("stroke", "image", false)]
    [InlineData("stroke", "image", true)]
    [InlineData("stroke", "text", false)]
    [InlineData("stroke", "text", true)]
    [InlineData("mask", "image", false)]
    [InlineData("mask", "image", true)]
    [InlineData("mask", "text", false)]
    [InlineData("mask", "text", true)]
    [InlineData("plain", "", false)]
    [InlineData("mask-none", "", true)]
    public async Task CompositeComponents_RequireSafePaintResources(string paint, string payload, bool beforeBdc)
    {
        var input = CreateComponentInput("plain", paint, payload, beforeBdc);
        var service = new Classifier();
        var output = await Process(input, service);
        service.Requests.Should().BeEmpty();
        using var pdf = new PdfDocument(new PdfReader(output));
        using var original = new PdfDocument(new PdfReader(input));
        var parent = (PdfStructElem)((PdfStructElem)pdf.GetStructTreeRoot().GetKids()[0]).GetKids()[0];
        var expected = paint is "plain" or "mask-none" ? PdfName.Span : PdfName.Figure;
        parent.GetKids().Cast<PdfStructElem>().Should().OnlyContain(c => c.GetRole().Equals(expected));
        parent.GetAlt().ToUnicodeString().Should().Be("Composite badge");
        pdf.GetPage(1).GetContentBytes().Should().Equal(original.GetPage(1).GetContentBytes());
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("stroke")]
    [InlineData("mask")]
    public async Task ComplexPaintResources_DoNotDisableImagePurposeClassification(string paint)
    {
        var input = CreateInput();
        var withResources = Path.Combine(_root, "resources.pdf");
        using (var pdf = new PdfDocument(new PdfReader(input), new PdfWriter(withResources)))
            AddPaintResources(pdf, pdf.GetPage(1), paint, "image");
        var service = new Classifier(new(ImagePurpose.Meaningful, 0.79, "", "Decorative copy"),
            new(ImagePurpose.Meaningful, 0.80, "Informative symbol", "Independent content"));
        var output = await Process(withResources, service);
        service.Requests.Should().HaveCount(2);
        using var result = new PdfDocument(new PdfReader(output));
        Images(result.GetPage(1)).Select(i => i.Roles.Single()).Should().Equal("Artifact", "Figure");
    }

    private static string AddPaintResources(PdfDocument pdf, PdfPage page, string paint, string payload)
    {
        if (paint == "plain") return "";
        var form = new PdfFormXObject(new iText.Kernel.Geom.Rectangle(0, 0, 40, 40));
        var inner = new PdfCanvas(form, pdf);
        if (payload == "image")
        {
            var image = new PdfImageXObject(ImageDataFactory.Create(1, 1, 3, 8, new byte[] { 255, 0, 0 }, null));
            inner.AddXObjectWithTransformationMatrix(image, 40, 0, 0, 40, 0, 0);
        }
        else if (payload == "text")
            inner.BeginText().SetFontAndSize(PdfFontFactory.CreateFont(StandardFonts.HELVETICA), 12)
                .MoveText(0, 10).ShowText("Content").EndText();
        inner.Release();
        var stream = form.GetPdfObject();
        stream.MakeIndirect(pdf);
        var resources = page.GetResources().GetPdfObject();
        if (paint is "fill" or "stroke")
        {
            stream.Put(PdfName.Type, PdfName.Pattern);
            stream.Remove(PdfName.Subtype);
            stream.Put(PdfName.PatternType, new PdfNumber(1));
            stream.Put(PdfName.PaintType, new PdfNumber(1));
            stream.Put(PdfName.TilingType, new PdfNumber(1));
            stream.Put(PdfName.XStep, new PdfNumber(40));
            stream.Put(PdfName.YStep, new PdfNumber(40));
            var patterns = new PdfDictionary(); patterns.Put(new PdfName("P1"), stream);
            resources.Put(PdfName.Pattern, patterns);
            return paint == "fill" ? "/Pattern cs /P1 scn\n" : "/Pattern CS /P1 SCN\n";
        }
        var group = new PdfDictionary();
        group.Put(PdfName.S, PdfName.Transparency);
        group.Put(PdfName.CS, PdfName.DeviceRGB);
        stream.Put(PdfName.Group, group);
        var mask = new PdfDictionary();
        mask.Put(PdfName.S, PdfName.Luminosity);
        mask.Put(PdfName.G, stream);
        var state = new PdfDictionary();
        state.Put(PdfName.Type, PdfName.ExtGState);
        state.Put(PdfName.SMask, paint == "mask-none" ? PdfName.None : mask);
        var states = new PdfDictionary(); states.Put(new PdfName("GS1"), state);
        resources.Put(PdfName.ExtGState, states);
        return "/GS1 gs\n";
    }

    private string CreateComponentInput(string mode, string paint = "plain", string payload = "", bool beforeBdc = false)
    {
        var path = Path.Combine(_root, "component.pdf");
        using var pdf = new PdfDocument(new PdfWriter(path));
        pdf.SetTagged(); pdf.GetDocumentInfo().SetTitle("Test");
        var page = pdf.AddNewPage();
        var document = new PdfStructElem(pdf, PdfName.Document); pdf.GetStructTreeRoot().AddKid(document);
        var parent = new PdfStructElem(pdf, PdfName.Figure, page); document.AddKid(parent);
        parent.SetAlt(new PdfString("Composite badge"));
        var canvas = new PdfCanvas(page);
        var paintState = AddPaintResources(pdf, page, paint, payload);
        for (var i = 0; i < 2; i++)
        {
            var child = new PdfStructElem(pdf, PdfName.Figure, page); parent.AddKid(child);
            var id = i == 0 ? 1 : 5;
            child.AddKid(new PdfMcrNumber(new PdfNumber(id), child));
            var properties = Props(id);
            if (i == 1 && (mode.StartsWith("inline-") || mode.StartsWith("named-")))
                properties.Put(mode.EndsWith("actualtext") ? PdfName.ActualText : PdfName.Alt, new PdfString("Inline description"));
            if (i == 1 && mode.StartsWith("named-")) properties.MakeIndirect(pdf);
            if (i == 1 && beforeBdc) canvas.WriteLiteral(paintState);
            canvas.BeginMarkedContent(PdfName.Figure, properties);
            if (i == 1 && !beforeBdc) canvas.WriteLiteral(paintState);
            canvas.Rectangle(100 + i * 60, 500, 40, 40);
            if (i == 1 && paint == "stroke") canvas.Stroke();
            else canvas.Fill();
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Classification_BoundsConcurrency_AndKeepsDrawOrder(int concurrency)
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var service = new Classifier
        {
            Handler = async (request, token) =>
            {
                var count = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maximum, Math.Max(count, maximum));
                try
                {
                    if (request.OverlappingText.Contains("After first"))
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(token);
                        return new(ImagePurpose.Meaningful, 0.95, "First symbol", "Independent content");
                    }
                    request.OverlappingText.Should().Contain("After second");
                    secondFinished.TrySetResult();
                    return new(ImagePurpose.Decorative, 0.95, "", "Decorative copy");
                }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        var processing = Process(CreateInput(), service, options: new() { OpenAiMaxConcurrency = concurrency });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (concurrency == 2) await secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        else secondFinished.Task.IsCompleted.Should().BeFalse();
        releaseFirst.SetResult();
        var output = await processing.WaitAsync(TimeSpan.FromSeconds(10));
        maximum.Should().Be(concurrency);
        using var pdf = new PdfDocument(new PdfReader(output));
        var images = Images(pdf.GetPage(1));
        images[0].Roles.Should().Equal("Figure");
        images[1].Roles.Should().Equal("Artifact");
        PdfImageOccurrenceEditor.PageContentOwners(pdf.GetPage(1))[images[0].Mcid].GetAlt().ToUnicodeString().Should().Be("First symbol");
        PdfTextExtractor.GetTextFromPage(pdf.GetPage(1)).Should().Be("Before\nAfter first\nAfter second");
    }

    [Fact]
    public async Task ClassificationTimeout_PreservesImage_AndContinuesOtherRequests()
    {
        var stalled = new TaskCompletionSource<ImagePurposeResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Classifier
        {
            Handler = (request, _) => request.OverlappingText.Contains("After first")
                ? stalled.Task // Even a provider that ignores cancellation cannot block the job.
                : Task.FromResult<ImagePurposeResult?>(new(ImagePurpose.Decorative, 0.95, "", "Decoration"))
        };
        try
        {
            var output = await Process(CreateInput(), service, options: new() { ImageClassificationTimeoutSeconds = 1 })
                .WaitAsync(TimeSpan.FromSeconds(10));
            using var pdf = new PdfDocument(new PdfReader(output));
            var images = Images(pdf.GetPage(1));
            images[0].Roles.Should().Equal("P");
            images[0].Mcid.Should().Be(0);
            images[1].Roles.Should().Equal("Artifact");
        }
        finally { stalled.TrySetResult(null); }
    }

    [Fact]
    public async Task CallerCancellation_DuringClassification_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Classifier
        {
            Handler = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }
        };
        var processing = Process(CreateInput(), service, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var action = async () => await processing.WaitAsync(TimeSpan.FromSeconds(10));
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<string> Process(string input, Classifier service, string name = "output.pdf", PdfRemediationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var output = Path.Combine(_root, name);
        var processor = new PdfRemediationProcessor(service, new NoopPdfBookmarkService(), new Rasterizer(),
            new SamplePdfTitleService(), Options.Create(options ?? new() { OpenAiMaxConcurrency = 1 }), NullLogger<PdfRemediationProcessor>.Instance);
        await processor.ProcessAsync("test", input, output, cancellationToken);
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
    private sealed class ParagraphTextListener : IEventListener
    {
        public Dictionary<int, string> ByMcid { get; } = new();
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is TextRenderInfo text && text.GetCanvasTagHierarchy().Any(t => PdfName.P.Equals(t.GetRole())))
                ByMcid[text.GetMcid()] = ByMcid.GetValueOrDefault(text.GetMcid(), "") + text.GetText();
        }
        public ICollection<EventType> GetSupportedEvents() => [EventType.RENDER_TEXT];
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
        public Func<ImagePurposeRequest, CancellationToken, Task<ImagePurposeResult?>>? Handler { get; init; }
        public string ImageAlt { get; init; } = "";
        public bool Fail { get; init; }
        public bool Cancel { get; init; }
        public Task<ImagePurposeResult?> ClassifyImageAsync(ImagePurposeRequest request, CancellationToken cancellationToken)
        {
            if (Handler is not null) return Handler(request, cancellationToken);
            lock (Requests)
            {
                Requests.Add(request);
                if (Cancel) throw new OperationCanceledException();
                if (Fail) throw new IOException("Unavailable");
                return Task.FromResult<ImagePurposeResult?>(results.ElementAtOrDefault(Requests.Count - 1));
            }
        }
        public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken) => Task.FromResult(ImageAlt);
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
