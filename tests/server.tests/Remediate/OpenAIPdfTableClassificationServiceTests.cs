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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseResult_WhenResponseIsEmpty_Throws(string response)
    {
        var act = () => InvokeParseResult(response);

        act.Should()
            .Throw<TargetInvocationException>()
            .Where(ex => ex.InnerException is InvalidOperationException
                && ex.InnerException.Message == "Empty classifier response.");
    }

    [Fact]
    public void ParseResult_WhenResponseIsInvalidJson_Throws()
    {
        var act = () => InvokeParseResult("not json");

        act.Should()
            .Throw<TargetInvocationException>()
            .Where(ex => ex.InnerException is InvalidOperationException
                && ex.InnerException.Message == "Classifier response was not valid JSON.");
    }

    [Fact]
    public void ParseResult_WhenResponseIsValidJson_ReturnsClassification()
    {
        var result = InvokeParseResult(
            """
            {"kind":"data_table","confidence":0.8,"reason":"Rows and columns contain records."}
            """);

        result.Kind.Should().Be(PdfTableKind.DataTable);
        result.Confidence.Should().Be(0.8);
        result.Reason.Should().Be("Rows and columns contain records.");
    }

    private static string InvokeBuildPrompt(PdfTableClassificationRequest request)
    {
        var method = typeof(OpenAIPdfTableClassificationService)
            .GetMethod("BuildPrompt", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (string)method.Invoke(null, [request])!;
    }

    private static PdfTableClassificationResult InvokeParseResult(string text)
    {
        var method = typeof(OpenAIPdfTableClassificationService)
            .GetMethod("ParseResult", BindingFlags.NonPublic | BindingFlags.Static)!;

        return (PdfTableClassificationResult)method.Invoke(null, [text])!;
    }
}
