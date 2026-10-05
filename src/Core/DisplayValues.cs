using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

internal static class DisplayValues
{
    private static readonly Regex Runs = new(@"<[^>]*>|[^<>]+", RegexOptions.CultureInvariant);
    private static readonly Regex Range = new(@"\A(?<low>[+\-]?\d+(?:[.,]\d+)?)(?:\s+to\s+|\s*至\s*)(?<high>[+\-]?\d+(?:[.,]\d+)?)(?<tail>.*)\z", RegexOptions.CultureInvariant);
    private static readonly Regex Numeric = new(@"\A[+\-−x]?(?:\d+(?:[.,]\d+)?|[.,]\d+)%?\z", RegexOptions.CultureInvariant);
    internal static string RunsIn(Catalog catalog, string source) => Runs.Replace(source, run => run.Value.StartsWith("<", StringComparison.Ordinal) ? run.Value : Value(catalog, run.Value) ?? run.Value);
    internal static string RichTerm(Catalog catalog, string source)
    {
        var plain = Regex.Replace(source, "<[^>]*>", "").Replace('\u00a0', ' ');
        if (!catalog.TryTranslateAtom(plain, "display-value", out var translated)) return RunsIn(catalog, source);
        var emitted = false;
        return Runs.Replace(source, run =>
        {
            if (run.Value.StartsWith("<", StringComparison.Ordinal)) return run.Value;
            if (emitted) return "";
            emitted = true; return translated;
        });
    }
    internal static string? Value(Catalog catalog, string source)
    {
        var start = source.Length - source.TrimStart().Length;
        var end = source.TrimEnd().Length;
        if (end <= start) return source;
        var plain = source.Substring(start, end - start);
        var normalized = plain.Replace('\u00a0', ' ');
        var prefix = source.Substring(0, start);
        var suffix = source.Substring(end);
        if (catalog.TryTranslateAtom(normalized, "display-value", out var term)) return prefix + term + suffix;
        if (catalog.IsTranslatedValue(normalized)) return source;
        if (normalized.StartsWith("Attunement:", StringComparison.Ordinal) && catalog.TryTranslateAtom("Attunement:", "display-value", out var attunement))
            return prefix + attunement + normalized.Substring("Attunement:".Length) + suffix;
        if (Regex.IsMatch(normalized, @"\A(?:\|\s*)?[+\-]?\d+ Equip Load\z") && catalog.TryTranslateAtom("Equip Load", "display-value", out var load))
            return prefix + plain.Replace("Equip Load", load) + suffix;
        if (Numeric.IsMatch(plain)) return source;
        var chance = Regex.Match(normalized, @"\A(?<n>[+\-]?\d+(?:\.\d+)?%) chance\z");
        if (chance.Success) return prefix + chance.Groups["n"].Value + "几率" + suffix;
        if (normalized.EndsWith(" Damage", StringComparison.Ordinal))
        {
            var type = normalized.Substring(0, normalized.Length - 7);
            if (catalog.TryTranslateAtom(type, "display-value", out var damageType) && catalog.TryTranslateAtom("Damage", "display-value", out var damageLabel))
                return prefix + damageType + damageLabel + suffix;
        }
        var range = Range.Match(plain);
        if (range.Success)
        {
            var tail = Value(catalog, range.Groups["tail"].Value);
            if (tail != null && catalog.TryTranslate("to", "weapon-card", out var connector))
                return prefix + range.Groups["low"].Value + connector + range.Groups["high"].Value + tail + suffix;
        }
        // Enumerated status lists are values, not English sentences.
        if (normalized.Contains(", "))
        {
            var parts = normalized.Split(new[] { ", " }, StringSplitOptions.None).Select(part => Value(catalog, part)).ToArray();
            if (parts.All(part => part != null)) return prefix + string.Join("、", parts) + suffix;
        }
        return null;
    }
}
