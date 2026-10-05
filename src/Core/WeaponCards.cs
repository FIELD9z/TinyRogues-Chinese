using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

// Weapon.GetDescription combines enum labels, stats and markup into one block.
// Recognize that block explicitly; never apply fragment replacement to prose.
public static class WeaponCards
{
    private static readonly string[] Fields = { "Weapon Type:", "Weapon Range:", "Damage Scaling:", "Attack Damage:", "Attack Speed:", "Inflicts:", "Grants:" };
    private static readonly Regex Tags = new(@"<[^>]*>", RegexOptions.CultureInvariant);
    private static readonly Regex Runs = new(@"<[^>]*>|[^<>]+", RegexOptions.CultureInvariant);
    private static readonly Regex Range = new(@"\A(?<low>[+\-]?\d+(?:[.,]\d+)?)\s+to\s+(?<high>[+\-]?\d+(?:[.,]\d+)?)(?<tail>.*)\z", RegexOptions.CultureInvariant);

    public static bool TryTranslate(Catalog catalog, string source, out string target)
    {
        target = source;
        if (string.IsNullOrEmpty(source) || source.Length > 12000) return false;
        var plain = Tags.Replace(source, "").Replace("##", "");
        var visibleRows = plain.Split('\n').Select(row => row.TrimStart(' ', '•')).ToArray();
        var weapon = new[] { "Weapon Type:", "Damage Scaling:", "Attack Damage:" }.All(field => visibleRows.Any(row => row.StartsWith(field, StringComparison.Ordinal)));
        weapon |= new[] { "武器类型：", "属性补正：", "攻击伤害：" }.All(field => visibleRows.Any(row => row.StartsWith(field, StringComparison.Ordinal)));
        var equipment = Regex.IsMatch(visibleRows[0], @"\A(?:Common|Uncommon|Rare|Epic|Legendary|Set)\s+(?:Accessory|Helmet|Boots|Gloves|Offhand|Body\s+Armor|Charm)(?:\s|\||\z)")
            && (plain.Contains("Equip Load") || plain.Contains("Attunement:") || visibleRows.Length > 1);
        equipment |= Regex.IsMatch(visibleRows[0], @"\A(?:普通|优秀|稀有|史诗|传说|套装)\s+(?:饰品|头盔|靴子|手套|副手|胸甲|护符)(?:\s|\||\z)")
            && (plain.Contains("装备负重") || plain.Contains("调谐：") || visibleRows.Length > 1);
        if (!weapon && !equipment) return false;
        var lines = source.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var visible = Tags.Replace(lines[index], "").Replace("##", "").TrimStart(' ', '•');
            var field = Fields.FirstOrDefault(value => visible.StartsWith(value, StringComparison.Ordinal));
            // Only the header and the five known stat rows belong to this schema.
            // Effects appended below the card must use their full prose templates.
            if (index != 0 && field == null)
            {
                if (catalog.TryTranslateRichLine(lines[index], "weapon-effect", out var effect)) lines[index] = effect;
                lines[index] = DisplayValues.RunsIn(catalog, lines[index]);
                // This token is emitted by the native trigger chance formatter.
                lines[index] = Regex.Replace(lines[index], @"\bchance(?=(?:<[^>]*>)*触发)", "几率");
                continue;
            }
            var translateTerms = index == 0 || field == "Weapon Type:" || field == "Weapon Range:" || field == "Attack Damage:" || field == "Inflicts:" || field == "Grants:";
            lines[index] = Runs.Replace(lines[index], run => run.Value.StartsWith("<", StringComparison.Ordinal)
                ? run.Value : TranslateRun(catalog, run.Value, field, translateTerms));
            lines[index] = DisplayValues.RunsIn(catalog, lines[index]);
        }
        target = string.Join("\n", lines);
        return target != source;
    }

    private static string TranslateRun(Catalog catalog, string run, string? field, bool translateTerms)
    {
        var start = run.Length - run.TrimStart().Length;
        var end = run.TrimEnd().Length;
        if (end <= start) return run;
        var prefix = run.Substring(0, start);
        var suffix = run.Substring(end);
        var text = run.Substring(start, end - start);
        // Weight and attunement values are emitted on the header or their own row.
        if (text.Contains("Equip Load") && catalog.TryTranslateAtom("Equip Load", "display-value", out var load))
            text = text.Replace("Equip Load", load);
        if (text.StartsWith("Attunement:", StringComparison.Ordinal) && catalog.TryTranslateAtom("Attunement:", "display-value", out var attunement))
            text = attunement + text.Substring("Attunement:".Length);
        if (text.StartsWith("##", StringComparison.Ordinal)) { prefix += "##"; text = text.Substring(2); }
        if (text.StartsWith("•", StringComparison.Ordinal))
        {
            prefix += "•";
            text = text.Substring(1);
            var spaces = text.Length - text.TrimStart().Length;
            prefix += text.Substring(0, spaces);
            text = text.Substring(spaces);
        }
        if (field != null && text.StartsWith(field, StringComparison.Ordinal))
        {
            var title = catalog.TryTranslate(field, "weapon-card", out var translated) ? translated : field;
            var remainder = text.Substring(field.Length);
            return prefix + title + (translateTerms ? TranslateValue(catalog, remainder) : remainder) + suffix;
        }
        return prefix + (translateTerms ? TranslateValue(catalog, text) : text) + suffix;
    }

    private static string TranslateValue(Catalog catalog, string value)
    {
        var start = value.Length - value.TrimStart().Length;
        var end = value.TrimEnd().Length;
        if (end <= start) return value;
        var text = value.Substring(start, end - start);
        var prefix = value.Substring(0, start);
        var suffix = value.Substring(end);
        if (catalog.TryTranslate(text, "weapon-card", out var complete)) return prefix + complete + suffix;
        var range = Range.Match(text);
        if (range.Success && catalog.TryTranslate("to", "weapon-card", out var connector))
            return prefix + range.Groups["low"].Value + connector + range.Groups["high"].Value + TranslateValue(catalog, range.Groups["tail"].Value) + suffix;
        // A field can contain several adjacent enum labels, e.g. Two-Handed Ranged.
        // Every piece must resolve in the dedicated card vocabulary, or keep it intact.
        var words = Regex.Split(text, @"\s+");
        if (words.Length > 6) return value;
        var results = new System.Collections.Generic.List<string>();
        for (var index = 0; index < words.Length;)
        {
            var found = false;
            for (var count = words.Length - index; count > 0; count--)
            {
                var candidate = string.Join(" ", words.Skip(index).Take(count));
                if (!catalog.TryTranslate(candidate, "weapon-card-term", out var term)) continue;
                results.Add(term); index += count; found = true; break;
            }
            if (!found) return value;
        }
        return prefix + string.Join(" ", results) + suffix;
    }
}
