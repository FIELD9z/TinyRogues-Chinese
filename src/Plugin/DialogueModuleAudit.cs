using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using UI.Dialogue_System;
using UnityEngine;

namespace TinyRogues.Chinese;

// Complete Dialogue_EN source inventory, passed through the real message
// entrance. Branch gameplay is separate; no NPC callbacks run in this audit.
internal static class DialogueModuleAudit
{
    [DataContract]
    private sealed class Row
    {
        [DataMember(Name = "key")] public string Key = "";
        [DataMember(Name = "source")] public string Source = "";
        [DataMember(Name = "allowedLatin")] public string[] AllowedLatin = Array.Empty<string>();
    }
    internal static void Run(DialogueSystemController controller)
    {
        var directory = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics");
        using var stream = File.OpenRead(Path.Combine(directory, "dialogue-module.json"));
        var rows = (Row[])new DataContractJsonSerializer(typeof(Row[])).ReadObject(stream)!;
        if (rows.Length != 1075) throw new InvalidDataException("Dialogue source inventory must have all 1075 independent fields.");
        var catalog = Plugin.Current.Catalog;
        var records = new List<string>();
        var failures = 0;
        try
        {
            foreach (var row in rows)
            {
                string reference = "", target = "", error = "";
                try
                {
                    try
                    {
                        Plugin.Current.Catalog = null;
                        controller.ShowMessage(row.Source, Vector2.zero, 20f, null, null, false);
                        reference = controller.currentMessage;
                        controller.HideMessage();
                    }
                    finally { Plugin.Current.Catalog = catalog; }
                    controller.ShowMessage(row.Source, Vector2.zero, 20f, null, null, false);
                    target = controller.currentMessage;
                    controller.HideMessage();
                    if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(target)) error = "empty-native-message";
                    var plain = Regex.Replace(target, "<[^>]*>", "");
                    var originalPlain = Regex.Replace(reference, "<[^>]*>", "").Replace("[[", "").Replace("]]", "");
                    var beforeNumbers = Regex.Matches(originalPlain, @"[+\-]?\d+(?:[.,]\d+)?%?").Cast<Match>().Select(m => m.Value).OrderBy(x => x);
                    var afterNumbers = Regex.Matches(plain, @"[+\-]?\d+(?:[.,]\d+)?%?").Cast<Match>().Select(m => m.Value).OrderBy(x => x);
                    if (!beforeNumbers.SequenceEqual(afterNumbers)) error += " numbers-changed";
                    if (!Regex.Matches(reference, @"<sprite\b[^>]*>").Cast<Match>().Select(m => m.Value).OrderBy(x => x)
                        .SequenceEqual(Regex.Matches(target, @"<sprite\b[^>]*>").Cast<Match>().Select(m => m.Value).OrderBy(x => x))) error += " sprites-changed";
                    var multipliers = Regex.Matches(originalPlain, @"\bx\d+\b").Cast<Match>().Select(m => m.Value).ToArray();
                    plain = Regex.Replace(plain, @"\bx\d+\b", m => multipliers.Contains(m.Value) ? "" : m.Value);
                    var latin = Regex.Matches(plain, "[A-Za-z]+").Cast<Match>().Select(m => m.Value).ToArray();
                    if (latin.Any(word => !row.AllowedLatin.Contains(word))) error += " remaining-English";
                    if (Regex.IsMatch(row.Source, "[A-Za-z]") && !Regex.IsMatch(plain, "[\u4e00-\u9fff]")) error += " no-Chinese";
                }
                catch (Exception exception) { error += " " + exception.GetType().Name + ": " + exception.Message; }
                if (error.Length != 0) failures++;
                records.Add("{\"key\":" + Core.PackJson.Quote(row.Key) + ",\"source\":" + Core.PackJson.Quote(row.Source)
                    + ",\"nativeSource\":" + Core.PackJson.Quote(reference) + ",\"target\":" + Core.PackJson.Quote(target) + ",\"error\":" + Core.PackJson.Quote(error) + "}");
            }
        }
        finally
        {
            Plugin.Current.Catalog = catalog;
            controller.HideMessage();
            File.WriteAllText(Path.Combine(directory, "dialogue-module-audit.json"), "[" + string.Join(",", records) + "]");
        }
        Plugin.Current.Info($"DIALOGUE MODULE AUDIT: {rows.Length} complete source fields through native ShowMessage; {failures} unresolved.");
        if (failures != 0) throw new InvalidOperationException("Dialogue native source corpus has unresolved fields; see dialogue-module-audit.json.");
    }
}
