using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace TinyRogues.Chinese;

// Enumerates source status resources with all description options, then uses
// transient clones to exercise stored instance counts, timers and multipliers.
// Original resources are not mutated and no actor lifecycle/save is invoked.
internal static class StatusEffectsModuleAudit
{
    // Independent disk extraction count: 207 distinct (source_file, path_id)
    // pairs in work/modules-source-index/skills-stats-effects.json whose script
    // context is Combat.Status_Effects.*. This is not computed from Catalog hits.
    private const int ExpectedDiskStatusResourceCount = 207;

    private static readonly Regex SpriteTags = new(@"<sprite\b[^>]*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AllTags = new(@"<[^>]*>", RegexOptions.CultureInvariant);
    private static readonly Regex NumberTokens = new(@"[+\-−]?\d+(?:[.,]\d+)?%?", RegexOptions.CultureInvariant);
    private static readonly Regex NativeTokens = new(@"\[\[[^\]]+\]\]|##[A-Za-z]+\b|//[A-Za-z]+\b|\((?:\*|[+\-#x0-9%.,]+|[A-Za-z_][A-Za-z_0-9]*)\)", RegexOptions.CultureInvariant);
    private static readonly Regex Latin = new(@"[A-Za-z]", RegexOptions.CultureInvariant);

    internal static void Run()
    {
        var records = new List<string>();
        var errors = 0;
        var latinReviewRows = 0;
        var catalog = Plugin.Current.Catalog;
        var display = new GameObject("Status effects module diagnostic");
        var label = display.AddComponent<TextMeshProUGUI>();

        void AddInventoryRecord(int actual)
        {
            var error = actual == ExpectedDiskStatusResourceCount
                ? ""
                : $"resource-inventory-mismatch:disk-index={ExpectedDiskStatusResourceCount},runtime={actual}";
            if (error.Length != 0) errors++;
            records.Add("{\"type\":\"inventory\",\"name\":\"Resources.LoadAll<StatusEffect>\",\"field\":\"count\",\"diskExpected\":"
                + ExpectedDiskStatusResourceCount + ",\"runtimeCount\":" + actual + ",\"error\":" + Core.PackJson.Quote(error) + "}");
        }

        void Check(string type, string name, string method, string arguments, Func<string> getter)
        {
            string source = "", nativeStyled = "", prepared = "", target = "", error = "";
            bool numbersChanged = false, spritesChanged = false, targetHasLatin = false, targetHasCjk = false;
            try
            {
                try
                {
                    Plugin.Current.Catalog = null;
                    source = getter() ?? "";
                    nativeStyled = UI.Text.TextMeshProxy.ApplyColorToText(source, true, label) ?? "";
                }
                finally { Plugin.Current.Catalog = catalog; }

                prepared = getter() ?? "";
                label.text = UI.Text.TextMeshProxy.ApplyColorToText(prepared, true, label) ?? "";
                target = label.text ?? "";

                var sourceSprites = SpriteTags.Matches(nativeStyled).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
                var targetSprites = SpriteTags.Matches(target).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
                spritesChanged = !sourceSprites.SequenceEqual(targetSprites);
                if (spritesChanged) error = Append(error, "sprite-tags-changed");

                var sourcePlain = AllTags.Replace(nativeStyled, "");
                var targetPlain = AllTags.Replace(target, "");
                var sourceNumbers = NumberTokens.Matches(sourcePlain).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
                var targetNumbers = NumberTokens.Matches(targetPlain).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
                numbersChanged = !sourceNumbers.SequenceEqual(targetNumbers);
                if (numbersChanged) error = Append(error, "numeric-values-changed");

                var multipliers = Regex.Matches(sourcePlain, @"(?<![A-Za-z])x\d+(?:\.\d+)?").Cast<Match>().Select(m => m.Value).ToArray();
                var reviewedPlain = Regex.Replace(targetPlain, @"(?<![A-Za-z])x\d+(?:\.\d+)?", m => multipliers.Contains(m.Value) ? "" : m.Value);
                var sourceGrades = Regex.Matches(sourcePlain, @"(?m)Scaling:\s*([SABCDEF][+-]?\s*\|\s*[SABCDEF][+-]?\s*\|\s*[SABCDEF][+-]?)").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
                reviewedPlain = Regex.Replace(reviewedPlain, @"属性补正：\s*([SABCDEF][+-]?\s*\|\s*[SABCDEF][+-]?\s*\|\s*[SABCDEF][+-]?)", m => sourceGrades.Contains(m.Groups[1].Value) ? "属性补正：" : m.Value);
                if (sourcePlain.Contains("DNA", StringComparison.Ordinal)) reviewedPlain = reviewedPlain.Replace("DNA", "");
                targetHasLatin = Latin.IsMatch(reviewedPlain);
                targetHasCjk = Regex.IsMatch(targetPlain, "[\\u4e00-\\u9fff]", RegexOptions.CultureInvariant);
                if (targetHasLatin)
                {
                    latinReviewRows++;
                    error = Append(error, "remaining-latin-review");
                }
                if (source.Length == 0 || nativeStyled.Length == 0 || prepared.Length == 0 || target.Length == 0)
                    error = Append(error, "unexpected-empty-output");
            }
            catch (Exception exception)
            {
                Plugin.Current.Catalog = catalog;
                var cause = exception.GetBaseException();
                error = Append(error, cause.GetType().Name + ": " + cause.Message + "\n" + cause.StackTrace);
            }

            if (error.Contains("remaining-latin-review", StringComparison.Ordinal)
                || error.Contains("sprite-tags-changed", StringComparison.Ordinal)
                || error.Contains("numeric-values-changed", StringComparison.Ordinal)
                || error.Contains("unexpected-empty-output", StringComparison.Ordinal)
                || error.Contains("Exception", StringComparison.Ordinal)) errors++;

            records.Add("{\"type\":" + Core.PackJson.Quote(type)
                + ",\"name\":" + Core.PackJson.Quote(name)
                + ",\"method\":" + Core.PackJson.Quote(method)
                + ",\"arguments\":" + Core.PackJson.Quote(arguments)
                + ",\"source\":" + Core.PackJson.Quote(source)
                + ",\"nativeStyled\":" + Core.PackJson.Quote(nativeStyled)
                + ",\"prepared\":" + Core.PackJson.Quote(prepared)
                + ",\"target\":" + Core.PackJson.Quote(target)
                + ",\"numbersChanged\":" + (numbersChanged ? "true" : "false")
                + ",\"spriteTagsChanged\":" + (spritesChanged ? "true" : "false")
                + ",\"targetHasLatin\":" + (targetHasLatin ? "true" : "false")
                + ",\"targetHasCjk\":" + (targetHasCjk ? "true" : "false")
                + ",\"error\":" + Core.PackJson.Quote(error) + "}");
        }

        try
        {
            // Enumerate independently of translation outcomes. The baseline is
            // from disk object path IDs, not a Catalog-filtered candidate set.
            Resources.LoadAll<Combat.Status_Effects.Abstract_Status_Effects.StatusEffect>("");
            var statusEffects = Resources.FindObjectsOfTypeAll<Combat.Status_Effects.Abstract_Status_Effects.StatusEffect>();
            AddInventoryRecord(statusEffects.Length);
            records.Add("{\"type\":\"scope-boundary\",\"field\":\"runtime stacks/timers/infliction\",\"status\":\"transient-clone-fixtures\",\"reason\":\"Resource clones with 2 and 5 stored instances; original assets remain untouched. Actor lifecycle and live icon positioning are not simulated.\"}");

            foreach (var effect in statusEffects.OrderBy(item => item.name, StringComparer.Ordinal))
            {
                Check("status-title", effect.name, "DisplayName", "", () => effect.DisplayName);
                var type = effect.GetIl2CppType()?.FullName ?? effect.GetType().FullName ?? "unknown";
                var runtimeType = effect.GetType();
                var active = FindDescriptionMethod(runtimeType, "ActiveDescription", 2);
                var informative = FindDescriptionMethod(runtimeType, "InformativeDescription", 3);

                if (active == null)
                {
                    Check(type, effect.name, "ActiveDescription(bool,bool)", "unavailable", () => throw new MissingMethodException(runtimeType.FullName, "ActiveDescription(bool,bool)"));
                }
                else
                {
                    foreach (var showDetails in new[] { false, true })
                    foreach (var showBaseValues in new[] { false, true })
                    {
                        var details = showDetails;
                        var baseValues = showBaseValues;
                        var args = new object[] { details, baseValues };
                        Check(type, effect.name, "ActiveDescription(bool,bool)", $"showDetails={details};showBaseValues={baseValues}",
                            () => InvokeString(active, effect, args));
                    }
                }

                if (informative == null)
                {
                    Check(type, effect.name, "InformativeDescription(bool,bool,bool)", "unavailable", () => throw new MissingMethodException(runtimeType.FullName, "InformativeDescription(bool,bool,bool)"));
                }
                else
                {
                    foreach (var includeTitle in new[] { false, true })
                    foreach (var showDetails in new[] { false, true })
                    foreach (var showBaseValues in new[] { false, true })
                    {
                        var title = includeTitle;
                        var details = showDetails;
                        var baseValues = showBaseValues;
                        var args = new object[] { title, details, baseValues };
                        Check(type, effect.name, "InformativeDescription(bool,bool,bool)",
                            $"includeTitle={title};showDetails={details};showBaseValues={baseValues}",
                            () => InvokeString(informative, effect, args));
                    }
                }
            }

            foreach (var effect in statusEffects.OrderBy(item => item.name, StringComparer.Ordinal))
            {
                var clone = UnityEngine.Object.Instantiate(effect);
                try
                {
                    foreach (var stacks in new[] { 2, 5 })
                    {
                        clone.instances = new Il2CppSystem.Collections.Generic.List<Combat.Status_Effects.StatusEffectInstance>();
                        for (var index = 0; index < stacks; index++)
                        {
                            var instance = new Combat.Status_Effects.StatusEffectInstance(0.6f + index * 0.1f);
                            instance.timer = 1.25f + index;
                            instance.roomCounter = index + 1;
                            instance.floorCounter = 1;
                            instance.expired = false;
                            instance.timeOfApplication = Time.time - 1.25f;
                            clone.instances.Add(instance);
                        }
                        var active = FindDescriptionMethod(clone.GetType(), "ActiveDescription", 2)!;
                        foreach (var details in new[] { false, true })
                        foreach (var baseValues in new[] { false, true })
                        {
                            var args = new object[] { details, baseValues };
                            Check("stored-instance-fixture", effect.name, "ActiveDescription(bool,bool)", $"instances={stacks};showDetails={details};showBaseValues={baseValues}", () => InvokeString(active, clone, args));
                        }
                    }
                }
                finally { UnityEngine.Object.Destroy(clone); }
            }
            Plugin.Current.Info($"STATUS EFFECT MODULE AUDIT: {statusEffects.Length} disk-enumerated resources; {records.Count - 2} description rows; {errors} structural/value errors; {latinReviewRows} rows retain Latin for review.");
        }
        finally
        {
            Plugin.Current.Catalog = catalog;
            UnityEngine.Object.Destroy(display);
            var directory = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "status-effects-module-audit.json"), "[" + string.Join(",", records) + "]");
        }

        if (errors > 0)
            throw new InvalidOperationException($"Status effects module audit has {errors} structural/value errors; see status-effects-module-audit.json. Latin rows are separately marked for review.");
    }

    private static MethodInfo? FindDescriptionMethod(Type type, string name, int parameterCount)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var method = current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(candidate => candidate.Name == name
                    && candidate.GetParameters().Length == parameterCount
                    && candidate.GetParameters().All(parameter => parameter.ParameterType == typeof(bool))
                    && candidate.ReturnType == typeof(string));
            if (method != null) return method;
        }
        return null;
    }

    private static string InvokeString(MethodInfo method, object instance, object[] arguments)
    {
        return method.Invoke(instance, arguments) as string
            ?? throw new InvalidOperationException(method.DeclaringType?.FullName + "." + method.Name + " returned null/non-string.");
    }

    private static string Append(string current, string value) => current.Length == 0 ? value : current + " " + value;
}
