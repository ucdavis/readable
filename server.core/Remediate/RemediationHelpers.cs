using System.Text;
using OpenAI.Chat;

namespace server.core.Remediate;

internal static class RemediationHelpers
{
    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var inWhitespace = true;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(ch);
            inWhitespace = false;
        }

        return sb.ToString().Trim();
    }

    public static string ExtractFirstTextOrEmpty(ChatCompletion completion)
    {
        if (completion.Content.Count == 0)
        {
            return string.Empty;
        }

        return completion.Content[0].Text ?? string.Empty;
    }
}

