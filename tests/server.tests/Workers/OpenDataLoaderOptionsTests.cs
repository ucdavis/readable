using FluentAssertions;
using Microsoft.Extensions.Configuration;
using opendataloader.api;

namespace server.tests.Workers;

public sealed class OpenDataLoaderOptionsTests
{
    [Fact]
    public void FromConfiguration_ReadsWorkerQueueNames()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ODL_AUTOTAG_QUEUE_NAME"] = "custom-autotag",
                ["ODL_FINALIZE_QUEUE_NAME"] = "custom-finalize",
            })
            .Build();

        var options = OpenDataLoaderOptions.FromConfiguration(configuration);

        options.AutotagQueueName.Should().Be("custom-autotag");
        options.FinalizeQueueName.Should().Be("custom-finalize");
    }
}
