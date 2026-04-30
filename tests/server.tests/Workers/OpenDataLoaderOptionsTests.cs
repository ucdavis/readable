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
                ["ODL_FAILED_QUEUE_NAME"] = "custom-failed",
            })
            .Build();

        var options = OpenDataLoaderOptions.FromConfiguration(configuration);

        options.AutotagQueueName.Should().Be("custom-autotag");
        options.FinalizeQueueName.Should().Be("custom-finalize");
        options.FailedQueueName.Should().Be("custom-failed");
    }

    [Fact]
    public void FromConfiguration_ReadsStorageConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage__ConnectionString"] = "UseDevelopmentStorage=true",
            })
            .Build();

        var options = OpenDataLoaderOptions.FromConfiguration(configuration);

        options.StorageConnectionString.Should().Be("UseDevelopmentStorage=true");
    }

    [Fact]
    public void ValidateConfiguration_WhenStorageConnectionStringIsMissing_Throws()
    {
        var options = new OpenDataLoaderOptions
        {
            ServiceBusConnectionString = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test",
        };

        Action act = () => OpenDataLoaderWorker.ValidateConfiguration(options);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Storage:ConnectionString*");
    }

    [Fact]
    public void ValidateConfiguration_WhenRequiredConnectionStringsArePresent_DoesNotThrow()
    {
        var options = new OpenDataLoaderOptions
        {
            ServiceBusConnectionString = "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test",
            StorageConnectionString = "UseDevelopmentStorage=true",
        };

        Action act = () => OpenDataLoaderWorker.ValidateConfiguration(options);

        act.Should().NotThrow();
    }
}
