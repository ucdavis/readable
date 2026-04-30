using System.Runtime.CompilerServices;
using iText.Kernel.Pdf;

namespace server.core.Remediate.Figures;

internal sealed class PdfDictionaryReferenceComparer : IEqualityComparer<PdfDictionary>
{
    public static PdfDictionaryReferenceComparer Instance { get; } = new();

    public bool Equals(PdfDictionary? x, PdfDictionary? y) => ReferenceEquals(x, y);

    public int GetHashCode(PdfDictionary obj) => RuntimeHelpers.GetHashCode(obj);
}
