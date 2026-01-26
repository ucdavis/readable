using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;
using Microsoft.Extensions.Logging.Abstractions;
using server.core.Remediate.Bookmarks;

var (inputPath, outputPath) = ParseArgs(args);
if (inputPath is null)
{
    PrintUsage();
    return 2;
}

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input PDF not found: {inputPath}");
    return 2;
}

outputPath ??= CreateDefaultOutputPath(inputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

try
{
    using (var pdf = new PdfDocument(new PdfReader(inputPath), new PdfWriter(outputPath)))
    {
        var sut = new PdfBookmarkService(NullLogger<PdfBookmarkService>.Instance);
        await sut.EnsureBookmarksAsync(pdf, CancellationToken.None);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to process PDF: {ex.Message}");
    return 1;
}

try
{
    using var outputPdf = new PdfDocument(new PdfReader(outputPath));
    var outlineRoot = outputPdf.GetOutlines(updateOutlines: true);

    var hasOutlines = HasOutlines(outputPdf);
    Console.WriteLine($"Output: {outputPath}");
    Console.WriteLine($"Tagged: {outputPdf.IsTagged()}");
    Console.WriteLine($"Has outlines: {hasOutlines}");

    if (!hasOutlines)
    {
        return 0;
    }

    var pageRefToPageNumber = BuildPageObjectNumberToPageNumberMap(outputPdf);
    PrintOutlineTree(outputPdf, outlineRoot, pageRefToPageNumber, indent: 0);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to read output PDF: {ex.Message}");
    return 1;
}

return 0;

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project tools/bookmarks.runner -- --input <path> [--output <path>]");
}

static (string? InputPath, string? OutputPath) ParseArgs(string[] args)
{
    string? inputPath = null;
    string? outputPath = null;

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if ((arg is "--input" or "-i") && i + 1 < args.Length)
        {
            inputPath = args[++i];
            continue;
        }

        if ((arg is "--output" or "-o") && i + 1 < args.Length)
        {
            outputPath = args[++i];
            continue;
        }
    }

    return (inputPath, outputPath);
}

static string CreateDefaultOutputPath(string inputPath)
{
    var safeBaseName = Path.GetFileNameWithoutExtension(inputPath);
    safeBaseName = string.IsNullOrWhiteSpace(safeBaseName) ? "output" : safeBaseName;

    var dir = Path.Combine(Path.GetTempPath(), "readable-tools", "bookmarks.runner");
    var fileName = $"{safeBaseName}-bookmarks-{DateTime.UtcNow:yyyyMMddHHmmss}.pdf";
    return Path.Combine(dir, fileName);
}

static bool HasOutlines(PdfDocument pdf)
{
    try
    {
        var catalog = pdf.GetCatalog().GetPdfObject();
        var outlinesRoot = catalog.GetAsDictionary(PdfName.Outlines);
        if (outlinesRoot is null)
        {
            return false;
        }

        var first = outlinesRoot.Get(PdfName.First);
        return first is not null && first is not PdfNull;
    }
    catch
    {
        return false;
    }
}

static Dictionary<int, int> BuildPageObjectNumberToPageNumberMap(PdfDocument pdf)
{
    var map = new Dictionary<int, int>();
    for (var pageNumber = 1; pageNumber <= pdf.GetNumberOfPages(); pageNumber++)
    {
        var pageRef = pdf.GetPage(pageNumber).GetPdfObject().GetIndirectReference();
        if (pageRef is null)
        {
            continue;
        }

        map[pageRef.GetObjNumber()] = pageNumber;
    }

    return map;
}

static void PrintOutlineTree(PdfDocument pdf, PdfOutline outline, Dictionary<int, int> pageRefToPageNumber, int indent)
{
    foreach (var child in outline.GetAllChildren())
    {
        var title = child.GetTitle() ?? string.Empty;
        var (pageNumber, topY) = TryGetDestination(pdf, child.GetDestination(), pageRefToPageNumber);

        var prefix = new string(' ', indent * 2);
        if (pageNumber is null)
        {
            Console.WriteLine($"{prefix}- {title}");
        }
        else if (topY is null)
        {
            Console.WriteLine($"{prefix}- {title} (p{pageNumber})");
        }
        else
        {
            Console.WriteLine($"{prefix}- {title} (p{pageNumber}, y={topY:0.##})");
        }

        PrintOutlineTree(pdf, child, pageRefToPageNumber, indent + 1);
    }
}

static (int? PageNumber, float? TopY) TryGetDestination(PdfDocument pdf, PdfDestination? destination, Dictionary<int, int> pageRefToPageNumber)
{
    if (destination is null)
    {
        return (PageNumber: null, TopY: null);
    }

    var obj = destination.GetPdfObject();
    if (obj is not PdfArray arr || arr.Size() < 1)
    {
        return (PageNumber: null, TopY: null);
    }

    var pageObj = Dereference(arr.Get(0));
    int? pageNumber = null;

    switch (pageObj)
    {
        case PdfIndirectReference pageRef:
        {
            if (pageRefToPageNumber.TryGetValue(pageRef.GetObjNumber(), out var mapped))
            {
                pageNumber = mapped;
            }

            break;
        }
        case PdfDictionary pageDict:
        {
            var pageRef = pageDict.GetIndirectReference();
            if (pageRef is not null && pageRefToPageNumber.TryGetValue(pageRef.GetObjNumber(), out var mapped))
            {
                pageNumber = mapped;
            }

            break;
        }
        case PdfNumber pageIndex:
        {
            // PDF destinations can encode the page as a 0-based index.
            var idx = pageIndex.IntValue();
            if (idx >= 0 && idx < pdf.GetNumberOfPages())
            {
                pageNumber = idx + 1;
            }

            break;
        }
    }

    float? topY = null;
    if (arr.Size() >= 3 && Dereference(arr.Get(1)) is PdfName destType && destType.Equals(PdfName.FitH))
    {
        if (Dereference(arr.Get(2)) is PdfNumber top)
        {
            topY = top.FloatValue();
        }
    }

    return (pageNumber, topY);
}

static PdfObject Dereference(PdfObject obj)
{
    if (obj is PdfIndirectReference reference)
    {
        return reference.GetRefersTo(true);
    }

    return obj;
}

