using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace TinyRogues.Chinese;

// Source-first diagnostic. Empty fields and intermediate fragments remain in
// the output; a catalog miss never removes a resource from this inventory.
internal static class SkillsStatsModuleAudit
{
    [System.Runtime.Serialization.DataContract]
    private sealed class IntermediateFragment
    {
        [System.Runtime.Serialization.DataMember(Name = "name")] public string Name = "";
        [System.Runtime.Serialization.DataMember(Name = "template")] public string Template = "";
        [System.Runtime.Serialization.DataMember(Name = "owner")] public string Owner = "";
    }
    internal static void Run()
    {
        var catalog = Plugin.Current.Catalog;
        var records = new List<string>();
        var failures = 0;
        var latin = 0;
        var display = new GameObject("Skill and stat source diagnostic");
        var label = display.AddComponent<TextMeshProUGUI>();
        var directory = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics");
        IntermediateFragment[] fragments;
        using (var stream = File.OpenRead(Path.Combine(directory, "stat-intermediate-fragments.json")))
            fragments = (IntermediateFragment[])new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(IntermediateFragment[])).ReadObject(stream)!;
        var ownerResults = new Dictionary<string, bool>(StringComparer.Ordinal);
        string Plain(string text) => Regex.Replace(text ?? "", "<[^>]*>", "");
        string[] Numbers(string text) => Regex.Matches(Plain(text), @"[+\-−]?\d+(?:[.,]\d+)?%?").Cast<Match>().Select(m => m.Value).OrderBy(x => x).ToArray();
        string[] Icons(string text) => Regex.Matches(text ?? "", @"<sprite\b[^>]*>").Cast<Match>().Select(m => m.Value).OrderBy(x => x).ToArray();
        void Capture(string family, string name, string type, string template, string variant, Func<string> getter, Func<string> readTemplate)
        {
            string source = "", reference = "", prepared = "", target = "", error = "";
            try
            {
                try { Plugin.Current.Catalog = null; source = getter() ?? ""; reference = UI.Text.TextMeshProxy.ApplyColorToText(source, true, label); }
                finally { Plugin.Current.Catalog = catalog; }
                prepared = getter() ?? "";
                label.text = UI.Text.TextMeshProxy.ApplyColorToText(prepared, true, label);
                target = label.text ?? "";
                if (readTemplate() != template) error += " source-field-not-restored";
                if (!Numbers(reference).SequenceEqual(Numbers(target))) error += " numeric-values-changed";
                if (!Icons(reference).SequenceEqual(Icons(target))) error += " sprite-icons-changed";
                if (source.Length != 0 && target.Length == 0) error += " unexpected-empty-target";
            }
            catch (Exception exception) { error += " " + exception.GetType().Name + ": " + exception.Message; }
            var remaining = Plain(target);
            var originalMultipliers = Regex.Matches(Plain(source), @"(?<![A-Za-z])x-?\d+(?:\.\d+)?").Cast<Match>().Select(m => m.Value).ToArray();
            remaining = Regex.Replace(remaining, @"(?<![A-Za-z])x-?\d+(?:\.\d+)?", m => originalMultipliers.Contains(m.Value) ? "" : m.Value);
            // FillInDescriptionValues is an intermediate formatter; its caller
            // expands layout markers after combining the stat fields.
            if (family == "stat" && template.Contains("(br)")) remaining = remaining.Replace("(br)", "");
            if (family == "skill") remaining = Regex.Replace(remaining, @"(?m)^•伤害加成：\s*[SABCD](?:\s*\|\s*[SABCD]){2}\s*$", "");
            var hasLatin = Regex.IsMatch(remaining, "[A-Za-z]");
            if (hasLatin) latin++;
            var owner = "";
            if (hasLatin && family == "stat")
            {
                var fragment = fragments.SingleOrDefault(item => item.Name == name && item.Template == template);
                if (fragment != null && ownerResults.TryGetValue(fragment.Owner, out var passed) && passed) owner = fragment.Owner;
            }
            if (hasLatin && owner.Length == 0) error += " unclassified-English";
            if (error.Length != 0) failures++;
            if (family != "stat")
            {
                var key = family.Replace("owner-", "") + ":" + name;
                ownerResults[key] = error.Length == 0 && (!ownerResults.TryGetValue(key, out var previous) || previous);
            }
            string Q(string value) => Core.PackJson.Quote(value ?? "");
            records.Add("{\"family\":" + Q(family) + ",\"name\":" + Q(name) + ",\"type\":" + Q(type)
                + ",\"template\":" + Q(template) + ",\"variant\":" + Q(variant) + ",\"source\":" + Q(source)
                + ",\"reference\":" + Q(reference) + ",\"prepared\":" + Q(prepared) + ",\"target\":" + Q(target)
                + ",\"latinReview\":" + (hasLatin ? "true" : "false") + ",\"completeOwner\":" + Q(owner) + ",\"error\":" + Q(error) + "}");
        }
        try
        {
            Resources.LoadAll<Skill_System.SkillBehaviour>("");
            Resources.LoadAll<Stats.SimpleStat>("");
            var skills = Resources.FindObjectsOfTypeAll<Skill_System.SkillBehaviour>();
            var stats = Resources.FindObjectsOfTypeAll<Stats.SimpleStat>();
            if (skills.Length != 34 || stats.Length != 757)
                throw new InvalidOperationException($"Skill/stat resource count differs from the independent 0.2.8.6 source inventory: {skills.Length}/{stats.Length}.");
            var forms = Resources.LoadAll<Player.Player_Class_Ability.DoppelgangerForm>("");
            var sets = Resources.LoadAll<Combat.Equipment.SetBonus>("");
            foreach (var form in forms)
            {
                var raw = form.description ?? "";
                foreach (var details in new[] { false, true })
                    foreach (var basis in new[] { false, true })
                        Capture("form", form.name, form.GetIl2CppType().FullName, raw, $"details={details};base={basis}",
                            () => form.GetFormDescription(null, details, basis), () => form.description ?? "");
            }
            foreach (var set in sets)
            {
                var raw = set.description ?? "";
                foreach (var details in new[] { false, true })
                    foreach (var basis in new[] { false, true })
                        Capture("set", set.name, set.GetIl2CppType().FullName, raw, $"details={details};base={basis}",
                            () => set.DescriptionAddon(set.components[0], details, basis), () => set.description ?? "");
            }
            foreach (var skill in skills)
            {
                var raw = skill.description ?? "";
                // Interop base wrappers do not dispatch to the derived CLR
                // wrapper. Invoke the concrete resource's native override.
                var nativeType = typeof(Skill_System.SkillBehaviour).Assembly.GetType(skill.GetIl2CppType().FullName)
                    ?? throw new TypeLoadException(skill.GetIl2CppType().FullName);
                var concrete = Activator.CreateInstance(nativeType, skill.Pointer)!;
                var method = nativeType.GetMethod("Description", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(bool), typeof(bool) }, null) ?? throw new MissingMethodException(nativeType.FullName, "Description");
                foreach (var details in new[] { false, true })
                    foreach (var basis in new[] { false, true })
                        Capture("skill", skill.name, skill.GetIl2CppType().FullName, raw, $"details={details};base={basis}",
                            () => (string)method.Invoke(concrete, new object[] { details, basis })!, () => skill.description ?? "");
            }
            var traits = Resources.FindObjectsOfTypeAll<Traits.Trait>();
            var equipment = Resources.FindObjectsOfTypeAll<Combat.Equipment.Equipment>();
            foreach (var owner in fragments.Select(item => item.Owner).Distinct())
            {
                if (owner.StartsWith("trait:", StringComparison.Ordinal))
                {
                    var trait = traits.Single(item => item.name == owner.Substring(6));
                    var raw = trait.description ?? "";
                    foreach (var details in new[] { false, true })
                        Capture("owner-trait", trait.name, trait.GetIl2CppType().FullName, raw, $"details={details}",
                            () => trait.Description(details, true), () => trait.description ?? "");
                }
                if (owner.StartsWith("equipment:", StringComparison.Ordinal))
                {
                    var item = equipment.Single(item => item.name == owner.Substring(10));
                    var raw = item.description ?? "";
                    foreach (var details in new[] { false, true })
                        Capture("owner-equipment", item.name, item.GetIl2CppType().FullName, raw, $"details={details}",
                            () => item.Description(details, false, true), () => item.description ?? "");
                }
            }
            foreach (var stat in stats)
            {
                var raw = stat.activeDescription ?? "";
                var wrappers = new Il2CppSystem.Collections.Generic.List<Stats.StatWrapper>();
                if (stat.StatWrapper != null) wrappers.Add(stat.StatWrapper);
                var values = wrappers.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Stats.StatWrapper>>();
                var context = new Il2CppSystem.Collections.Generic.HashSet<string>();
                foreach (var multiplier in new[] { 1f, 1.75f })
                    foreach (var basis in new[] { false, true })
                        Capture("stat", stat.name, stat.GetIl2CppType().FullName, raw, $"multiplier={multiplier};base={basis};wrappers={wrappers.Count}",
                            () => Stats.StatDescriptionUtility.FillInDescriptionValues(raw, values, multiplier, 1f, context, null, false, basis),
                            () => stat.activeDescription ?? "");
            }
            Plugin.Current.Info($"SKILLS/STATS SOURCE AUDIT: {skills.Length} skill resources; {stats.Length} stat resources; {forms.Length} forms; {sets.Length} sets; {records.Count} rows; {failures} errors; {latin} intermediate Latin rows checked against complete owners.");
        }
        finally
        {
            Plugin.Current.Catalog = catalog;
            UnityEngine.Object.Destroy(display);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "skills-stats-module-audit.json"), "[" + string.Join(",", records) + "]");
        }
        if (failures != 0) throw new InvalidOperationException($"Skills/stats source audit has {failures} structural errors.");
    }
}
