namespace server.core.Remediate.AltText;

public sealed record ImageAltTextRequest(
    byte[] ImageBytes,
    string MimeType,
    string ContextBefore,
    string ContextAfter);

public sealed record LinkAltTextRequest(
    string? Target,
    string LinkText,
    string ContextBefore,
    string ContextAfter);

public interface IAltTextService
{
    Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken);
    Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken);

    string GetFallbackAltTextForImage();
    string GetFallbackAltTextForLink();
}

