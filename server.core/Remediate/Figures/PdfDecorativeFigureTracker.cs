using iText.Kernel.Pdf;
using Microsoft.Extensions.Logging;

namespace server.core.Remediate.Figures;

internal sealed class PdfDecorativeFigureTracker
{
    private static readonly PdfName RoleSpan = new("Span");

    private readonly HashSet<PdfDictionary> _demotedOrRemovedFigures = new(PdfDictionaryReferenceComparer.Instance);
    private readonly string _fileId;
    private readonly ILogger _logger;
    private readonly Dictionary<string, List<PdfDictionary>> _repeatedChromeFiguresBySignature = new(StringComparer.Ordinal);
    private readonly Func<PdfDictionary, bool> _tryRemoveStructElemFromParent;

    public PdfDecorativeFigureTracker(
        string fileId,
        ILogger logger,
        Func<PdfDictionary, bool> tryRemoveStructElemFromParent)
    {
        _fileId = fileId;
        _logger = logger;
        _tryRemoveStructElemFromParent = tryRemoveStructElemFromParent;
    }

    public int Removed { get; private set; }

    public int Demoted { get; private set; }

    public int Total => Removed + Demoted;

    public bool Demote(PdfDictionary figure, string reason, int? pageNumber = null)
    {
        if (!_demotedOrRemovedFigures.Add(figure))
        {
            return false;
        }

        figure.Remove(PdfName.Alt);
        figure.Put(PdfName.S, RoleSpan);
        Demoted++;
        _logger.LogInformation(
            "Demoted decorative figure in {fileId}: page={pageNumber} reason={reason}",
            _fileId,
            pageNumber,
            reason);

        return true;
    }

    public bool RemoveOrDemote(PdfDictionary figure, string reason, int? pageNumber = null)
    {
        if (!_demotedOrRemovedFigures.Add(figure))
        {
            return false;
        }

        figure.Remove(PdfName.Alt);
        if (_tryRemoveStructElemFromParent(figure))
        {
            Removed++;
            _logger.LogInformation(
                "Removed decorative figure from structure tree in {fileId}: page={pageNumber} reason={reason}",
                _fileId,
                pageNumber,
                reason);
        }
        else
        {
            figure.Put(PdfName.S, RoleSpan);
            Demoted++;
            _logger.LogInformation(
                "Demoted decorative figure in {fileId}: page={pageNumber} reason={reason}",
                _fileId,
                pageNumber,
                reason);
        }

        return true;
    }

    public bool RemoveOrDemoteRepeatedChromeFigure(
        PdfDictionary figure,
        string signature,
        string reason,
        int pageNumber)
    {
        if (!_repeatedChromeFiguresBySignature.TryGetValue(signature, out var figures))
        {
            figures = new List<PdfDictionary>();
            _repeatedChromeFiguresBySignature[signature] = figures;
        }

        if (!figures.Contains(figure, PdfDictionaryReferenceComparer.Instance))
        {
            figures.Add(figure);
        }

        if (figures.Count < PdfFigureVisualHeuristics.RepeatedFigureChromeThreshold)
        {
            return false;
        }

        var changed = false;
        foreach (var repeatedFigure in figures)
        {
            changed |= RemoveOrDemote(repeatedFigure, reason, pageNumber);
        }

        return changed;
    }

    public bool DemoteRepeatedChromeFigure(
        PdfDictionary figure,
        string signature,
        string reason,
        int pageNumber)
    {
        if (!_repeatedChromeFiguresBySignature.TryGetValue(signature, out var figures))
        {
            figures = new List<PdfDictionary>();
            _repeatedChromeFiguresBySignature[signature] = figures;
        }

        if (!figures.Contains(figure, PdfDictionaryReferenceComparer.Instance))
        {
            figures.Add(figure);
        }

        if (figures.Count < PdfFigureVisualHeuristics.RepeatedFigureChromeThreshold)
        {
            return false;
        }

        var changed = false;
        foreach (var repeatedFigure in figures)
        {
            changed |= Demote(repeatedFigure, reason, pageNumber);
        }

        return changed;
    }
}
