using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using server.core.Ingest;
using server.core.Remediate;

namespace server.tests.Remediate;

public sealed class PdfRemediationOptionsTests
{
    [Fact]
    public void Defaults_DemoteSmallTablesWithoutHeaders_IsTrue()
    {
        new PdfRemediationOptions().DemoteSmallTablesWithoutHeaders.Should().BeTrue();
    }

    [Fact]
    public void Defaults_NoHeaderTableClassificationTimeoutSeconds_IsThirty()
    {
        new PdfRemediationOptions().NoHeaderTableClassificationTimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void Defaults_PromoteFirstRowHeadersForNoHeaderTables_IsTrue()
    {
        new PdfRemediationOptions().PromoteFirstRowHeadersForNoHeaderTables.Should().BeTrue();
    }

    [Fact]
    public void Defaults_UseAiCharacterEncodingRepair_IsTrue()
    {
        new PdfRemediationOptions().UseAiCharacterEncodingRepair.Should().BeTrue();
    }

    [Fact]
    public void IngestOptions_WhenAiCharacterEncodingRepairMissing_DefaultsTrue()
    {
        BuildRemediationOptions().UseAiCharacterEncodingRepair.Should().BeTrue();
    }

    [Fact]
    public void IngestOptions_WhenAiCharacterEncodingRepairEnvironmentFalse_DisablesRepair()
    {
        BuildRemediationOptions(new Dictionary<string, string?>
        {
            ["INGEST_USE_AI_CHARACTER_ENCODING_REPAIR"] = "false",
        }).UseAiCharacterEncodingRepair.Should().BeFalse();
    }

    [Fact]
    public void IngestOptions_WhenAiCharacterEncodingRepairConfigFalse_DisablesRepair()
    {
        BuildRemediationOptions(new Dictionary<string, string?>
        {
            ["Ingest:UseAiCharacterEncodingRepair"] = "false",
        }).UseAiCharacterEncodingRepair.Should().BeFalse();
    }

    private static PdfRemediationOptions BuildRemediationOptions(
        IDictionary<string, string?>? configurationValues = null)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddFileIngest(o =>
        {
            o.UseAdobePdfServices = false;
            o.UsePdfRemediationProcessor = true;
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PdfRemediationOptions>>().Value;
    }
}
