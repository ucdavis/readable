using System.Text;
using FluentAssertions;
using iText.IO.Font;
using iText.Kernel.Pdf;
using server.core.Remediate;

namespace server.tests.Integration.Remediate;

public sealed class PdfXObjectStructureAssociationRemediatorTests
{
    private static readonly PdfName RoleOther = new("Other");
    private static readonly PdfName RoleFormula = new("Formula");
    private static readonly PdfName StructParentsKey = new("StructParents");

    [Fact]
    public void Remediate_UnclaimedFormXObjectWithStructParents_DisconnectsAssociation()
    {
        var runRoot = CreateRunRoot("remediate-xobject-form");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            CreateTaggedPdfWithXObjectStructParents(inputPdfPath, PdfName.Form, RoleOther);

            PdfXObjectStructureAssociationRemediationResult result;
            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                result = PdfXObjectStructureAssociationRemediator.Remediate(pdf, CancellationToken.None);
            }

            result.Inspected.Should().Be(2);
            result.Protected.Should().Be(0);
            result.Disconnected.Should().Be(1);
            CountXObjectsWithStructParents(outputPdfPath).Should().Be(0);
        }
        finally
        {
            DeleteRunRoot(runRoot);
        }
    }

    [Fact]
    public void Remediate_UnclaimedImageXObjectWithStructParents_DisconnectsAssociation()
    {
        var runRoot = CreateRunRoot("remediate-xobject-image");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            CreateTaggedPdfWithXObjectStructParents(inputPdfPath, PdfName.Image, RoleOther);

            PdfXObjectStructureAssociationRemediationResult result;
            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                result = PdfXObjectStructureAssociationRemediator.Remediate(pdf, CancellationToken.None);
            }

            result.Inspected.Should().Be(1);
            result.Protected.Should().Be(0);
            result.Disconnected.Should().Be(1);
            CountXObjectsWithStructParents(outputPdfPath).Should().Be(0);
        }
        finally
        {
            DeleteRunRoot(runRoot);
        }
    }

    [Fact]
    public void Remediate_FigureClaimedXObject_PreservesAssociationEvenWhenAltIsMissing()
    {
        var runRoot = CreateRunRoot("remediate-xobject-figure");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            CreateTaggedPdfWithXObjectStructParents(inputPdfPath, PdfName.Form, PdfName.Figure);

            PdfXObjectStructureAssociationRemediationResult result;
            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                result = PdfXObjectStructureAssociationRemediator.Remediate(pdf, CancellationToken.None);
            }

            result.Inspected.Should().Be(2);
            result.Protected.Should().Be(1);
            result.Disconnected.Should().Be(0);
            CountXObjectsWithStructParents(outputPdfPath).Should().Be(1);
        }
        finally
        {
            DeleteRunRoot(runRoot);
        }
    }

    [Fact]
    public void Remediate_AlreadyRemediatedFigureClaimedXObject_PreservesAssociation()
    {
        var runRoot = CreateRunRoot("remediate-xobject-figure-alt");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            CreateTaggedPdfWithXObjectStructParents(inputPdfPath, PdfName.Form, PdfName.Figure, "Existing figure alt text");

            PdfXObjectStructureAssociationRemediationResult result;
            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                result = PdfXObjectStructureAssociationRemediator.Remediate(pdf, CancellationToken.None);
            }

            result.Inspected.Should().Be(2);
            result.Protected.Should().Be(1);
            result.Disconnected.Should().Be(0);
            CountXObjectsWithStructParents(outputPdfPath).Should().Be(1);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            ListStructElementsByRole(outputPdf, PdfName.Figure)
                .Should()
                .ContainSingle(f => GetAlt(f) == "Existing figure alt text");
        }
        finally
        {
            DeleteRunRoot(runRoot);
        }
    }

    [Fact]
    public void Remediate_FormBackedFigureMcrMissingPage_AddsPageToMarkedContentReference()
    {
        var runRoot = CreateRunRoot("remediate-xobject-mcr-page");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            CreateTaggedPdfWithFormBackedFigureMcrMissingPage(inputPdfPath);

            PdfXObjectStructureAssociationRemediationResult result;
            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                result = PdfXObjectStructureAssociationRemediator.Remediate(pdf, CancellationToken.None);
            }

            result.InlineAltApplied.Should().Be(1);

            using var outputPdf = new PdfDocument(new PdfReader(outputPdfPath));
            var figure = ListStructElementsByRole(outputPdf, PdfName.Figure).Should().ContainSingle().Subject;
            var mcr = figure.GetAsArray(PdfName.K)!.GetAsDictionary(0)!;

            mcr.GetAsNumber(PdfName.MCID)!.IntValue().Should().Be(0);
            mcr.GetAsDictionary(PdfName.Pg).Should().NotBeNull("Form-backed MCRs need explicit page context for Acrobat to resolve the Structure Tag");
            mcr.Get(PdfName.Stm).Should().NotBeNull();
            GetFormXObjectContent(outputPdf)
                .Should()
                .Contain("/Alt <FEFF", "Acrobat checks Form XObject inline Figure marked content for alt text separately from the tag-tree Figure /Alt");
        }
        finally
        {
            DeleteRunRoot(runRoot);
        }
    }

    [Theory]
    [MemberData(nameof(MeaningfulClaims))]
    public void Remediate_MeaningfullyClaimedXObject_PreservesAssociation(PdfName role, string? alt)
    {
        var runRoot = CreateRunRoot($"remediate-xobject-claimed-{role.GetValue()}");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            CreateTaggedPdfWithXObjectStructParents(inputPdfPath, PdfName.Image, role, alt);

            PdfXObjectStructureAssociationRemediationResult result;
            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                result = PdfXObjectStructureAssociationRemediator.Remediate(pdf, CancellationToken.None);
            }

            result.Protected.Should().Be(1);
            result.Disconnected.Should().Be(0);
            CountXObjectsWithStructParents(outputPdfPath).Should().Be(1);
        }
        finally
        {
            DeleteRunRoot(runRoot);
        }
    }

    [Fact]
    public void Remediate_UntaggedPdf_DoesNothing()
    {
        var runRoot = CreateRunRoot("remediate-xobject-untagged");

        try
        {
            var inputPdfPath = Path.Combine(runRoot, "input.pdf");
            var outputPdfPath = Path.Combine(runRoot, "output.pdf");
            CreateUntaggedPdfWithXObjectStructParents(inputPdfPath);

            PdfXObjectStructureAssociationRemediationResult result;
            using (var pdf = new PdfDocument(new PdfReader(inputPdfPath), new PdfWriter(outputPdfPath)))
            {
                result = PdfXObjectStructureAssociationRemediator.Remediate(pdf, CancellationToken.None);
            }

            result.Should().Be(new PdfXObjectStructureAssociationRemediationResult(0, 0, 0));
            CountXObjectsWithStructParents(outputPdfPath).Should().Be(1);
        }
        finally
        {
            DeleteRunRoot(runRoot);
        }
    }

    public static TheoryData<PdfName, string?> MeaningfulClaims()
    {
        return new TheoryData<PdfName, string?>
        {
            { RoleOther, "Meaningful alternate text" },
            { PdfName.Link, null },
            { PdfName.Form, null },
            { RoleFormula, null },
        };
    }

    private static void CreateTaggedPdfWithXObjectStructParents(
        string outputPath,
        PdfName xObjectSubtype,
        PdfName claimRole,
        string? claimAlt = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();
        AddXObjectWithStructParents(pdf, page, xObjectSubtype);
        AddTagTreeWithParentTreeClaim(pdf, claimRole, claimAlt);
    }

    private static void CreateUntaggedPdfWithXObjectStructParents(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();
        AddXObjectWithStructParents(pdf, page, PdfName.Form);
    }

    private static void CreateTaggedPdfWithFormBackedFigureMcrMissingPage(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var pdf = new PdfDocument(new PdfWriter(outputPath));
        var page = pdf.AddNewPage();

        var resources = new PdfDictionary();
        resources.MakeIndirect(pdf);
        var xObjects = new PdfDictionary();
        resources.Put(PdfName.XObject, xObjects);
        page.GetPdfObject().Put(PdfName.Resources, resources);

        var form = new PdfStream(Encoding.ASCII.GetBytes("/Figure <</MCID 0 >> BDC /Im0 Do EMC"));
        form.MakeIndirect(pdf);
        form.Put(PdfName.Type, PdfName.XObject);
        form.Put(PdfName.Subtype, PdfName.Form);
        form.Put(PdfName.BBox, new PdfArray(new[] { 0, 0, 10, 10 }));
        form.Put(StructParentsKey, new PdfNumber(0));
        xObjects.Put(new PdfName("Fm0"), form.GetIndirectReference());

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

        var figure = new PdfDictionary();
        figure.MakeIndirect(pdf);
        figure.Put(PdfName.Type, new PdfName("StructElem"));
        figure.Put(PdfName.S, PdfName.Figure);
        figure.Put(PdfName.P, documentElem);
        figure.Put(PdfName.Pg, page.GetPdfObject());
        figure.Put(PdfName.Alt, new PdfString("Existing figure alt text", PdfEncodings.UNICODE_BIG));

        var mcr = new PdfDictionary();
        mcr.Put(PdfName.Type, new PdfName("MCR"));
        mcr.Put(PdfName.MCID, new PdfNumber(0));
        mcr.Put(PdfName.Stm, form.GetIndirectReference());
        figure.Put(PdfName.K, new PdfArray(mcr));

        var documentKids = new PdfArray();
        documentKids.Add(figure);
        documentElem.Put(PdfName.K, documentKids);
        structTreeRoot.Put(PdfName.K, documentElem);

        var nums = new PdfArray();
        nums.Add(new PdfNumber(0));
        nums.Add(new PdfArray(figure));
        parentTree.Put(PdfName.Nums, nums);

        var markInfo = new PdfDictionary();
        markInfo.MakeIndirect(pdf);
        markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(true));

        catalog.Put(PdfName.MarkInfo, markInfo);
        catalog.Put(PdfName.StructTreeRoot, structTreeRoot);
    }

    private static void AddXObjectWithStructParents(PdfDocument pdf, PdfPage page, PdfName subtype)
    {
        var resources = new PdfDictionary();
        resources.MakeIndirect(pdf);

        var xObjects = new PdfDictionary();
        resources.Put(PdfName.XObject, xObjects);
        page.GetPdfObject().Put(PdfName.Resources, resources);

        var xObject = PdfName.Image.Equals(subtype)
            ? new PdfStream(new byte[] { 0, 0, 0 })
            : new PdfStream(Array.Empty<byte>());

        xObject.MakeIndirect(pdf);
        xObject.Put(PdfName.Type, PdfName.XObject);
        xObject.Put(PdfName.Subtype, subtype);
        xObject.Put(StructParentsKey, new PdfNumber(0));

        if (PdfName.Image.Equals(subtype))
        {
            xObject.Put(PdfName.Width, new PdfNumber(1));
            xObject.Put(PdfName.Height, new PdfNumber(1));
            xObject.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
            xObject.Put(PdfName.BitsPerComponent, new PdfNumber(8));
        }
        else
        {
            xObject.Put(PdfName.BBox, new PdfArray(new[] { 0, 0, 10, 10 }));
        }

        xObjects.Put(new PdfName("XO1"), xObject.GetIndirectReference());

        if (PdfName.Form.Equals(subtype))
        {
            var nestedResources = new PdfDictionary();
            nestedResources.MakeIndirect(pdf);

            var nestedXObjects = new PdfDictionary();
            nestedResources.Put(PdfName.XObject, nestedXObjects);
            xObject.Put(PdfName.Resources, nestedResources);

            var image = new PdfStream(new byte[] { 0, 0, 0 });
            image.MakeIndirect(pdf);
            image.Put(PdfName.Type, PdfName.XObject);
            image.Put(PdfName.Subtype, PdfName.Image);
            image.Put(PdfName.Width, new PdfNumber(1));
            image.Put(PdfName.Height, new PdfNumber(1));
            image.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
            image.Put(PdfName.BitsPerComponent, new PdfNumber(8));
            nestedXObjects.Put(new PdfName("Im0"), image.GetIndirectReference());
        }
    }

    private static void AddTagTreeWithParentTreeClaim(PdfDocument pdf, PdfName claimRole, string? claimAlt)
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

        var claimedElem = new PdfDictionary();
        claimedElem.MakeIndirect(pdf);
        claimedElem.Put(PdfName.Type, new PdfName("StructElem"));
        claimedElem.Put(PdfName.S, claimRole);
        claimedElem.Put(PdfName.P, documentElem);
        if (claimAlt is not null)
        {
            claimedElem.Put(PdfName.Alt, new PdfString(claimAlt, PdfEncodings.UNICODE_BIG));
        }

        var documentKids = new PdfArray();
        documentKids.Add(claimedElem);
        documentElem.Put(PdfName.K, documentKids);
        structTreeRoot.Put(PdfName.K, documentElem);

        var parentTreeEntry = new PdfArray();
        parentTreeEntry.Add(claimedElem);

        var nums = new PdfArray();
        nums.Add(new PdfNumber(0));
        nums.Add(parentTreeEntry);
        parentTree.Put(PdfName.Nums, nums);

        var markInfo = new PdfDictionary();
        markInfo.MakeIndirect(pdf);
        markInfo.Put(PdfName.Marked, PdfBoolean.ValueOf(true));

        catalog.Put(PdfName.MarkInfo, markInfo);
        catalog.Put(PdfName.StructTreeRoot, structTreeRoot);
    }

    private static int CountXObjectsWithStructParents(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));
        var visited = new HashSet<(int objNum, int genNum)>();
        var count = 0;

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            count += CountXObjectsWithStructParents(pdf.GetPage(pageNumber).GetResources()?.GetPdfObject(), visited);
        }

        return count;
    }

    private static int CountFormFigureMarkedContentWithoutInlineAlt(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));
        var visited = new HashSet<(int objNum, int genNum)>();
        var count = 0;

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            count += CountFormFigureMarkedContentWithoutInlineAlt(
                pdf.GetPage(pageNumber).GetResources()?.GetPdfObject(),
                visited);
        }

        return count;
    }

    private static int CountFormFigureMarkedContentWithoutInlineAlt(
        PdfDictionary? resources,
        HashSet<(int objNum, int genNum)> visited)
    {
        var xObjects = resources?.GetAsDictionary(PdfName.XObject);
        if (xObjects is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var name in xObjects.KeySet())
        {
            var obj = xObjects.Get(name);
            if (obj is null || obj is PdfNull)
            {
                continue;
            }

            var objRef = obj as PdfIndirectReference ?? obj.GetIndirectReference();
            if (objRef is not null && !visited.Add((objRef.GetObjNumber(), objRef.GetGenNumber())))
            {
                continue;
            }

            obj = obj is PdfIndirectReference reference ? reference.GetRefersTo(true) : obj;
            if (obj is not PdfStream stream || !PdfName.Form.Equals(stream.GetAsName(PdfName.Subtype)))
            {
                continue;
            }

            var content = Encoding.Latin1.GetString(stream.GetBytes());
            if (content.Contains("/Figure", StringComparison.Ordinal)
                && content.Contains("BDC", StringComparison.Ordinal)
                && !content.Contains("/Alt", StringComparison.Ordinal))
            {
                count++;
            }

            count += CountFormFigureMarkedContentWithoutInlineAlt(stream.GetAsDictionary(PdfName.Resources), visited);
        }

        return count;
    }

    private static int CountImageXObjectsWithAlt(string pdfPath)
    {
        using var pdf = new PdfDocument(new PdfReader(pdfPath));
        var visited = new HashSet<(int objNum, int genNum)>();
        var count = 0;

        for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
        {
            count += CountImageXObjectsWithAlt(pdf.GetPage(pageNumber).GetResources()?.GetPdfObject(), visited);
        }

        return count;
    }

    private static int CountImageXObjectsWithAlt(
        PdfDictionary? resources,
        HashSet<(int objNum, int genNum)> visited)
    {
        var xObjects = resources?.GetAsDictionary(PdfName.XObject);
        if (xObjects is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var name in xObjects.KeySet())
        {
            var obj = xObjects.Get(name);
            if (obj is null || obj is PdfNull)
            {
                continue;
            }

            var objRef = obj as PdfIndirectReference ?? obj.GetIndirectReference();
            if (objRef is not null && !visited.Add((objRef.GetObjNumber(), objRef.GetGenNumber())))
            {
                continue;
            }

            obj = obj is PdfIndirectReference reference ? reference.GetRefersTo(true) : obj;
            if (obj is not PdfStream stream)
            {
                continue;
            }

            if (PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype))
                && !string.IsNullOrWhiteSpace(stream.GetAsString(PdfName.Alt)?.ToUnicodeString()))
            {
                count++;
            }

            if (PdfName.Form.Equals(stream.GetAsName(PdfName.Subtype)))
            {
                count += CountImageXObjectsWithAlt(stream.GetAsDictionary(PdfName.Resources), visited);
            }
        }

        return count;
    }

    private static List<PdfDictionary> ListStructElementsByRole(PdfDocument pdf, PdfName role)
    {
        var results = new List<PdfDictionary>();
        var rootKids = pdf.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.StructTreeRoot)?.Get(PdfName.K);
        if (rootKids is not null)
        {
            TraverseStructElements(rootKids, role, results);
        }

        return results;
    }

    private static void TraverseStructElements(PdfObject node, PdfName role, List<PdfDictionary> results)
    {
        node = node is PdfIndirectReference reference ? reference.GetRefersTo(true) : node;

        if (node is PdfArray array)
        {
            foreach (var item in array)
            {
                TraverseStructElements(item, role, results);
            }

            return;
        }

        if (node is not PdfDictionary dict)
        {
            return;
        }

        if (role.Equals(dict.GetAsName(PdfName.S)))
        {
            results.Add(dict);
        }

        var kids = dict.Get(PdfName.K);
        if (kids is not null)
        {
            TraverseStructElements(kids, role, results);
        }
    }

    private static string? GetAlt(PdfDictionary structElem)
        => structElem.GetAsString(PdfName.Alt)?.ToUnicodeString();

    private static string GetFormXObjectContent(PdfDocument pdf)
    {
        var xObjects = pdf.GetPage(1).GetResources().GetPdfObject().GetAsDictionary(PdfName.XObject)!;
        var formObject = xObjects.Get(new PdfName("Fm0"));
        var form = formObject is PdfIndirectReference reference
            ? (PdfStream)reference.GetRefersTo(true)
            : (PdfStream)formObject;
        return Encoding.Latin1.GetString(form.GetBytes());
    }

    private static int CountXObjectsWithStructParents(
        PdfDictionary? resources,
        HashSet<(int objNum, int genNum)> visited)
    {
        var xObjects = resources?.GetAsDictionary(PdfName.XObject);
        if (xObjects is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var name in xObjects.KeySet())
        {
            var obj = xObjects.Get(name);
            if (obj is null || obj is PdfNull)
            {
                continue;
            }

            var objRef = obj as PdfIndirectReference ?? obj.GetIndirectReference();
            if (objRef is not null && !visited.Add((objRef.GetObjNumber(), objRef.GetGenNumber())))
            {
                continue;
            }

            obj = obj is PdfIndirectReference reference ? reference.GetRefersTo(true) : obj;
            if (obj is not PdfStream stream)
            {
                continue;
            }

            var subtype = stream.GetAsName(PdfName.Subtype);
            if (!PdfName.Form.Equals(subtype) && !PdfName.Image.Equals(subtype))
            {
                continue;
            }

            if (stream.GetAsNumber(StructParentsKey) is not null)
            {
                count++;
            }

            if (PdfName.Form.Equals(subtype))
            {
                count += CountXObjectsWithStructParents(stream.GetAsDictionary(PdfName.Resources), visited);
            }
        }

        return count;
    }

    private static string CreateRunRoot(string name)
    {
        var runRoot = Path.Combine(Path.GetTempPath(), "readable-tests", $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        return runRoot;
    }

    private static void DeleteRunRoot(string runRoot)
    {
        if (Directory.Exists(runRoot))
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }
}
