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
}

