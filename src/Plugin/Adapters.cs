using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UI.Dialogue_System;
using UI.Text;

namespace TinyRogues.Chinese;

internal static class Adapters
{
    private static readonly Harmony Patches = new(Plugin.Id);
    [ThreadStatic] private static bool _writing;
    public static void Install()
    {
        var messages = typeof(DialogueSystemController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name is "ShowMessage" or "PromptMessage")
            .Where(method => method.GetParameters().FirstOrDefault()?.ParameterType == typeof(string)).ToArray();
        if (messages.Count(method => method.Name == "ShowMessage") != 1 || messages.Count(method => method.Name == "PromptMessage") != 2)
            throw new MissingMethodException("Game dialogue signatures changed; refusing partial adapter installation.");
        foreach (var message in messages)
            Patch(message, message.GetParameters()[2].ParameterType == typeof(Il2CppSystem.Collections.Generic.IEnumerable<Choice>) ? nameof(PrepareChoices) : nameof(PrepareMessage));
        foreach (var method in typeof(UnityEngine.TextCore.LowLevel.FontEngine).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "LoadFontFace" && method.ReturnType == typeof(UnityEngine.TextCore.LowLevel.FontEngineError)
                && method.GetParameters().Length is 2 or 3 && method.GetParameters()[0].ParameterType == typeof(UnityEngine.Font)
                && method.GetParameters().Skip(1).All(parameter => parameter.ParameterType == typeof(int))))
            Patch(method, nameof(LoadChineseFontFace));
        var descriptions = typeof(Stats.StatDescriptionUtility).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "FillInDescriptionValues" or "FillInDescriptionValuesForStatusEffect")
            .Where(method => method.ReturnType == typeof(string) && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(string)).ToArray();
        if (descriptions.Length != 2) throw new MissingMethodException("Description formatter signatures changed.");
        foreach (var description in descriptions) Patch(description, nameof(PrepareDescription));
        PatchScoped(AccessTools.Method(typeof(Weapons.Weapon), "GetDescription"), nameof(PrepareWeapon));
        PatchScoped(AccessTools.Method(typeof(Weapons.Weapon), "ShapeShiftWeaponDescription"), nameof(PrepareWeapon));
        PatchScoped(AccessTools.Method(typeof(Combat.Equipment.Equipment), "Description"), nameof(PrepareEquipment));
        // Skill descriptions use named game tokens as well as the common stat formatter.
        // Swap only descriptive fields for the duration of the native render call.
        foreach (var type in typeof(Skill_System.SkillBehaviour).Assembly.GetTypes().Where(type => typeof(Skill_System.SkillBehaviour).IsAssignableFrom(type)))
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "Description" && !method.IsAbstract && method.ReturnType == typeof(string)))
                PatchScoped(method, nameof(PrepareSkill));
        PatchScoped(AccessTools.Method(typeof(Traits.Trait), "Description"), nameof(PrepareTrait));
        PatchScoped(AccessTools.PropertyGetter(typeof(Cinder_System.CinderModifier), "Description"), nameof(PrepareCinder));
        PatchScoped(AccessTools.PropertyGetter(typeof(World_Progression.WorldObjective), "Description"), nameof(PrepareObjective));
        Patch(AccessTools.PropertyGetter(typeof(World_Progression.WorldObjective), "Description"), nameof(AfterObjectiveDescription), true);
        Patch(AccessTools.PropertyGetter(typeof(Meta_Perk_Tree.System.MetaPerk), "Title"), nameof(AfterMetaTitle), true);
        Patch(AccessTools.PropertyGetter(typeof(Player.PlayerClass), "Name"), nameof(AfterClassName), true);
        Patch(typeof(TMP_Text).GetProperty("text")!.SetMethod!, nameof(AssignText));
        // Native text formatting has completed here, including numeric SetText overloads.
        foreach (var method in typeof(TMP_Text).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "SetText" && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(string)))
            Patch(method, nameof(AfterText), true);
        // Serialized prefab labels need one pass when enabled; no periodic text scanning.
        Patch(AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"), nameof(AfterText), true);
        // TextMeshProxy performs semantic coloring and icon expansion before this return value.
        Patch(AccessTools.Method(typeof(TextMeshProxy), "ApplyColorToText"), nameof(BeforeStyling));
        Patch(AccessTools.Method(typeof(TextMeshProxy), "GetTextWithKeyTagsColored"), nameof(BeforeStyling));
        Patch(AccessTools.Method(typeof(TextMeshProxy), "ApplyColorToText"), nameof(AfterStyling), true);
        Patch(AccessTools.Method(typeof(DialogueSystemController), "HideMessage"), nameof(ReleaseDialogue), true);
        Plugin.Current.Info("Adapters installed: three dialogue methods, two description template formatters, TMP assignment/formatted text, prefab labels, and styled equipment/skill UI.");
    }
    private static void Patch(MethodInfo? method, string callback, bool after = false)
    {
        if (method == null) throw new MissingMethodException(callback);
        var patch = new HarmonyMethod(typeof(Adapters).GetMethod(callback, BindingFlags.NonPublic | BindingFlags.Static));
        Patches.Patch(method, prefix: after ? null : patch, postfix: after ? patch : null);
    }
    public static void Remove() => Patches.UnpatchSelf();

    private static void ReleaseDialogue(DialogueSystemController __instance)
    {
        if (__instance.text != null && __instance.text.Pointer == Plugin.Current.DialogueBody) Plugin.Current.DialogueBody = IntPtr.Zero;
    }

    private static bool LoadChineseFontFace(UnityEngine.Font __0, int __1, ref UnityEngine.TextCore.LowLevel.FontEngineError __result)
    {
        var source = Plugin.Current.SourceFont;
        if (source == null || __0 == null || source.Pointer != __0.Pointer) return true;
        __result = UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(Plugin.Current.FontPath, __1, 0);
        return false;
    }

    private static void PatchScoped(MethodInfo? method, string callback)
    {
        if (method == null) throw new MissingMethodException(callback);
        Patches.Patch(method,
            prefix: new HarmonyMethod(typeof(Adapters).GetMethod(callback, BindingFlags.NonPublic | BindingFlags.Static)),
            finalizer: new HarmonyMethod(typeof(Adapters).GetMethod(nameof(RestoreDescription), BindingFlags.NonPublic | BindingFlags.Static)));
    }
    private sealed class DescriptionState
    {
        public Skill_System.SkillBehaviour? Skill;
        public Traits.Trait? Trait;
        public string Original = "";
        public Action? Restore;
    }
    private static readonly System.Collections.Generic.HashSet<string> SharedMethods = new(StringComparer.Ordinal);
    private static bool IsNative<T>(Il2CppSystem.Object instance) where T : Il2CppSystem.Object
    {
        // IL2CPP may fold trivial getters from unrelated classes to the same
        // native address. Harmony's CLR parameter type does not validate that object.
        if (instance.TryCast<T>() != null) return true;
        if (Plugin.Current.Diagnostic)
        {
            var mismatch = typeof(T).FullName + " <- " + instance.GetIl2CppType().FullName;
            if (SharedMethods.Add(mismatch)) Plugin.Current.Info("Shared native getter skipped: " + mismatch);
        }
        return false;
    }
    private static void PrepareSkill(Skill_System.SkillBehaviour __instance, out DescriptionState __state)
    {
        __state = new DescriptionState { Skill = __instance, Original = __instance.description };
        try { __instance.description = Plugin.Current.Translate(__state.Original, "skill-template"); }
        catch (Exception error) { Plugin.Current.Error($"Skill description adapter failed: {error.Message}"); }
    }
    private static void PrepareTrait(Traits.Trait __instance, out DescriptionState __state)
    {
        __state = new DescriptionState { Trait = __instance, Original = __instance.description };
        try { __instance.description = Plugin.Current.Translate(__state.Original, "description-template"); }
        catch (Exception error) { Plugin.Current.Error($"Trait description adapter failed: {error.Message}"); }
    }
    private static void RestoreDescription(DescriptionState? __state)
    {
        if (__state == null) return;
        try
        {
            __state.Restore?.Invoke();
            if (__state.Skill != null) __state.Skill.description = __state.Original;
            if (__state.Trait != null) __state.Trait.description = __state.Original;
        }
        catch (Exception error) { Plugin.Current.Error($"Description restoration failed: {error.Message}"); }
    }

    private static void PrepareCinder(Cinder_System.CinderModifier __instance, out DescriptionState __state)
    {
        __state = new DescriptionState();
        if (!IsNative<Cinder_System.CinderModifier>(__instance)) return;
        var original = __instance.description;
        __state = new DescriptionState { Restore = () => __instance.description = original };
        __instance.description = Plugin.Current.Translate(original, "description-template");
    }
    private static void PrepareObjective(World_Progression.WorldObjective __instance, out DescriptionState __state)
    {
        __state = new DescriptionState();
        if (!IsNative<World_Progression.WorldObjective>(__instance)) return;
        var main = __instance.mainDescription;
        var condition = __instance.conditionDescription;
        var effect = __instance.effectDescription;
        __state = new DescriptionState { Restore = () => { __instance.mainDescription = main; __instance.conditionDescription = condition; __instance.effectDescription = effect; } };
        __instance.mainDescription = Plugin.Current.Translate(main, "description-template");
        __instance.conditionDescription = Plugin.Current.Translate(condition, "description-template");
        __instance.effectDescription = Plugin.Current.Translate(effect, "description-template");
    }

    private static void PrepareDescription(ref string __0)
    {
        try { __0 = Plugin.Current.Translate(__0, "description-template"); }
        catch (Exception error) { Plugin.Current.Error($"Description template adapter failed: {error.Message}"); }
    }

    private static void AfterObjectiveDescription(Il2CppSystem.Object __instance, ref string __result)
    {
        if (!IsNative<World_Progression.WorldObjective>(__instance) || string.IsNullOrEmpty(__result)) return;
        __result = Plugin.Current.Translate(__result, "world-objective-description");
        // These two headings are inserted by the getter, outside its translated
        // descriptive fields. Restrict replacement to this getter's complete lines.
        foreach (var heading in new[] { "Objective:", "Result:" })
            __result = __result.Replace("\n" + heading + "\n", "\n" + Plugin.Current.Translate(heading, "world-objective-heading") + "\n");
    }

    private static void PrepareWeapon(Weapons.Weapon __instance, out DescriptionState __state)
    {
        var scope = new DescriptionScope();
        __state = new DescriptionState { Restore = scope.Restore };
        scope.Effects(__instance.effects);
    }
    private static void PrepareEquipment(Combat.Equipment.Equipment __instance, out DescriptionState __state)
    {
        var scope = new DescriptionScope();
        __state = new DescriptionState { Restore = scope.Restore };
        scope.Text(() => __instance.description, value => __instance.description = value);
        scope.Effects(__instance.effects);
    }

    private static void PrepareMessage(DialogueSystemController __instance, ref string __0)
    {
        try
        {
            var body = __instance.text;
            Plugin.Current.DialogueBody = body?.Pointer ?? IntPtr.Zero;
            if (body != null) { Plugin.Current.FontFor(body); body.text = ""; }
            __0 = Plugin.Current.Translate(__0, "dialogue");
        }
        catch (Exception error) { Plugin.Current.Error($"Dialogue adapter failed: {error.Message}"); }
    }
    private static void PrepareChoices(DialogueSystemController __instance, ref string __0, ref Il2CppSystem.Collections.Generic.IEnumerable<Choice> __2)
    {
        PrepareMessage(__instance, ref __0);
        if (__2 == null) return;
        try
        {
            var translated = new Il2CppSystem.Collections.Generic.List<Choice>();
            // Let IL2CPP copy its enumerable natively. Crossing a boxed List<T>
            // enumerator into managed interfaces can corrupt its version state.
            var snapshot = new Il2CppSystem.Collections.Generic.List<Choice>(__2);
            for (var index = 0; index < snapshot.Count; index++)
            {
                var choice = snapshot[index];
                translated.Add(new Choice(Plugin.Current.Translate(choice.Text, "dialogue"), choice.Callback, choice.Tooltips, choice.IsCancelChoice));
            }
            __2 = translated.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Choice>>();
        }
        catch (Exception error) { Plugin.Current.Error($"Choice translation failed; original choices retained: {error.Message}"); }
    }

    private static bool Owned(TMP_Text body) => body.Pointer == Plugin.Current.DialogueBody && body.Pointer != IntPtr.Zero;
    private static string Context(TMP_Text body)
    {
        var parts = new System.Collections.Generic.List<string>();
        var node = body.transform;
        for (var depth = 0; node != null && depth < 12; depth++, node = node.parent) parts.Add(node.name.Replace("(Clone)", ""));
        parts.Reverse();
        return "ui:" + string.Join("/", parts);
    }
    private static void AssignText(TMP_Text __instance, ref string __0)
    {
        if (_writing || __instance == null || Owned(__instance)) return;
        try
        {
            Plugin.Current.FontFor(__instance);
            __0 = Plugin.Current.Translate(__0, Context(__instance));
        }
        catch (Exception error) { Plugin.Current.Error($"UI assignment adapter failed: {error.Message}"); }
    }
    private static void AfterText(TMP_Text __instance)
    {
        if (_writing || __instance == null || Owned(__instance)) return;
        try
        {
            Plugin.Current.FontFor(__instance);
            var source = __instance.text;
            var target = Plugin.Current.Translate(source, Context(__instance));
            if (target == source) return;
            _writing = true;
            __instance.text = target;
        }
        catch (Exception error) { Plugin.Current.Error($"Formatted UI adapter failed: {error.Message}"); }
        finally { _writing = false; }
    }
    private static void AfterStyling(ref string __result)
    {
        try { __result = Plugin.Current.Translate(__result, "styled-ui"); }
        catch (Exception error) { Plugin.Current.Error($"Styled UI adapter failed: {error.Message}"); }
    }
    private static void BeforeStyling(ref string __0)
    {
        try
        {
            var template = Plugin.Current.Translate(__0, "description-template", false);
            // Composed weapon cards translate after native semantic coloring.
            // Translating enum labels here would prevent the game coloring them.
            __0 = template != __0 ? template : Plugin.Current.Translate(__0, "styled-input");
        }
        catch (Exception error) { Plugin.Current.Error($"Styled source adapter failed: {error.Message}"); }
    }
    private static void AfterMetaTitle(Meta_Perk_Tree.System.MetaPerk __instance, ref string __result)
    {
        if (!IsNative<Meta_Perk_Tree.System.MetaPerk>(__instance)) return;
        try
        {
            const string suffix = " (Meta Perk)";
            var name = __instance.name;
            if (name == null || !name.EndsWith(suffix, StringComparison.Ordinal)) return;
            var source = name.Substring(0, name.Length - suffix.Length);
            var target = Plugin.Current.Translate(source, "meta-title");
            if (target != source) __result = target;
        }
        catch (Exception error) { Plugin.Current.Error($"Mastery title adapter failed: {error.Message}"); }
    }
    private static void TranslateTitle(string source, string context, ref string target)
    {
        var translated = Plugin.Current.Translate(source, context);
        if (translated != source) target = translated;
    }
    private static void AfterClassName(Player.PlayerClass __instance, ref string __result) { if (IsNative<Player.PlayerClass>(__instance)) TranslateTitle(__instance.name, "class-name", ref __result); }
}
