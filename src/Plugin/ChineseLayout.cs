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
    private sealed class SpacingState
    {
        internal string Text = "";
        internal IntPtr Font;
        internal float Original, Applied;
    }
    private static readonly Dictionary<IntPtr, SpacingState> Spacings = new();
    private static readonly Dictionary<IntPtr, (TMP_Text Text, string Source, IntPtr Font, float Baseline, float Value)> PendingSpacing = new();
    private static bool IsPanel(TMP_Text text) => text.GetComponentInParent<UI.Title_Screen.PlayerClassInspector>() != null
        || text.GetComponentInParent<Meta_Perk_Tree.UI.MetaPerkTooltip>() != null
        || text.GetComponentInParent<UI.Help_Texts.HelpTextManager>() != null;
    private static bool Applies(TMP_Text text) => (text.name == "Text" && text.GetComponentInParent<TextBox>() != null
        || text.GetComponentInParent<UI.Text.Tag_Boxes.TooltipBox>() != null || IsPanel(text))
        && System.Text.RegularExpressions.Regex.IsMatch(text.text ?? "", "[\u4e00-\u9fff]");
    internal static void BeforeMesh(TextMeshProUGUI __instance)
    {
        if (__instance.font == null) return;
        if (!Spacings.TryGetValue(__instance.Pointer, out var state)) return;
        if (state.Text == __instance.text && state.Font == __instance.font.Pointer && state.Applied == __instance.lineSpacing) return;
        if (state.Applied == __instance.lineSpacing) __instance.lineSpacing = state.Original;
        Spacings.Remove(__instance.Pointer);
        PendingSpacing.Remove(__instance.Pointer);
    }
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
        if (info.lineCount > 1 && __instance.fontSize > 0)
        {
            float gap = float.MaxValue;
            for (var i=1; i<info.lineCount; i++)
                if (info.lineInfo[i-1].visibleCharacterCount > 0 && info.lineInfo[i].visibleCharacterCount > 0)
                {
                    gap = Math.Min(gap, info.lineInfo[i-1].descender - info.lineInfo[i].ascender);
                    float bottom=float.MaxValue, top=float.MinValue;
                    for (var c=0; c<info.characterCount; c++) if (info.characterInfo[c].isVisible)
                    {
                        var ch=info.characterInfo[c];
                        if(ch.lineNumber==i-1)bottom=Math.Min(bottom,ch.bottomLeft.y);
                        if(ch.lineNumber==i)top=Math.Max(top,ch.topRight.y);
                    }
                    if(bottom!=float.MaxValue && top!=float.MinValue)gap=Math.Min(gap,bottom-top);
                }
            var needed = __instance.fontSize * .2f - gap;
            // TMP lineSpacing uses hundredths of an em. Correct from the
            // generated line metrics and re-layout on the next frame.
            if (needed > .01f && __instance.lineSpacing < 200)
                PendingSpacing[__instance.Pointer] = (__instance, __instance.text, __instance.font.Pointer, __instance.lineSpacing, Math.Min(200, __instance.lineSpacing + needed * 100 / __instance.fontSize));
        }
        var height = info.lineInfo[0].ascender - info.lineInfo[info.lineCount - 1].descender;
        if (info.characterCount > 0)
        {
            float bottom=float.MaxValue, top=float.MinValue;
            for(var c=0;c<info.characterCount;c++) if(info.characterInfo[c].isVisible)
            { bottom=Math.Min(bottom,info.characterInfo[c].bottomLeft.y); top=Math.Max(top,info.characterInfo[c].topRight.y); }
            if(bottom!=float.MaxValue)height=Math.Max(height,top-bottom);
        }
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
        Spacings.Remove(__instance.Pointer);
        PendingSpacing.Remove(__instance.Pointer);
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
        foreach (var item in PendingSpacing.Values)
        {
            var text = item.Text;
            if (text == null || !text.gameObject.activeInHierarchy || text.font == null || text.text != item.Source || text.font.Pointer != item.Font || text.lineSpacing != item.Baseline || !Applies(text)) continue;
            if (!Spacings.TryGetValue(text.Pointer, out var state))
                Spacings[text.Pointer] = state = new SpacingState { Text=text.text, Font=text.font.Pointer, Original=text.lineSpacing };
            state.Applied = item.Value;
            text.lineSpacing = item.Value;
            LayoutRebuilder.MarkLayoutForRebuild(text.rectTransform);
            var tooltip = text.GetComponentInParent<UI.Text.Tag_Boxes.TooltipBox>();
            if (tooltip != null) { tooltip.height = new Il2CppSystem.Nullable<int>(); tooltip.TryFetchHeight(); }
        }
        PendingSpacing.Clear();
        foreach (var text in Pending.Values)
            if (text != null && text.gameObject.activeInHierarchy) LayoutRebuilder.MarkLayoutForRebuild(text.rectTransform);
        Pending.Clear();
    }
}
