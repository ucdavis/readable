using System.Text.Json;
using FluentAssertions;

namespace server.tests.Workers;

public class IngestWorkerHostJsonTests
{
    [Fact]
    public void HostJson_ConfiguresServiceBusBackpressure()
    {
        var repoRoot = FindRepoRoot();
        var hostJsonPath = Path.Combine(repoRoot, "workers", "function.ingest", "host.json");

        using var document = JsonDocument.Parse(File.ReadAllText(hostJsonPath));
        var serviceBus = document.RootElement
            .GetProperty("extensions")
            .GetProperty("serviceBus");

        serviceBus.GetProperty("prefetchCount").GetInt32().Should().Be(0);
        serviceBus.GetProperty("maxConcurrentCalls").GetInt32().Should().Be(4);
        serviceBus.GetProperty("autoCompleteMessages").GetBoolean().Should().BeFalse();
        serviceBus.GetProperty("maxAutoLockRenewalDuration").GetString().Should().Be("00:35:00");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "app.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
