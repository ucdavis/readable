namespace server.core.Remediate.AltText;

public sealed class SampleAltTextService : IAltTextService
{
    public Task<string> GetAltTextForImageAsync(ImageAltTextRequest request, CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("sample image alt text");
    }

    public Task<string> GetAltTextForLinkAsync(LinkAltTextRequest request, CancellationToken cancellationToken)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("sample link alt text");
    }

    public string GetFallbackAltTextForImage() => "sample image alt text";

    public string GetFallbackAltTextForLink() => "sample link alt text";
}

