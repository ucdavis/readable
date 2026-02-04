using System.Text;
using System.Text.Json;

namespace server.core.Ingest;

public static class AdobeAccessibilityReportRetagDecider
{
    private const string FailedStatus = "failed";

    // Rules that strongly indicate the PDF needs a full re-tag (Autotag) instead of incremental remediation.
    // These align with Acrobat's Accessibility Checker categories/rules.
    private static readonly HashSet<string> RetagTriggerRules =
        new(StringComparer.Ordinal)
        {
            // Document
            MakeKey(section: "document", rule: "taggedpdf"),

            // Page Content
            MakeKey(section: "pagecontent", rule: "taggedcontent"),
            MakeKey(section: "pagecontent", rule: "taggedannotations"),
            // "Tab order" is usually fixable without full autotagging (e.g., setting page /Tabs to /S).
            MakeKey(section: "pagecontent", rule: "taggedmultimedia"),

            // Forms
            MakeKey(section: "forms", rule: "taggedformfields"),

            // Headings (optional but useful signal for broken structure)
            MakeKey(section: "headings", rule: "appropriatenesting"),
        };

    public static bool TryShouldRetag(
        string? reportJson,
        out bool shouldRetag,
        out IReadOnlyList<string> triggers,
        out string? error)
    {
        shouldRetag = false;
        triggers = Array.Empty<string>();
        error = null;

        if (string.IsNullOrWhiteSpace(reportJson))
        {
            error = "Report JSON was empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(reportJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Report JSON root was not an object.";
                return false;
            }

            if (!TryGetPropertyByNormalizedName(root, normalizedPropertyName: "detailedreport", out var detailedReport))
            {
                error = "Report JSON missing 'Detailed Report'.";
                return false;
            }

            if (detailedReport.ValueKind != JsonValueKind.Object)
            {
                error = "Report JSON 'Detailed Report' was not an object.";
                return false;
            }

            var found = new List<string>();
            foreach (var sectionProperty in detailedReport.EnumerateObject())
            {
                if (sectionProperty.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var sectionName = sectionProperty.Name;
                var sectionKey = NormalizeKey(sectionName);

                foreach (var ruleResult in sectionProperty.Value.EnumerateArray())
                {
                    if (ruleResult.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var rule = TryGetString(ruleResult, "Rule");
                    var status = TryGetString(ruleResult, "Status");

                    if (string.IsNullOrWhiteSpace(rule) || string.IsNullOrWhiteSpace(status))
                    {
                        continue;
                    }

                    if (!string.Equals(NormalizeKey(status), FailedStatus, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var ruleKey = NormalizeKey(rule);
                    if (!RetagTriggerRules.Contains(MakeKey(sectionKey, ruleKey)))
                    {
                        continue;
                    }

                    found.Add($"{sectionName}: {rule}");
                }
            }

            triggers = found;
            shouldRetag = found.Count > 0;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid report JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetPropertyByNormalizedName(
        JsonElement obj,
        string normalizedPropertyName,
        out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(NormalizeKey(prop.Name), normalizedPropertyName, StringComparison.Ordinal))
            {
                value = prop.Value;
                return true;
            }
        }

        return false;
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!obj.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string NormalizeKey(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString().Trim();
    }

    private static string MakeKey(string section, string rule) => $"{section}|{rule}";
}
