using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;

namespace TinyRogues.Chinese;

internal static class CollectiblesModuleAudit
{
    internal static void Run()
    {
        var records = new List<string>();
        var failures = 0;
        var catalog = Plugin.Current.Catalog;
        var display = new GameObject("Collectibles module diagnostic");
        var label = display.AddComponent<TextMeshProUGUI>();
        void Check(string family, string name, string field, Func<string> getter)
        {
            string initialSource = "", source = "", reference = "", prepared = "", target = "", error = "";
            try
            {
                try
                {
                    Plugin.Current.Catalog = null;
                    initialSource = getter() ?? "";
                    source = getter() ?? "";
                    reference = UI.Text.TextMeshProxy.ApplyColorToText(source, true, label);
                }
                finally { Plugin.Current.Catalog = catalog; }
                getter();
                prepared = getter() ?? "";
                label.text = UI.Text.TextMeshProxy.ApplyColorToText(prepared, true, label);
                target = label.text;
                // Compare authoritative raw values after expanding numeric
                // wrappers, so +[[1]] and the rendered +1 remain equivalent.
                // Native rainbow rendering itself can expose color-tag digits.
                var before = Regex.Replace(source, "<[^>]*>", "").Replace("[[", "").Replace("]]", "");
                var after = Regex.Replace(target, "<[^>]*>", "");
                var numeric = new Regex(@"[+\-]?\d+(?:[.,]\d+)?%?");
                if (!numeric.Matches(before).Cast<Match>().Select(m => m.Value).OrderBy(x => x).SequenceEqual(numeric.Matches(after).Cast<Match>().Select(m => m.Value).OrderBy(x => x))) error = "numeric-values-changed";
                if (!Regex.Matches(reference, @"<sprite\b[^>]*>").Cast<Match>().Select(m => m.Value).OrderBy(x => x)
                    .SequenceEqual(Regex.Matches(target, @"<sprite\b[^>]*>").Cast<Match>().Select(m => m.Value).OrderBy(x => x))) error += " sprite-icons-changed";
                var multipliers = Regex.Matches(before, @"(?<![A-Za-z])x\d+(?:\.\d+)?").Cast<Match>().Select(m => m.Value).ToArray();
                after = Regex.Replace(after, @"(?<![A-Za-z])x\d+(?:\.\d+)?", m => multipliers.Contains(m.Value) ? "" : m.Value);
                var modelNumbers = Regex.Matches(before, @"\bV\d+\b").Cast<Match>().Select(m => m.Value).ToArray();
                after = Regex.Replace(after, @"\bV\d+\b", m => modelNumbers.Contains(m.Value) ? "" : m.Value);
                if (Regex.IsMatch(after, "[A-Za-z]")) error += " remaining-English";
            }
            catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
            if (error.Length > 0) failures++;
            records.Add("{\"family\":" + Core.PackJson.Quote(family) + ",\"name\":" + Core.PackJson.Quote(name)
                + ",\"field\":" + Core.PackJson.Quote(field) + ",\"initialSource\":" + Core.PackJson.Quote(initialSource) + ",\"source\":" + Core.PackJson.Quote(source)
                + ",\"reference\":" + Core.PackJson.Quote(reference) + ",\"prepared\":" + Core.PackJson.Quote(prepared) + ",\"target\":" + Core.PackJson.Quote(target)
                + ",\"error\":" + Core.PackJson.Quote(error) + "}");
        }
        try
        {
            var consumables = Resources.LoadAll<Items.Consumables.Consumable>("");
            var gifts = Resources.LoadAll<Gifts.Gift>("");
            var collections = Resources.LoadAll<Player.Collection_Items.CollectionItem>("");
            if (consumables.Length != 174 || gifts.Length != 16 || collections.Length != 35)
                throw new InvalidOperationException($"Collectible source inventory mismatch: {consumables.Length}/{gifts.Length}/{collections.Length}; expected 174/16/35 for 0.2.8.6.");
            foreach (var item in consumables)
            {
                Check("consumable", item.name, "Name", () => item.Name);
                foreach (var details in new[] { false, true })
                foreach (var baseValues in new[] { false, true })
                    Check("consumable", item.name, "Description details=" + details + " base=" + baseValues, () => item.Description(details, baseValues, 0));
            }
            foreach (var gift in gifts)
            {
                Check("gift", gift.name, "Name", () => gift.Name);
                Check("gift", gift.name, "Description", () => gift.Description);
                Check("gift", gift.name, "Tier", () => gift.GetTier);
            }
            foreach (var item in collections)
            {
                Check("collection", item.name, "Title", () => item.title);
                Check("collection", item.name, "Description", () => item.description);
                if (item.IsUnlocked != null) Check("collection", item.name, "UnlockProgress", () => item.IsUnlocked.Progress);
            }
            Resources.LoadAll<GameObject>("");
            var companions = Resources.FindObjectsOfTypeAll<Companions.Companion>().Where(item => !item.gameObject.scene.IsValid()).ToArray();
            if (companions.Length != 86)
            {
                failures++;
                records.Add("{\"family\":\"inventory\",\"name\":\"companions\",\"field\":\"count\",\"source\":\"86 raw disk objects\",\"prepared\":\"\",\"target\":\"" + companions.Length + "\",\"error\":\"inventory-count-review\"}");
            }
            foreach (var companion in companions)
            {
                Check("companion", companion.name, "Name", () => companion.CompanionNameWithColor);
                foreach (var details in new[] { false, true })
                foreach (var baseValues in new[] { false, true })
                    Check("companion", companion.name, "Description details=" + details + " base=" + baseValues, () => companion.GetDescription(details, baseValues));
            }
            Plugin.Current.Info($"COLLECTIBLES MODULE AUDIT: {consumables.Length} consumables, {gifts.Length} gifts, {collections.Length} collection items; {records.Count} fields; {failures} unresolved.");
        }
        finally
        {
            Plugin.Current.Catalog = catalog;
            UnityEngine.Object.Destroy(display);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "collectibles-module-audit.json"), "[" + string.Join(",", records) + "]");
        }
        if (failures > 0) throw new InvalidOperationException($"Collectibles module has {failures} unresolved fields; see collectibles-module-audit.json.");
    }
}
