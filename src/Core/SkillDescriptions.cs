using System;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

// Used only at the native skill Description boundary, after named values have
// been expanded. These are complete generated fields, badges and value units.
public static class SkillDescriptions
{
    public static string Translate(Catalog catalog, string source)
    {
        foreach (var sentence in new[] { "Recover all Hearts<Heart>.", "Gain Stamina Surge.", "Gain a Soul Heart<Soul_Heart> if you don't have any." })
            if (catalog.TryTranslate(sentence, "skill-generated", out var target)) source = source.Replace(sentence, target);
        return Regex.Replace(source, @"<[^>]*>|[^<>]+", match =>
        {
            if (match.Value.StartsWith("<", StringComparison.Ordinal)) return match.Value;
            var run = match.Value;
            var normalized = run.Trim().Replace('\u00a0', ' ');
            var prefix = Regex.Match(normalized, @"\A(?:\(\*\)|•)?(?:##)?").Value;
            var field = normalized.Substring(prefix.Length);
            if (catalog.TryTranslateAtom(field, "skill-field", out var translated))
                return run.Substring(0, run.Length - run.TrimStart().Length) + prefix + translated + run.Substring(run.TrimEnd().Length);
            run = Regex.Replace(run, @"\[(?<term>[^\[\]]+)\]", badge =>
                catalog.TryTranslateAtom(badge.Groups["term"].Value.Replace('\u00a0', ' '), "skill-field", out var term) ? "[" + term + "]" : badge.Value);
            run = Regex.Replace(run, @"(?<n>\d+(?:\.\d+)?) times per second", value => "每秒" + value.Groups["n"].Value + "次");
            run = Regex.Replace(run, @"(?<n>\d+(?:\.\d+)?) seconds", value => value.Groups["n"].Value + "秒");
            run = Regex.Replace(run, @"\((?<n>\d+(?:\.\d+)?) Seconds Cooldown\)", value => "(" + value.Groups["n"].Value + "秒冷却)");
            run = Regex.Replace(run, @"(?<a>\d+(?:\.\d+)?)[\s\u00a0]+to[\s\u00a0]+(?<b>\d+(?:\.\d+)?)", value => value.Groups["a"].Value + "至" + value.Groups["b"].Value);
            // Element values can adjoin a translated body or a rich-text icon.
            run = Regex.Replace(run, @"(?<![A-Za-z])(?:Physical|Striking|Slashing|Thrusting|Fire|Cold|Lightning|Poison|Radiant|Dark|Explosion)(?![A-Za-z])", value =>
                catalog.TryTranslateAtom(value.Value, "display-value", out var term) ? term : value.Value);
            // Refill and cooldown grammar is emitted in individual color runs.
            if (Regex.IsMatch(normalized, @"\A(?:\(Boss|\(\[\[x\d+\]\]\)|\(\[\[\d+(?:\.\d+)?\]\] per|Instant \(\d+(?:\.\d+)? Seconds Cooldown\)|seconds)[\s)]*\z"))
            {
                run = run.Replace("Boss", "首领").Replace(" per", "／每").Replace("Instant", "瞬发").Replace(" Seconds Cooldown", "秒冷却").Replace("seconds", "秒");
            }
            return run;
        });
    }
}
