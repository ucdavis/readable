using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Filter;
using iText.Kernel.Pdf.Tagging;
using Microsoft.Extensions.Logging;
using server.core.Remediate.AltText;
using server.core.Remediate.Figures;
using server.core.Remediate.Rasterize;

namespace server.core.Remediate;

public sealed partial class PdfRemediationProcessor
{
    private async Task RemediateImagesOutsideFiguresAsync(PdfDocument pdf, string inputPath,
        string? primaryLanguage, CancellationToken cancellationToken)
    {
        if (!_options.ClassifyImagesOutsideFigures || !_pageRasterizer.IsAvailable) return;
        IPdfRasterDocument? raster = null;
        try
        {
            for (var number = 1; number <= pdf.GetNumberOfPages(); number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = pdf.GetPage(number);
                // Existing crop coordinates assume unrotated, zero-origin pages. Preserve other layouts until
                // their raster-to-PDF transform can be established rather than classify against the wrong crop.
                var box = page.GetCropBox();
                if (page.GetRotation() != 0 || box.GetX() != 0 || box.GetY() != 0
                    || !box.EqualsWithEpsilon(page.GetMediaBox())) continue;
                PdfImageOccurrenceEditor editor;
                IReadOnlyList<ImageOccurrence> occurrences;
                try
                {
                    editor = new PdfImageOccurrenceEditor(page);
                    if (editor.Draws.Count == 0) continue;
                    occurrences = PdfContentScanner.ListImageOccurrences(page, number);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Cannot safely map image draws on page {page}; preserving them.", number);
                    continue;
                }
                BgraBitmap bitmap;
                try
                {
                    raster ??= _pageRasterizer.OpenDocument(inputPath, 144);
                    bitmap = raster.RenderPage(number, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Cannot render image context on page {page}; preserving images.", number);
                    continue;
                }
                // Match repeated uses in paint order, never dedupe purpose by image bytes. Form XObject
                // occurrences can share page MCIDs; ambiguous groups are intentionally not rewritten.
                foreach (var group in editor.Draws.GroupBy(d => (d.Mcid, d.Image.GetIndirectReference())))
                {
                    var matching = occurrences.Where(o => o.Mcid == group.Key.Mcid
                        && Equals(o.ObjectRef, group.Key.Item2)).ToArray();
                    var draws = group.ToArray();
                    if (matching.Length != draws.Length) continue;
                    for (var i = 0; i < draws.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var occ = matching[i];
                        if (occ.Bounds is null) continue;
                        try
                        {
                            var (bytes, mime) = ExtractImageBytes(occ.Image);
                            if (!IsSupportedAltTextImageMimeType(mime)) continue;
                            var crop = TryComputeCropRectPx(occ.Bounds, page.GetPageSize(), bitmap, 64, 8);
                            if (crop is null || crop.Value.IsEmpty) continue;
                            var overlap = PdfTextExtractor.GetTextFromPage(page, new FilteredTextEventListener(
                                new LocationTextExtractionStrategy(), new TextRegionEventFilter(occ.Bounds)));
                            var result = await _altTextService.ClassifyImageAsync(new ImagePurposeRequest(
                                new ImageAltTextRequest(bytes, mime, occ.ContextBefore, occ.ContextAfter, primaryLanguage),
                                PngEncoder.EncodeBgra32(BgraBitmapCropper.Crop(bitmap, crop.Value)), overlap), cancellationToken);
                            if (result is null) continue;
                            if (!Enum.IsDefined(result.Purpose) || !double.IsFinite(result.Confidence) || result.Confidence < 0
                                || result.Confidence > 1 || string.IsNullOrWhiteSpace(result.Reason))
                                throw new InvalidDataException("Invalid image-purpose classification.");
                            var meaningful = result.Purpose == ImagePurpose.Meaningful && result.Confidence >= _options.ImageMeaningfulConfidenceThreshold;
                            if (meaningful && (string.IsNullOrWhiteSpace(result.AltText) || IsPlaceholderImageAltText(result.AltText)))
                                throw new InvalidDataException("Meaningful image classification requires a usable description.");
                            editor.Add(draws[i], meaningful ? result.AltText : null);
                            _logger.LogInformation("Image purpose page={page} mcid={mcid} confidence={confidence} action={action} reason={reason}",
                                number, occ.Mcid, result.Confidence, meaningful ? "Figure" : "Artifact", result.Reason);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "Image classification failed on page {page} MCID {mcid}; preserving content.", number, occ.Mcid);
                        }
                    }
                }
                editor.Apply();
            }
        }
        finally { raster?.Dispose(); }
    }

    private void NormalizeNestedFigureComponents(PdfDocument pdf, CancellationToken cancellationToken)
    {
        var demoted = 0;
        for (var number = 1; number <= pdf.GetNumberOfPages(); number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pdf.GetPage(number);
            PdfImageOccurrenceEditor content;
            try { content = new PdfImageOccurrenceEditor(page); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Cannot safely map figure components on page {page}; preserving them.", number);
                continue;
            }
            var owners = PdfImageOccurrenceEditor.PageContentOwners(page);
            foreach (var owner in owners.Values.DistinctBy(e => e.GetPdfObject()))
            {
                var child = owner.GetPdfObject();
                var parent = child.GetAsDictionary(PdfName.P);
                if (!PdfName.Figure.Equals(PdfImageOccurrenceEditor.ResolveRole(child, pdf))
                    || child.ContainsKey(PdfName.Alt) || child.ContainsKey(PdfName.ActualText)
                    || PdfImageOccurrenceEditor.HasUnknownNamespace(child)
                    || parent is null || !PdfName.Figure.Equals(PdfImageOccurrenceEditor.ResolveRole(parent, pdf))
                    || PdfImageOccurrenceEditor.HasUnknownNamespace(parent)
                    || ShouldGenerateAltForFigure(parent)) continue;
                var kids = owner.GetKids();
                // Only vector-only leaf components of an already described composite. Independent raster
                // images, child text, explicit semantics and deeper structure keep their original roles.
                if (kids.Count == 0 || kids.Any(k => k is not PdfMcr m || m is PdfObjRef
                    || m.GetPdfObject() is PdfDictionary d && d.ContainsKey(PdfName.Stm)
                    || !page.GetPdfObject().Equals(m.GetPageObject())
                    || !owners.TryGetValue(m.GetMcid(), out var uniqueOwner)
                    || !uniqueOwner.GetPdfObject().Equals(child)
                    || !content.VectorOnlyMcids.Contains(m.GetMcid()))) continue;
                child.Put(PdfName.S, RoleSpan);
                demoted++;
            }
        }
        _logger.LogInformation("Normalized {count} vector components inside described composite figures.", demoted);
    }
}
