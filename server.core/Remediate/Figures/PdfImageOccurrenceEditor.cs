using System.Text;
using iText.IO.Font;
using iText.IO.Source;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser.Util;
using iText.Kernel.Pdf.Tagging;

namespace server.core.Remediate.Figures;

/// <summary>Splits page-level image draws out of their marked text without rewriting drawing operators.</summary>
internal sealed class PdfImageOccurrenceEditor
{
    internal sealed record Draw(int Start, int End, int Mcid, PdfName Role, PdfDictionary Properties,
        PdfStream Image, PdfStructElem Owner);

    private readonly PdfPage _page;
    private readonly byte[] _content;
    private readonly List<(Draw Draw, string? Alt)> _edits = new();
    public IReadOnlyList<Draw> Draws { get; }
    public IReadOnlySet<int> VectorOnlyMcids { get; }

    public PdfImageOccurrenceEditor(PdfPage page)
    {
        _page = page;
        _content = page.GetContentBytes();
        var objectClaims = new HashSet<PdfObject>();
        var owners = PageContentOwners(page, objectClaims);
        var paths = new HashSet<int>();
        var unsupportedComponents = new HashSet<int>();
        var draws = new List<Draw>();
        var stack = new Stack<(PdfName? Role, PdfDictionary? Properties)>();
        var counts = new Dictionary<int, int>();
        var source = new RandomAccessFileOrArray(new RandomAccessSourceFactory().CreateSource(_content));
        using var tokenizer = new PdfTokenizer(source);
        var parser = new PdfCanvasParser(tokenizer, page.GetResources());
        var operands = new List<PdfObject>();
        while (true)
        {
            var start = (int)tokenizer.GetPosition();
            parser.Parse(operands);
            if (operands.Count == 0) break;
            var end = (int)tokenizer.GetPosition();
            var op = operands[^1].ToString();
            if (op is "BDC" or "BMC")
            {
                var properties = op == "BDC" ? operands[1] as PdfDictionary
                    ?? page.GetResources().GetResource(PdfName.Properties)?.GetAsDictionary(operands[1] as PdfName) : null;
                stack.Push((operands[0] as PdfName, properties));
                if (properties?.GetAsNumber(PdfName.MCID) is { } id)
                    counts[id.IntValue()] = counts.GetValueOrDefault(id.IntValue()) + 1;
            }
            else if (op == "EMC")
            {
                if (!stack.TryPop(out _)) throw new InvalidDataException("Unbalanced marked content.");
            }
            if (stack.Count > 1 || stack.Any(t => t.Properties is { } p
                && (p.ContainsKey(PdfName.Alt) || p.ContainsKey(PdfName.ActualText)))
                || op is "Do" or "BI" or "EI" or "BT" or "Tj" or "TJ" or "'" or "\"" or "sh")
            {
                foreach (var tag in stack)
                    if (tag.Properties?.GetAsNumber(PdfName.MCID) is { } id)
                        unsupportedComponents.Add(id.IntValue());
            }
            if (stack.Count == 1 && stack.Peek().Properties?.GetAsNumber(PdfName.MCID) is { } pathId
                && op is "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "n")
                paths.Add(pathId.IntValue());
            if (op == "Do" && stack.Count == 1)
            {
                var (role, properties) = stack.Peek();
                var mcid = properties?.GetAsNumber(PdfName.MCID)?.IntValue();
                var image = page.GetResources().GetResource(PdfName.XObject)?.GetAsStream(operands[0] as PdfName);
                if (mcid is null || role is null || PdfName.Artifact.Equals(role)
                    || properties!.ContainsKey(PdfName.ActualText) || properties.ContainsKey(PdfName.Alt)
                    || !PdfName.Image.Equals(image?.GetAsName(PdfName.Subtype))
                    || objectClaims.Contains(image!) || image!.ContainsKey(PdfName.StructParent)
                    || !owners.TryGetValue(mcid.Value, out var owner) || IsProtected(owner, page.GetDocument())) continue;
                draws.Add(new Draw(start, end, mcid.Value, role, properties, image!, owner));
            }
        }
        if (stack.Count != 0) throw new InvalidDataException("Unbalanced marked content.");
        // Repeated MCIDs cannot be split unambiguously without repairing the source structure first.
        Draws = draws.Where(d => counts[d.Mcid] == 1).ToArray();
        VectorOnlyMcids = paths.Where(id => counts[id] == 1 && !unsupportedComponents.Contains(id)).ToHashSet();
    }

    public void Add(Draw draw, string? alt) => _edits.Add((draw, alt));

    public void Apply()
    {
        if (_edits.Count == 0) return;
        using var output = new MemoryStream();
        var offset = 0;
        var previousMcid = new Dictionary<int, int>();
        foreach (var (draw, alt) in _edits.OrderBy(e => e.Draw.Start))
        {
            output.Write(_content, offset, draw.Start - offset);
            Write("\nEMC\n");
            var prior = previousMcid.GetValueOrDefault(draw.Mcid, draw.Mcid);
            var kids = draw.Owner.GetKids();
            var index = kids.ToList().FindIndex(k => k is PdfMcr mcr && mcr.GetMcid() == prior
                && mcr.GetPageObject().Equals(_page.GetPdfObject()));
            if (index < 0) throw new InvalidDataException("Missing image content owner.");
            if (alt is null)
            {
                Write("/Artifact BMC\n");
            }
            else
            {
                var figure = new PdfStructElem(_page.GetDocument(), PdfName.Figure);
                figure.SetAlt(new PdfString(alt, PdfEncodings.UNICODE_BIG));
                draw.Owner.AddKid(++index, figure);
                var imageMcid = AddContent(figure);
                Write($"/Figure <</MCID {imageMcid}>> BDC\n");
            }
            output.Write(_content, draw.Start, draw.End - draw.Start);
            Write("\nEMC\n");
            var next = AddContent(draw.Owner, ++index);
            previousMcid[draw.Mcid] = next;
            var props = new PdfDictionary(draw.Properties);
            props.Put(PdfName.MCID, new PdfNumber(next));
            using (var serialized = new MemoryStream())
            {
                var pdfOutput = new PdfOutputStream(serialized);
                pdfOutput.Write(draw.Role).WriteSpace().Write(props).WriteString(" BDC\n");
                output.Write(serialized.ToArray());
            }
            offset = draw.End;
        }
        output.Write(_content, offset, _content.Length - offset);
        var stream = new PdfStream(output.ToArray());
        stream.MakeIndirect(_page.GetDocument());
        _page.GetPdfObject().Put(PdfName.Contents, stream);

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
        int AddContent(PdfStructElem owner, int index = -1)
        {
            var mcid = _page.GetNextMcid();
            var dict = new PdfDictionary();
            dict.Put(PdfName.Type, PdfName.MCR);
            dict.Put(PdfName.Pg, _page.GetPdfObject());
            dict.Put(PdfName.MCID, new PdfNumber(mcid));
            // Register with iText, which regenerates ParentTree on close.
            owner.AddKid(index, new PdfMcrDictionary(dict, owner));
            return mcid;
        }
    }

    internal static Dictionary<int, PdfStructElem> PageContentOwners(PdfPage page, ISet<PdfObject>? objectClaims = null)
    {
        var owners = new Dictionary<int, PdfStructElem>();
        var ambiguous = new HashSet<int>();
        var visited = new HashSet<PdfDictionary>();
        foreach (var kid in page.GetDocument().GetStructTreeRoot().GetKids()) Visit(kid);
        foreach (var mcid in ambiguous) owners.Remove(mcid);
        return owners;
        void Visit(IStructureNode node)
        {
            if (node is not PdfStructElem elem || !visited.Add(elem.GetPdfObject())) return;
            foreach (var kid in elem.GetKids())
            {
                if (kid is PdfObjRef objRef && objRef.GetPdfObject() is PdfDictionary reference
                    && reference.Get(PdfName.Obj) is { } target)
                    objectClaims?.Add(target);
                if (kid is PdfMcr mcr && mcr.GetMcid() >= 0 && page.GetPdfObject().Equals(mcr.GetPageObject())
                    && !(mcr.GetPdfObject() is PdfDictionary d && d.ContainsKey(PdfName.Stm)))
                {
                    if (!owners.TryAdd(mcr.GetMcid(), elem)) ambiguous.Add(mcr.GetMcid());
                }
                else if (kid is PdfStructElem) Visit(kid);
            }
        }
    }

    internal static PdfName? ResolveRole(PdfDictionary elem, PdfDocument pdf)
    {
        var role = elem.GetAsName(PdfName.S);
        var mapping = pdf.GetStructTreeRoot().GetRoleMap();
        var seen = new HashSet<PdfName>();
        while (role is not null && seen.Add(role) && mapping.GetAsName(role) is { } mapped) role = mapped;
        return role;
    }

    internal static bool HasUnknownNamespace(PdfDictionary elem)
    {
        var ns = elem.GetAsDictionary(PdfName.NS);
        if (ns is null) return elem.ContainsKey(PdfName.NS);
        return ns.GetAsString(PdfName.NS)?.ToUnicodeString() is not
            ("http://iso.org/pdf/ssn" or "http://iso.org/pdf2/ssn");
    }

    private static bool IsProtected(PdfStructElem owner, PdfDocument pdf)
    {
        var visited = new HashSet<PdfDictionary>();
        for (var elem = owner.GetPdfObject(); elem is not null && visited.Add(elem); elem = elem.GetAsDictionary(PdfName.P))
        {
            var role = ResolveRole(elem, pdf);
            if (role is not null && new[] { "Figure", "Formula", "Link", "Annot", "Artifact" }.Contains(role.GetValue())
                || elem.ContainsKey(PdfName.Alt) || elem.ContainsKey(PdfName.ActualText) || HasUnknownNamespace(elem)) return true;
        }
        return false;
    }
}
