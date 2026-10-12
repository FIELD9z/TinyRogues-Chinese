using System;

namespace TinyRogues.Chinese.Core;

public static class CharacterPanelTexts
{
    public static bool TryTranslate(Catalog catalog, string source, string context, out string target)
    {
        target = source;
        if (string.IsNullOrEmpty(source)) return false;
        if (!context.StartsWith("ui:", StringComparison.Ordinal) || !context.Contains("/Character Panel/")) return false;
        if (catalog.TryTranslateLabel(source, "character-panel", out target)) return true;
        if (catalog.TryTranslateRichLine(source, "character-panel", out target)) return true;
        if (!context.EndsWith("/Stat Line/Value", StringComparison.Ordinal)) return false;
        // Stat values contain enumerated damage types, rarity and numeric ranges.
        // Do not apply this field grammar to tooltips or ordinary dialogue.
        var normalized = source.Replace('\u00a0', ' ');
        var translated = DisplayValues.RunsIn(catalog, normalized);
        if (translated == normalized) return false;
        target = translated;
        return true;
    }
}
