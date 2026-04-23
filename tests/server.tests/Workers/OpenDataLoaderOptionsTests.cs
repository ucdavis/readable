using FluentAssertions;
using Microsoft.Extensions.Configuration;
using opendataloader.api;

namespace server.tests.Workers;

public sealed class OpenDataLoaderOptionsTests
{
    [Fact]
    public void FromConfiguration_AllowsZeroQueuedConversions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ODL_MAX_QUEUED_CONVERSIONS"] = "0",
            })
            .Build();

        var options = OpenDataLoaderOptions.FromConfiguration(configuration);

        options.MaxQueuedConversions.Should().Be(0);
    }
}
