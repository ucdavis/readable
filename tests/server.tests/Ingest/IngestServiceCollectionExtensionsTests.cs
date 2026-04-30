using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using server.core.Ingest;

namespace server.tests.Ingest;

public class IngestServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFileIngest_RegistersAutotagAndAccessibilityAsSeparateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddFileIngest(options =>
        {
            options.UseNoops();
            options.AutotagProvider = FileIngestOptions.AutotagProviders.OpenDataLoader;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAutotagProvider>()
            .Should().BeAssignableTo<NoopAdobePdfServices>();
        provider.GetRequiredService<IAccessibilityChecker>()
            .Should().BeAssignableTo<NoopAdobePdfServices>();
        provider.GetRequiredService<IAutotagProvider>()
            .Should().BeSameAs(provider.GetRequiredService<IAccessibilityChecker>());
    }

    [Fact]
    public void AddFileIngest_WithOpenDataLoaderProvider_DoesNotRequireAdobeCredentialsWhenAdobeServicesAreDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        Action act = () => services.AddFileIngest(options =>
        {
            options.UseNoops();
            options.AutotagProvider = FileIngestOptions.AutotagProviders.OpenDataLoader;
        });

        act.Should().NotThrow();
    }
}
