using System;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

// SetBonus.DescriptionAddon has its own header and component list and can be
// displayed independently of Equipment.Description. Translate after styling.
public static class SetCards
{
    public static bool TryTranslate(Catalog catalog, string source, out string target)
    {
        target = source;
        var plain = Regex.Replace(source, "<[^>]*>", "");
        if (!Regex.IsMatch(plain, @"\A\s*\d+(?:-Set:|件套：)") || source.Contains("(*)")) return false;
        var rows = source.Split('\n');
        var effect = false;
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            effect |= Regex.Replace(row, "<[^>]*>", "").TrimStart().StartsWith("•", StringComparison.Ordinal);
            if (effect)
            {
                if (catalog.TryTranslateRichLine(row, "weapon-effect", out var body)) row = body;
                row = DisplayValues.RunsIn(catalog, row);
            }
            else
            {
                row = Regex.Replace(row, @"\[(?<value>(?:<[^>]*>|[^\[\]\r\n]){1,160})\]", badge =>
                    catalog.TryTranslateRichLine(badge.Groups["value"].Value, "weapon-card", out var value) ? "[" + value + "]" : badge.Value);
                row = Regex.Replace(row, @"<[^>]*>|[^<>]+", run =>
                {
                    if (run.Value.StartsWith("<", StringComparison.Ordinal)) return run.Value;
                    var text = run.Value;
                    text = Regex.Replace(text, @"(?<n>\d+)-Set:", match => match.Groups["n"].Value + "件套：");
                    var trimmed = text.Trim().Replace('\u00a0', ' ');
                    var badge = trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal);
                    var value = badge ? trimmed.Substring(1, trimmed.Length - 2) : trimmed;
                    if (catalog.TryTranslate(value, badge ? "weapon-card" : "display-value", out var translated))
                        text = text.Substring(0, text.Length - text.TrimStart().Length) + (badge ? "[" + translated + "]" : translated) + text.Substring(text.TrimEnd().Length);
                    return text;
                });
            }
            rows[index] = row;
        }
        target = string.Join("\n", rows);
        return target != source;
    }
}
