using System;

namespace CodexQuota.Usage
{
    /// <summary>Short plan labels for UI next to provider titles (avoids repeating the app name).</summary>
    public static class PlanDisplayNames
    {
        public static string Shorten(ProviderId id, string? plan)
        {
            if (string.IsNullOrWhiteSpace(plan))
                return string.Empty;

            var text = plan.Trim();
            foreach (var prefix in PrefixesFor(id))
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..].Trim();
                    break;
                }
            }

            if (text.StartsWith('(') && text.EndsWith(')'))
                text = text[1..^1].Trim();

            return text;
        }

        /// <summary>Plan text for the dashboard title; empty when the display name already includes the tier.</summary>
        public static string ForTitle(ProviderId id, string displayName, string? plan)
        {
            var shortened = Shorten(id, plan);
            return IsRedundantWithDisplayName(displayName, shortened) ? string.Empty : shortened;
        }

        public static bool IsRedundantWithDisplayName(string displayName, string planLabel)
        {
            if (string.IsNullOrWhiteSpace(planLabel))
                return true;

            var name = displayName.Trim();
            var plan = planLabel.Trim();
            return name.EndsWith(plan, StringComparison.OrdinalIgnoreCase)
                || name.Contains($" {plan}", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] PrefixesFor(ProviderId id) => id switch
        {
            ProviderId.Codex => ["ChatGPT ", "Codex ", "OpenAI "],
            _ => Array.Empty<string>(),
        };
    }
}
