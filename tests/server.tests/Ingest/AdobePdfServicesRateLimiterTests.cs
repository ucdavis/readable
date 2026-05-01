using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using server.core.Data;
using server.core.Ingest;

namespace server.tests.Ingest;

public class AdobePdfServicesRateLimiterTests
{
    [Fact]
    public void Options_FromConfiguration_UsesConservativeDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PDF_SERVICES_CLIENT_ID"] = "client-id",
            })
            .Build();

        var options = SqlAdobePdfServicesRateLimiter.AdobePdfServicesRateLimitOptions
            .FromConfiguration(configuration);

        options.Enabled.Should().BeTrue();
        options.RequestsPerWindow.Should().Be(20);
        options.Window.Should().Be(TimeSpan.FromSeconds(60));
        options.ThrottleCooldown.Should().Be(TimeSpan.FromSeconds(90));
        options.BucketKey.Should().Be("client-id");
    }

    [Fact]
    public void Options_FromConfiguration_AllowsOperationalOverrides()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdobePdfServices:RateLimit:Enabled"] = "false",
                ["AdobePdfServices:RateLimit:RequestsPerMinute"] = "12",
                ["AdobePdfServices:RateLimit:WindowSeconds"] = "30",
                ["AdobePdfServices:RateLimit:ThrottleCooldownSeconds"] = "45",
                ["AdobePdfServices:RateLimit:BucketKey"] = "bucket",
                ["AdobePdfServices:RateLimit:WaitLogThresholdSeconds"] = "7",
                ["AdobePdfServices:RateLimit:SqlLockTimeoutSeconds"] = "9",
            })
            .Build();

        var options = SqlAdobePdfServicesRateLimiter.AdobePdfServicesRateLimitOptions
            .FromConfiguration(configuration);

        options.Enabled.Should().BeFalse();
        options.RequestsPerWindow.Should().Be(12);
        options.Window.Should().Be(TimeSpan.FromSeconds(30));
        options.ThrottleCooldown.Should().Be(TimeSpan.FromSeconds(45));
        options.BucketKey.Should().Be("bucket");
        options.WaitLogThreshold.Should().Be(TimeSpan.FromSeconds(7));
        options.SqlLockTimeout.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public void AddFileIngest_WithNoops_RegistersNoopRateLimiter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddFileIngest(options => options.UseNoops());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAdobePdfServicesRateLimiter>()
            .Should().BeOfType<NoopAdobePdfServicesRateLimiter>();
    }

    [Fact]
    public void AddFileIngest_WithAdobeServices_RegistersSqlRateLimiter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseInMemoryDatabase($"rate-limiter-{Guid.NewGuid():N}"));
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PDF_SERVICES_CLIENT_ID"] = "client-id",
                    ["PDF_SERVICES_CLIENT_SECRET"] = "client-secret",
                })
                .Build());

        services.AddFileIngest(options =>
        {
            options.UseAdobePdfServices = true;
            options.AutotagProvider = FileIngestOptions.AutotagProviders.Adobe;
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAdobePdfServicesRateLimiter>()
            .Should().BeOfType<SqlAdobePdfServicesRateLimiter>();
    }

    [Fact]
    public async Task WaitAsync_WhenDisabled_ReturnsWithoutTouchingSql()
    {
        using var provider = CreateInMemoryProvider();
        var dbContextFactory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdobePdfServices:RateLimit:Enabled"] = "false",
            })
            .Build();
        var limiter = new SqlAdobePdfServicesRateLimiter(
            dbContextFactory,
            configuration,
            loggerFactory.CreateLogger<SqlAdobePdfServicesRateLimiter>());

        await limiter.WaitAsync(
            AdobePdfServicesRateLimitOperations.AccessibilityChecker,
            cost: 1,
            CancellationToken.None);

        await limiter.RecordThrottleAsync(
            AdobePdfServicesRateLimitOperations.AccessibilityChecker,
            new InvalidOperationException("rate limited"),
            CancellationToken.None);
    }

    [Fact]
    public async Task WaitAsync_WhenCostExceedsCapacity_ThrowsBeforeTouchingSql()
    {
        using var provider = CreateInMemoryProvider();
        var dbContextFactory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdobePdfServices:RateLimit:RequestsPerMinute"] = "2",
            })
            .Build();
        var limiter = new SqlAdobePdfServicesRateLimiter(
            dbContextFactory,
            configuration,
            loggerFactory.CreateLogger<SqlAdobePdfServicesRateLimiter>());

        var act = async () => await limiter.WaitAsync(
            AdobePdfServicesRateLimitOperations.AccessibilityChecker,
            cost: 3,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*exceeds configured capacity*");
    }

    private static ServiceProvider CreateInMemoryProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseInMemoryDatabase($"rate-limiter-{Guid.NewGuid():N}"));
        return services.BuildServiceProvider();
    }
}
