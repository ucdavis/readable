using FluentAssertions;
using server.core.Ingest;

namespace server.tests.Ingest;

public class AdobeAccessibilityReportRetagDeciderTests
{
    [Fact]
    public void TryShouldRetag_WhenReportJsonEmpty_ReturnsFalse()
    {
        var ok = AdobeAccessibilityReportRetagDecider.TryShouldRetag(
            reportJson: "",
            out var shouldRetag,
            out var triggers,
            out var error);

        ok.Should().BeFalse();
        shouldRetag.Should().BeFalse();
        triggers.Should().BeEmpty();
        error.Should().NotBeNull();
    }

    [Fact]
    public void TryShouldRetag_WhenReportHasTriggerFailures_ReturnsTrueAndTriggers()
    {
        var ok = AdobeAccessibilityReportRetagDecider.TryShouldRetag(
            reportJson: SampleReport_WithTriggerFailures,
            out var shouldRetag,
            out var triggers,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        shouldRetag.Should().BeTrue();
        triggers.Should().Contain(new[]
        {
            "Document: Tagged PDF",
            "Page Content: Tagged content",
            "Page Content: Tagged annotations",
            "Headings: Appropriate nesting",
        });
    }

    [Fact]
    public void TryShouldRetag_WhenOnlyNonTriggerFailures_ReturnsTrueAndNoTriggers()
    {
        var ok = AdobeAccessibilityReportRetagDecider.TryShouldRetag(
            reportJson: SampleReport_WithNonTriggerFailures,
            out var shouldRetag,
            out var triggers,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        shouldRetag.Should().BeFalse();
        triggers.Should().BeEmpty();
    }

    [Fact]
    public void TryShouldRetag_WhenDetailedReportKeyHasNoSpace_StillMatches()
    {
        var ok = AdobeAccessibilityReportRetagDecider.TryShouldRetag(
            reportJson: SampleReport_DetailedReportWithoutSpace,
            out var shouldRetag,
            out var triggers,
            out var error);

        ok.Should().BeTrue();
        error.Should().BeNull();
        shouldRetag.Should().BeFalse();
        triggers.Should().BeEmpty();
    }

    private const string SampleReport_WithTriggerFailures = """
        {
          "Summary": {
            "Description": "The checker found problems which may prevent the document from being fully accessible.",
            "Needs manual check": 3,
            "Passed manually": 0,
            "Failed manually": 0,
            "Skipped": 0,
            "Passed": 10,
            "Failed": 19
          },
          "Detailed Report": {
            "Document": [
              { "Rule": "Tagged PDF", "Status": "Failed", "Description": "Document is tagged PDF" }
            ],
            "Page Content": [
              { "Rule": "Tagged content", "Status": "Failed", "Description": "All page content is tagged" },
              { "Rule": "Tagged annotations", "Status": "Failed", "Description": "All annotations are tagged" },
              { "Rule": "Tab order", "Status": "Failed", "Description": "Tab order is consistent with structure order" },
              { "Rule": "Tagged multimedia", "Status": "Passed", "Description": "All multimedia objects are tagged" }
            ],
            "Forms": [
              { "Rule": "Tagged form fields", "Status": "Passed", "Description": "All form fields are tagged" }
            ],
            "Headings": [
              { "Rule": "Appropriate nesting", "Status": "Failed", "Description": "Appropriate nesting" }
            ]
          }
        }
        """;

    private const string SampleReport_WithNonTriggerFailures = """
        {
          "Summary": { "Failed": 2 },
          "Detailed Report": {
            "Document": [
              { "Rule": "Title", "Status": "Failed", "Description": "Document title is showing in title bar" },
              { "Rule": "Primary language", "Status": "Failed", "Description": "Text language is specified" }
            ],
            "Alternate Text": [
              { "Rule": "Figures alternate text", "Status": "Failed", "Description": "Figures require alternate text" }
            ],
            "Page Content": [
              { "Rule": "Navigation links", "Status": "Needs manual check", "Description": "Navigation links are not repetitive" }
            ]
          }
        }
        """;

    private const string SampleReport_DetailedReportWithoutSpace = """
        {
          "Summary": { "Failed": 1 },
          "DetailedReport": {
            "PageContent": [
              { "Rule": "Tab order", "Status": "Failed", "Description": "Tab order is consistent with structure order" }
            ]
          }
        }
        """;
}
