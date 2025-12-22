using FluentAssertions;
using server.core.Ingest;

namespace server.tests.Ingest;

public class StorageBlobCreatedEventParserTests
{
    [Fact]
    public void TryParse_WhenDataUrlPresent_ParsesBlobUrlAndFileId()
    {
        const string json =
            """
            {"id":"6587a7a4-601e-003e-1f6c-7385b406c29c","source":"/subscriptions/105dede4-4731-492e-8c28-5121226319b0/resourceGroups/rg-readable-dev/providers/Microsoft.Storage/storageAccounts/streadabledevdataor3ker","specversion":"1.0","type":"Microsoft.Storage.BlobCreated","subject":"/blobServices/default/containers/incoming/blobs/drylab.pdf","time":"2025-12-22T17:55:29.0309555Z","data":{"api":"PutBlob","requestId":"6587a7a4-601e-003e-1f6c-7385b4000000","eTag":"0x8DE41834B2337B3","contentType":"application/pdf","contentLength":1382815,"blobType":"BlockBlob","accessTier":"Default","url":"https://streadabledevdataor3ker.blob.core.windows.net/incoming/drylab.pdf","sequencer":"00000000000000000000000000015F5F0000000000051c9c","storageDiagnostics":{"batchId":"112d8661-0006-0028-006c-738790000000"}}}
            """;

        StorageBlobCreatedEventParser.TryParse(json, out var request, out var error).Should().BeTrue(error);
        request.BlobUri.ToString().Should().Be("https://streadabledevdataor3ker.blob.core.windows.net/incoming/drylab.pdf");
        request.ContainerName.Should().Be("incoming");
        request.BlobName.Should().Be("drylab.pdf");
        request.FileId.Should().Be("drylab");
    }

    [Fact]
    public void TryParse_WhenDataUrlMissing_UsesSubjectAndSourceToBuildUrl()
    {
        const string json =
            """
            {"source":"/subscriptions/x/resourceGroups/y/providers/Microsoft.Storage/storageAccounts/mystorageacct","subject":"/blobServices/default/containers/incoming/blobs/abc123.pdf","data":{}}
            """;

        StorageBlobCreatedEventParser.TryParse(json, out var request, out var error).Should().BeTrue(error);
        request.BlobUri.ToString().Should().Be("https://mystorageacct.blob.core.windows.net/incoming/abc123.pdf");
        request.FileId.Should().Be("abc123");
    }
}

