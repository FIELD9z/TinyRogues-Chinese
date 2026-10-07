using System;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

// This adapter is called only from Trait.Description. Each complete row must
// match a reviewed template; unknown prose stays unchanged and is audited.
public static class TraitDescriptions
{
    public static string Translate(Catalog catalog, string source)
    {
        var rows = source.Replace("(br)", "\n").Replace("(*)", "•").Split('\n');
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            // The game appends category chips after a complete effect sentence.
            // Treat these as their own fields, not part of its prose template.
            var chips = Regex.Match(row, @"(?:(?:<[^>]*>|\s)*\[(?:<[^>]*>)*(?:Tick|Buff|Emotion)(?:<[^>]*>)*\](?:<[^>]*>|\s)*)+$");
            var suffix = "";
            if (chips.Success)
            {
                suffix = chips.Value.Replace(">Tick<", ">周期<").Replace("[Tick]", "[周期]").Replace(">Buff<", ">增益<").Replace("[Buff]", "[增益]").Replace(">Emotion<", ">情绪<").Replace("[Emotion]", "[情绪]");
                row = row.Substring(0, chips.Index);
            }
            var prefix = row.StartsWith("•", StringComparison.Ordinal) ? "•" : "";
            if (prefix.Length > 0) row = row.Substring(1);
            if (catalog.TryTranslateRichLine(row, "trait-generated", out var translated)) rows[i] = prefix + translated + suffix;
            else if (suffix.Length > 0) rows[i] = prefix + row + suffix;
        }
        return string.Join("\n", rows).Replace("<Tick>", "<color=#808080>[周期]</color>");
    }
}
