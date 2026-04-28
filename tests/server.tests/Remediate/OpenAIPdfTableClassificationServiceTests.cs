using System.Reflection;
using FluentAssertions;
using server.core.Remediate.Table;

namespace server.tests.Remediate;

public sealed class OpenAIPdfTableClassificationServiceTests
{
    [Fact]
    public void BuildPrompt_NormalizesBlanksAndTruncatesSampledCells()
    {
        var longCell = new string('x', 250);
        var request = new PdfTableClassificationRequest(
            RowCount: 1,
            MaxColumnCount: 3,
            HasNestedTable: false,
            Rows:
            [
                [
                    longCell,
                    "  Alpha\n\tBeta  ",
                    "   ",
                ],
            ],
            PrimaryLanguage: null);

        var prompt = InvokeBuildPrompt(request);

        prompt.Should().Contain($"{new string('x', 200)}...");
        prompt.Should().NotContain(new string('x', 201));
        prompt.Should().Contain("Alpha Beta");
        prompt.Should().Contain("[blank]");
    }

    private static string InvokeBuildPrompt(PdfTableClassificationRequest request)
    {
        var method = typeof(OpenAIPdfTableClassificationService)
            .GetMethod("BuildPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (string)method.Invoke(null, [request])!;
    }
}
