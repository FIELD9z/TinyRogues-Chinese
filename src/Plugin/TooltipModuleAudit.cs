using System;
using System.Collections.Generic;
using System.IO;
using UI.Text.Tag_Boxes;
using UI.Text.Tag_Boxes.Tag_Box_Definitions;
using UnityEngine;

namespace TinyRogues.Chinese;

// Source discovery for tooltip definitions whose prose lives in native code.
// Some definitions require an active player; report that limitation explicitly.
internal static class TooltipModuleAudit
{
    internal static void Run()
    {
        var rows = new List<string>();
        var label = UI.Title_Screen.SplashTextHandler.Instance.textMeshProUGUI;
        foreach (var definition in Resources.LoadAll<TooltipDefinition>(""))
        foreach (var detail in new[] { false, true })
        foreach (var baseValue in new[] { false, true })
        {
            string source = "", target = "", error = "";
            try
            {
                source = definition.Cast<ITooltip>().GetTooltipText.Invoke(detail, baseValue) ?? "";
                target = UI.Text.TextMeshProxy.ApplyColorToText(source, true, label);
            }
            catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
            rows.Add("{\"name\":" + Core.PackJson.Quote(definition.name) + ",\"details\":" + detail.ToString().ToLowerInvariant()
                + ",\"baseValues\":" + baseValue.ToString().ToLowerInvariant() + ",\"source\":" + Core.PackJson.Quote(source)
                + ",\"target\":" + Core.PackJson.Quote(target) + ",\"error\":" + Core.PackJson.Quote(error) + "}");
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "tooltip-definition-inventory.json"), "[" + string.Join(",", rows) + "]");
        Plugin.Current.Info($"TOOLTIP DEFINITION INVENTORY: {rows.Count} native variants recorded; this discovery inventory is not a coverage assertion.");
    }
}
