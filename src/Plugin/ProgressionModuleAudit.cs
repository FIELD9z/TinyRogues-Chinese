using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace TinyRogues.Chinese;

// Diagnostic only: enumerate resources before consulting the catalog. A missing
// translation must remain a failing row, rather than disappearing from the test.
internal static class ProgressionModuleAudit
{
    internal static void Run()
    {
        var records = new List<string>();
        var errors = 0;
        var catalog = Plugin.Current.Catalog;
        var display = new GameObject("Progression module diagnostic");
        var label = display.AddComponent<TextMeshProUGUI>();
        void Check(string type, string name, string field, Func<string> getter)
        {
            string source = "", reference = "", prepared = "", target = "", error = "";
            try
            {
                try { Plugin.Current.Catalog = null; source = getter() ?? ""; reference = UI.Text.TextMeshProxy.ApplyColorToText(source, true, label); }
                finally { Plugin.Current.Catalog = catalog; }
                prepared = getter() ?? "";
                label.text = UI.Text.TextMeshProxy.ApplyColorToText(prepared, true, label);
                target = label.text;
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) error = "unexpected-empty-field";
                var sourceIcons = Regex.Matches(reference, @"<sprite\b[^>]*>").Cast<Match>().Select(m => m.Value).OrderBy(x => x);
                var targetIcons = Regex.Matches(target, @"<sprite\b[^>]*>").Cast<Match>().Select(m => m.Value).OrderBy(x => x);
                if (!sourceIcons.SequenceEqual(targetIcons)) error += " sprite-icons-changed";
                var before = Regex.Replace(source, "<[^>]*>", "");
                var after = Regex.Replace(target, "<[^>]*>", "");
                var numbers = new Regex(@"[+\-]?\d+(?:[.,]\d+)?%?");
                if (!numbers.Matches(before).Cast<Match>().Select(m => m.Value).OrderBy(x => x)
                    .SequenceEqual(numbers.Matches(after).Cast<Match>().Select(m => m.Value).OrderBy(x => x)))
                    error = "numeric-values-changed";
                var multipliers = Regex.Matches(before, @"(?<![A-Za-z])x\d+(?:\.\d+)?").Cast<Match>().Select(m => m.Value).ToArray();
                after = Regex.Replace(after, @"(?<![A-Za-z])x\d+(?:\.\d+)?", m => multipliers.Contains(m.Value) ? "" : m.Value);
                if (Regex.IsMatch(after, "[A-Za-z]")) error += " remaining-English";
                if (Regex.IsMatch(before, "[A-Za-z]") && !Regex.IsMatch(after, "[\u4e00-\u9fff]")) error += " no-Chinese";
            }
            catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
            if (error.Length != 0) errors++;
            records.Add("{\"type\":" + Core.PackJson.Quote(type) + ",\"name\":" + Core.PackJson.Quote(name)
                + ",\"field\":" + Core.PackJson.Quote(field) + ",\"source\":" + Core.PackJson.Quote(source)
                + ",\"prepared\":" + Core.PackJson.Quote(prepared) + ",\"target\":" + Core.PackJson.Quote(target) + ",\"error\":" + Core.PackJson.Quote(error) + "}");
        }
        try
        {
            var perks = Resources.LoadAll<Meta_Perk_Tree.System.MetaPerk>("");
            var classes = Resources.LoadAll<Player.PlayerClass>("");
            var cinders = Resources.LoadAll<Cinder_System.CinderModifier>("");
            Resources.LoadAll<World_Progression.WorldObjective>("");
            var objectives = Resources.FindObjectsOfTypeAll<World_Progression.WorldObjective>();
            // Independent 0.2.8.6 disk-resource inventory, not counts derived from
            // the subset which happens to have a catalog match.
            if (perks.Length != 108 || classes.Length != 35 || cinders.Length != 16 || objectives.Length != 9)
                throw new InvalidOperationException($"Progression source inventory mismatch: {perks.Length}/{classes.Length}/{cinders.Length}/{objectives.Length}; expected 108/35/16/9 for 0.2.8.6.");
            foreach (var perk in perks)
            {
                Check("mastery", perk.name, "Title", () => perk.Title);
                Check("mastery", perk.name, "Description", () => perk.Description);
            }
            foreach (var playerClass in classes)
            {
                Check("class", playerClass.name, "Name", () => playerClass.Name);
                Check("class", playerClass.name, "Description", () => playerClass.Description);
                if (playerClass.classAbility == null) { Check("class-ability", playerClass.name, "Missing resource", () => throw new InvalidOperationException("Class ability resource missing")); continue; }
                var ability = playerClass.classAbility;
                Check("class-ability", ability.name, "Title", () => ability.Title);
                Check("class-ability", ability.name, "Description(false,true)", () => ability.Description(false, true));
                Check("class-ability", ability.name, "Description(true,true)", () => ability.Description(true, true));
                var pirate = ability.TryCast<Player.Player_Class_Ability.PirateBountyClassAbility>();
                if (pirate != null)
                    foreach (var rank in pirate.pirateEffects)
                        Check("pirate-rank", ability.name, "Title@" + rank.BountyThreshold, () => rank.Title);
            }
            foreach (var cinder in cinders)
            {
                Check("cinder", cinder.name, "Title", () => cinder.Title);
                Check("cinder", cinder.name, "Description", () => cinder.Description);
                Check("cinder", cinder.name, "UnlockCondition", () => cinder.GetUnlockConditionDescription());
            }
            foreach (var objective in objectives)
            {
                Check("world-objective", objective.name, "Title", () => objective.Title);
                Check("world-objective", objective.name, "Description", () => objective.Description);
                Check("world-objective", objective.name, "UnlockMessage", () => objective.UnlockMessage);
            }
            Plugin.Current.Info($"PROGRESSION MODULE INVENTORY: {perks.Length} mastery, {classes.Length} classes, {cinders.Length} cinders, {objectives.Length} objectives; {records.Count} fields; {errors} unresolved.");
        }
        finally
        {
            Plugin.Current.Catalog = catalog;
            UnityEngine.Object.Destroy(display);
            var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "progression-module-audit.json");
            File.WriteAllText(path, "[" + string.Join(",", records) + "]");
        }
        if (errors > 0) throw new InvalidOperationException($"Progression module has {errors} unresolved fields; see progression-module-audit.json.");
    }
}
