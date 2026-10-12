using System;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

// Called only by native consumable/companion description getters. Unknown
// prose remains untouched; the runtime audit reports every unresolved row.
public static class CollectibleCards
{
    public static string Translate(Catalog catalog, string source)
    {
        var rows = source.Replace("(br)", "\n").Replace("(*)", "•").Split('\n');
        for (var count = 3; count >= 2; count--)
            for (var start = 0; start + count <= rows.Length; start++)
            {
                var block = string.Join("\n", rows, start, count);
                if (!TryRow(catalog, block, out var translated)) continue;
                var replacements = translated.Split('\n');
                if (replacements.Length == count) Array.Copy(replacements, 0, rows, start, count);
            }
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (TryRow(catalog, row, out var whole)) { rows[i] = whole; continue; }
            var bullet = Regex.Match(row, @"\A(?<tags>(?:<[^>]*>)*)•");
            var prefix = bullet.Success ? "•" : "";
            if (bullet.Success) row = bullet.Groups["tags"].Value + row.Substring(bullet.Length);
            if (TryRow(catalog, row, out whole)) row = whole;
            var plain = Regex.Replace(row, "<[^>]*>", "").Replace('\u00a0', ' ');
            var header = i == 0 && plain.Contains("//");
            var chips = Regex.IsMatch(plain.Trim(), @"\A(?:\[(?:Booze|Buff|Infusion|Potion|Tick)\]\s*)+\z");
            var field = Regex.IsMatch(plain, @"\A(?:Duration:|Max Stacks:|Summon Limit:|Attack Damage:|Attack Speed:|Damage Scaling:|Each Grants:|Inflicts:)");
            if (header || chips || field)
                row = Regex.Replace(row, @"<[^>]*>|[^<>]+", match =>
                {
                    if (match.Value.StartsWith("<", StringComparison.Ordinal)) return match.Value;
                    var run = match.Value.Replace('\u00a0', ' ');
                    // Only schema labels and category chips use this vocabulary.
                    run = Regex.Replace(run, @"(?<![A-Za-z])(?:Fishing Booty|Gift Box|Tipsiness Duration|Tipsiness Limit|Summon Limit:|Attack Damage:|Attack Speed:|Damage Scaling:|Each Grants:|Max Stacks:|Duration:|Inflicts:|Consumable|Companion|Material|Treasure|Legendary|Uncommon|Common|Rare|Epic|Potion|Infusion|Vitamin|Blessing|Booze|Buff|Tick|Tome|Item|Permanent)(?![A-Za-z])", term => catalog.TryTranslate(term.Value, "collectible-card-term", out var translated) ? translated : term.Value);
                    run = Regex.Replace(run, @"//(?:Sticky //Goo|Rupture|Scorch|Judgement|Poison|Corruption|Fear)", term => catalog.TryTranslate(term.Value, "collectible-card-term", out var translated) ? translated : term.Value);
                    return run;
                });
            if (field) row = DisplayValues.RunsIn(catalog, row);
            rows[i] = prefix + row;
        }
        return string.Join("\n", rows);
    }
    private static bool TryRow(Catalog catalog, string row, out string target) =>
        catalog.TryTranslate(row, "collectible-card", out target)
        || catalog.TryTranslateRichLine(row, "collectible-card", out target)
        || catalog.TryTranslateRichLine(row, "*", out target);
}
