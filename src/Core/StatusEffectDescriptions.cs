using System;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

// Native status descriptions have bracketed state badges and complete effect
// rows. Translate those fields explicitly; never scan arbitrary UI paragraphs.
public static class StatusEffectDescriptions
{
    public static string Translate(Catalog catalog, string source)
    {
        source = CollectibleCards.Translate(catalog, source);
        var blocks = source.Split('\n');
        for (var count = 4; count >= 2; count--)
            for (var start = 0; start + count <= blocks.Length; start++)
            {
                var block = string.Join("\n", blocks, start, count);
                var bullet = Regex.Match(block, @"\A(?<tags>(?:<[^>]*>)*)•");
                if (bullet.Success) block = bullet.Groups["tags"].Value + block.Substring(bullet.Length);
                if (!TryRow(catalog, block, out var translated)) continue;
                if (bullet.Success) translated = "•" + translated;
                var replacements = translated.Split('\n');
                if (replacements.Length == count) Array.Copy(replacements, 0, blocks, start, count);
            }
        source = string.Join("\n", blocks);
        source = Regex.Replace(source, @"\[(?<value>(?:<[^>]*>|[^\[\]\r\n]){1,600})\]", match =>
        {
            var value = match.Groups["value"].Value;
            return catalog.TryTranslateRichLine(value, "status-badge", out var translated) ? "[" + translated + "]" : match.Value;
        });
        var rows = source.Split('\n');
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var bullet = Regex.Match(row, @"\A(?<tags>(?:<[^>]*>)*)•");
            var prefix = bullet.Success ? "•" : "";
            if (bullet.Success) row = bullet.Groups["tags"].Value + row.Substring(bullet.Length);
            if (TryRow(catalog, row, out var translated)) row = translated;
            else
            {
                var badge = row.IndexOf('[', StringComparison.Ordinal);
                if (badge > 0)
                {
                    var body = row.Substring(0, badge);
                    body = Regex.Replace(body, @"\s+(?=(?:<[^>]*>)*$)", "");
                    if (TryRow(catalog, body, out translated)) row = translated + " " + row.Substring(badge);
                }
            }
            var plain = Regex.Replace(row, "<[^>]*>", "").Replace('\u00a0', ' ');
            if (Regex.IsMatch(plain, @"\A(?:Maximum Stacks:|Tick Speed:|Effect:|Damage:|Radius:|Stacks:|Duration:|Max Stacks:|Inflicts:|持续时间：|叠加上限：|施加：|伤害：)") )
            {
                row = Regex.Replace(row, @"<[^>]*>|[^<>]+", match =>
                {
                    if (match.Value.StartsWith("<", StringComparison.Ordinal)) return match.Value;
                    return Regex.Replace(match.Value, @"(?<![A-Za-z])(?:Maximum Stacks:|Max Stacks:|Tick Speed:|Effect:|Damage:|Radius:|Stacks:|Infinite|infinite|Forever|until room completion|Until all stacks were consumed\.|Tipsiness Duration)(?![A-Za-z])", term =>
                        catalog.TryTranslate(term.Value, "status-field", out var value) ? value : term.Value);
                });
                row = DisplayValues.RunsIn(catalog, row);
            }
            rows[index] = prefix + row;
        }
        return string.Join("\n", rows);
    }
    private static bool TryRow(Catalog catalog, string source, out string target) =>
        catalog.TryTranslate(source, "status-generated", out target)
        || catalog.TryTranslateRichLine(source, "status-generated", out target);
}
