using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Runtime.Serialization.Json;
using TMPro;
using UnityEngine;
using UI.Text.World_Text_Boxes;
using UI.Text.Tag_Boxes;

internal static class RelatedScan
{
    internal static string Q(string value)
    {
        using var f=new MemoryStream();new DataContractJsonSerializer(typeof(string)).WriteObject(f,value);return Encoding.UTF8.GetString(f.ToArray());
    }
    internal static string PathOf(Transform t)
    {
        var parts=new List<string>();for(var n=t;n!=null;n=n.parent)parts.Add(n.name);parts.Reverse();return string.Join("/",parts);
    }
    internal static void Inventory()
    {
        Resources.LoadAll<GameObject>("");
        var rows=new List<string>();
        foreach(var t in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            var box=t.GetComponentInParent<TextBox>(true);var tooltip=t.GetComponentInParent<TooltipBox>(true);
            rows.Add("{\"path\":"+Q(PathOf(t.transform))+",\"text\":"+Q(t.text??"")+",\"active\":"+t.gameObject.activeInHierarchy.ToString().ToLowerInvariant()+",\"scene\":"+t.gameObject.scene.IsValid().ToString().ToLowerInvariant()+",\"textBox\":"+(box!=null?Q(PathOf(box.transform)):"null")+",\"tooltip\":"+(tooltip!=null?Q(PathOf(tooltip.transform)):"null")+",\"rect\":"+Q(t.rectTransform.rect.ToString())+",\"fontSize\":"+t.fontSize+"}");
        }
        File.WriteAllText(Path.Combine(LayoutPlugin.Root,"related-layout-inventory.json"),"["+string.Join(",",rows)+"]");
        File.WriteAllLines(Path.Combine(LayoutPlugin.Root,"related-component-inventory.txt"),
            Resources.FindObjectsOfTypeAll<TextBox>().Select(b=>"TextBox "+b.GetIl2CppType().FullName+" "+PathOf(b.transform))
            .Concat(Resources.FindObjectsOfTypeAll<TooltipGroup>().Select(b=>"TooltipGroup "+PathOf(b.transform)))
            .Concat(Resources.FindObjectsOfTypeAll<TooltipBox>().Select(b=>"TooltipBox "+PathOf(b.transform))));
    }
}
