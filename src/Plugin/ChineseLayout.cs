using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UI.Text.World_Text_Boxes;

namespace TinyRogues.Chinese;

// Native TMP preferred-height calculation can disagree with the generated
// mixed-font mesh. Keep the rendered measurement, never an accumulated offset.
internal static class ChineseLayout
{
    private sealed class Measurement
    {
        internal string Text = "";
        internal float Width, FontSize, Spacing, Height;
        internal IntPtr Font;
        internal Vector4 Margin;
        internal FontStyles Style;
        internal bool Wrap, AutoSize;
        internal float CharacterSpacing, WordSpacing, ParagraphSpacing;
    }
    private static readonly Dictionary<IntPtr, Measurement> Measured = new();
    private static readonly Dictionary<IntPtr, TMP_Text> Pending = new();
    private static readonly Dictionary<IntPtr, (TMP_Text Text, bool Wrap)> Choices = new();
    private static bool Applies(TMP_Text text) => text.name == "Text"
        && text.GetComponentInParent<TextBox>() != null
        && System.Text.RegularExpressions.Regex.IsMatch(text.text ?? "", "[\u4e00-\u9fff]");
    private static bool Matches(Measurement m, TMP_Text text) => m.Text == text.text
        && Math.Abs(m.Width - text.rectTransform.rect.width) < .01f
        && m.FontSize == text.fontSize && m.Spacing == text.lineSpacing
        && m.Font == text.font.Pointer && m.Margin == text.margin
        && m.Style == text.fontStyle && m.Wrap == text.enableWordWrapping && m.AutoSize == text.enableAutoSizing
        && m.CharacterSpacing == text.characterSpacing && m.WordSpacing == text.wordSpacing && m.ParagraphSpacing == text.paragraphSpacing;
    internal static void AfterMesh(TextMeshProUGUI __instance)
    {
        if (!Applies(__instance) || __instance.textInfo.lineCount == 0) return;
        var info = __instance.textInfo;
        var height = info.lineInfo[0].ascender - info.lineInfo[info.lineCount - 1].descender;
        if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0) return;
        if (Measured.TryGetValue(__instance.Pointer, out var old) && Matches(old, __instance) && Math.Abs(old.Height-height)<.01f) return;
        Measured[__instance.Pointer] = new Measurement { Text=__instance.text, Width=__instance.rectTransform.rect.width,
            FontSize=__instance.fontSize, Spacing=__instance.lineSpacing, Height=height,
            Font=__instance.font.Pointer, Margin=__instance.margin, Style=__instance.fontStyle,
            Wrap=__instance.enableWordWrapping, AutoSize=__instance.enableAutoSizing,
            CharacterSpacing=__instance.characterSpacing, WordSpacing=__instance.wordSpacing, ParagraphSpacing=__instance.paragraphSpacing };
        Pending[__instance.Pointer] = __instance;
    }
    internal static void PreferredHeight(TMP_Text __instance, ref float __result)
    {
        if (!Applies(__instance)) return;
        if (Measured.TryGetValue(__instance.Pointer, out var m) && Matches(m, __instance))
            __result = Math.Max(__result, m.Height + Math.Max(0, m.Margin.y) + Math.Max(0, m.Margin.w));
        // Leave room for fallback-font ascenders at the next paragraph boundary.
        __result += __instance.fontSize * .4f;
    }
    internal static void Destroyed(TextMeshProUGUI __instance)
    {
        Measured.Remove(__instance.Pointer);
        Pending.Remove(__instance.Pointer);
        Choices.Remove(__instance.Pointer);
    }
    internal static void RestoreChoice(TMP_Text text)
    {
        if (!Choices.TryGetValue(text.Pointer, out var state)) return;
        Choices.Remove(text.Pointer);
        text.enableWordWrapping = state.Wrap;
    }
    internal static void SingleLineChoice(TMP_Text text)
    {
        Choices[text.Pointer] = (text, text.enableWordWrapping);
        text.enableWordWrapping = false;
    }
    internal static void ReleaseChoices()
    {
        foreach (var state in Choices.Values) if (state.Text != null) state.Text.enableWordWrapping = state.Wrap;
        Choices.Clear();
    }
    internal static void Flush()
    {
        foreach (var text in Pending.Values)
            if (text != null && text.gameObject.activeInHierarchy) LayoutRebuilder.MarkLayoutForRebuild(text.rectTransform);
        Pending.Clear();
    }
}
