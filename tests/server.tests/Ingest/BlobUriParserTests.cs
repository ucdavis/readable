using FluentAssertions;
using server.core.Ingest;

namespace server.tests.Ingest;

public sealed class BlobUriParserTests
{
    [Theory]
    [InlineData("https://example.blob.core.windows.net/incoming/my%20file.pdf", "incoming", "my file.pdf")]
    [InlineData("https://example.blob.core.windows.net/incoming/caf%C3%A9.pdf", "incoming", "café.pdf")]
    [InlineData("https://example.blob.core.windows.net/incoming/my%2520file.pdf", "incoming", "my%20file.pdf")]
    [InlineData("https://example.blob.core.windows.net/incoming/folder%2Fa.pdf", "incoming", "folder/a.pdf")]
    [InlineData("https://example.blob.core.windows.net/incoming/a+b.pdf", "incoming", "a+b.pdf")]
    public void ParseContainerAndBlob_DecodesBlobNamesOnce(
        string uri,
        string expectedContainerName,
        string expectedBlobName)
    {
        var result = BlobUriParser.ParseContainerAndBlob(new Uri(uri));

        result.ContainerName.Should().Be(expectedContainerName);
        result.BlobName.Should().Be(expectedBlobName);
    }

    [Fact]
    public void ParseContainerAndBlob_WhenPathDoesNotContainContainerAndBlob_Throws()
    {
        Action act = () => BlobUriParser.ParseContainerAndBlob(
            new Uri("https://example.blob.core.windows.net/incoming"));

        act.Should().Throw<InvalidOperationException>();
    }
}
