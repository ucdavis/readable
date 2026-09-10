using FluentAssertions;
using Rectangle = iText.Kernel.Geom.Rectangle;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Tagutils;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate;
using server.core.Remediate.AltText;
using server.core.Remediate.Title;

namespace server.tests.Integration.Remediate;

public sealed class PdfFormAnnotationAltTextTests
{
    [Theory]
    [InlineData("custom-alt")]
    [InlineData("different-case")]
    [InlineData("missing-label")]
    [InlineData("blank-label")]
    [InlineData("link")]
    [InlineData("figure")]
    [InlineData("non-widget")]
    [InlineData("multiple-children")]
    [InlineData("nested-content")]
    [InlineData("actual-text")]
    [InlineData("parent-cycle")]
    public void Cleanup_WhenTagDoesNotMatchConservativeGuard_PreservesAlt(string scenario)
    {
        using var source = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(source)))
        {
            AddWidget(pdf);
        }
        using var destination = new MemoryStream();
        using var editable = new PdfDocument(new PdfReader(new MemoryStream(source.ToArray())), new PdfWriter(destination));
        var widget = editable.GetPage(1).GetAnnotations().Single().GetPdfObject();
        var form = FindWidgetTag(editable, widget);
        switch (scenario)
        {
            case "custom-alt": form.Put(PdfName.Alt, new PdfString("Enter your department")); break;
            case "different-case": form.Put(PdfName.Alt, new PdfString("annotation")); break;
            case "missing-label": widget.Remove(PdfName.TU); break;
            case "blank-label": widget.Put(PdfName.TU, new PdfString(" ")); break;
            case "link": form.Put(PdfName.S, PdfName.Link); break;
            case "figure": form.Put(PdfName.S, PdfName.Figure); break;
            case "non-widget": widget.Put(PdfName.Subtype, PdfName.Link); break;
            case "multiple-children":
                var children = new PdfArray(form.Get(PdfName.K));
                children.Add(new PdfNumber(0));
                form.Put(PdfName.K, children);
                break;
            case "nested-content":
                var paragraph = new PdfDictionary();
                paragraph.Put(PdfName.S, PdfName.P);
                paragraph.Put(PdfName.K, form.Get(PdfName.K));
                form.Put(PdfName.K, new PdfArray(paragraph));
                break;
            case "actual-text": form.Put(PdfName.ActualText, new PdfString("Department")); break;
            case "parent-cycle":
                widget.Remove(PdfName.TU);
                widget.Put(PdfName.Parent, widget);
                break;
        }
        var expectedAlt = form.GetAsString(PdfName.Alt).ToUnicodeString();
        PdfAnnotationRemediator.RemovePlaceholderAltFromLabelledWidgets(editable, CancellationToken.None).Should().Be(0);
        form.GetAsString(PdfName.Alt).ToUnicodeString().Should().Be(expectedAlt);
        // Restore this deliberately malformed parent before iText closes the document.
        if (scenario == "parent-cycle") widget.Remove(PdfName.Parent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_WhenWidgetReferenceIsDirectOrInArray_IsIdempotent(bool directChild)
    {
        using var source = new MemoryStream();
        using (var pdf = new PdfDocument(new PdfWriter(source))) AddWidget(pdf);
        using var destination = new MemoryStream();
        using var editable = new PdfDocument(new PdfReader(new MemoryStream(source.ToArray())), new PdfWriter(destination));
        var widget = editable.GetPage(1).GetAnnotations().Single().GetPdfObject();
        var form = FindWidgetTag(editable, widget);
        if (!directChild) form.Put(PdfName.K, new PdfArray(form.Get(PdfName.K)));

        PdfAnnotationRemediator.RemovePlaceholderAltFromLabelledWidgets(editable, CancellationToken.None).Should().Be(1);
        PdfAnnotationRemediator.RemovePlaceholderAltFromLabelledWidgets(editable, CancellationToken.None).Should().Be(0);
        form.ContainsKey(PdfName.Alt).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_WhenWidgetHasPlaceholderAltAndLabel_RemovesOnlyTagAlt(bool parentLabel)
    {
        var directory = Path.Combine(Path.GetTempPath(), "readable-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.pdf");
            var output = Path.Combine(directory, "output.pdf");
            using (var pdf = new PdfDocument(new PdfWriter(input)))
            {
                AddWidget(pdf, parentLabel: parentLabel);
            }

            var processor = new PdfRemediationProcessor(
                new SampleAltTextService(), new NoopPdfBookmarkService(), new SamplePdfTitleService(),
                NullLogger<PdfRemediationProcessor>.Instance);
            await processor.ProcessAsync("form-placeholder", input, output, CancellationToken.None);

            using var result = new PdfDocument(new PdfReader(output));
            var widget = result.GetPage(1).GetAnnotations().Single().GetPdfObject();
            var form = FindWidgetTag(result, widget);
            form.ContainsKey(PdfName.Alt).Should().BeFalse("the placeholder must not hide the labelled widget");
            widget.GetAsString(PdfName.Contents).ToUnicodeString().Should().Be("Annotation");
            var field = parentLabel ? widget.GetAsDictionary(PdfName.Parent) : widget;
            field.GetAsString(PdfName.TU).ToUnicodeString().Should().Be("Department");
            field.GetAsString(PdfName.V).ToUnicodeString().Should().Be("Readable");
            field.GetAsString(PdfName.T).ToUnicodeString().Should().Be("department");
            result.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.AcroForm)
                .GetAsArray(PdfName.Fields).Size().Should().Be(1);
            widget.GetAsDictionary(PdfName.AP).GetAsStream(PdfName.N).GetBytes()
                .Should().Equal(System.Text.Encoding.ASCII.GetBytes("q Q"));
            form.GetAsDictionary(PdfName.K).GetAsDictionary(PdfName.Obj).GetIndirectReference()
                .Should().Be(widget.GetIndirectReference());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PdfDictionary AddWidget(PdfDocument pdf, bool parentLabel = false)
    {
        pdf.SetTagged();
        var page = pdf.AddNewPage();
        var annotation = new PdfWidgetAnnotation(new Rectangle(72, 700, 220, 24));
        annotation.SetContents("Annotation");
        var widget = annotation.GetPdfObject();
        widget.MakeIndirect(pdf);
        var field = parentLabel ? new PdfDictionary() : widget;
        field.MakeIndirect(pdf);
        field.Put(PdfName.FT, PdfName.Tx);
        field.Put(PdfName.T, new PdfString("department"));
        field.Put(PdfName.TU, new PdfString("Department"));
        field.Put(PdfName.V, new PdfString("Readable"));
        if (parentLabel)
        {
            field.Put(PdfName.Kids, new PdfArray(widget));
            widget.Put(PdfName.Parent, field);
        }
        var appearance = new PdfStream(System.Text.Encoding.ASCII.GetBytes("q Q"));
        appearance.Put(PdfName.Type, PdfName.XObject);
        appearance.Put(PdfName.Subtype, PdfName.Form);
        appearance.Put(PdfName.BBox, new PdfArray(new[] { 0, 0, 220, 24 }));
        appearance.MakeIndirect(pdf);
        var ap = new PdfDictionary();
        ap.Put(PdfName.N, appearance);
        widget.Put(PdfName.AP, ap);
        page.AddAnnotation(-1, annotation, false);
        var acroForm = new PdfDictionary();
        acroForm.Put(PdfName.Fields, new PdfArray(field));
        pdf.GetCatalog().GetPdfObject().Put(PdfName.AcroForm, acroForm);
        var pointer = new TagTreePointer(pdf).SetPageForTagging(page).AddTag("Form");
        pointer.GetProperties().SetAlternateDescription("Annotation");
        pointer.AddAnnotationTag(annotation);
        return widget;
    }

    private static PdfDictionary FindWidgetTag(PdfDocument pdf, PdfDictionary widget)
    {
        var parentTree = pdf.GetStructTreeRoot().GetPdfObject().GetAsDictionary(PdfName.ParentTree);
        var nums = parentTree.GetAsArray(PdfName.Nums);
        var key = widget.GetAsNumber(PdfName.StructParent).IntValue();
        for (var i = 0; i < nums.Size(); i += 2)
        {
            if (nums.GetAsNumber(i).IntValue() == key)
            {
                return nums.GetAsDictionary(i + 1);
            }
        }
        throw new InvalidOperationException("Widget tag was not preserved in the parent tree.");
    }
}
