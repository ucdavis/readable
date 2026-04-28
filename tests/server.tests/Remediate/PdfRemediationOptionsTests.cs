using FluentAssertions;
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
}
