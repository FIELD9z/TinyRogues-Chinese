using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using TMPro;
using UnityEngine;
using UI.Text.World_Text_Boxes;

internal sealed class CorpusScan
{
    [DataContract] private sealed class Row
    {
        [DataMember(Name="name")]public string Name="";
        [DataMember(Name="text")]public string Text="";
    }
    private Row[] _rows;
    private TextBox _box;
    private int _index=-1;
    private int _frame;
    private float _last;
    internal CorpusScan()
    {
        using(var f=File.OpenRead(Path.Combine(LayoutPlugin.Root,"layout-corpus.json")))_rows=(Row[])new DataContractJsonSerializer(typeof(Row[])).ReadObject(f)!;
        Resources.LoadAll<GameObject>("");
        var prefab=Resources.FindObjectsOfTypeAll<TextBox>().First(t=>t.name.Contains("World")&&!t.gameObject.scene.IsValid());
        _box=UnityEngine.Object.Instantiate(prefab);
        File.WriteAllText(Path.Combine(LayoutPlugin.Root,"corpus-layout.jsonl"),"");
        Next();
    }
    internal bool Step()
    {
        if(Time.frameCount-_frame<3||Time.realtimeSinceStartup-_last<.035f)return false;
        Canvas.ForceUpdateCanvases();
        var texts=_box.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t=>t.name=="Text"&&t.gameObject.activeInHierarchy).ToArray();
        foreach(var t in texts)t.ForceMeshUpdate(true,true);
        var parts=new System.Collections.Generic.List<string>();
        foreach(var t in texts)
        {
            float measured=0;
            if(t.textInfo.lineCount>0)measured=t.textInfo.lineInfo[0].ascender-t.textInfo.lineInfo[t.textInfo.lineCount-1].descender;
            parts.Add("{\"text\":"+Q(t.text)+",\"width\":"+t.rectTransform.rect.width+",\"height\":"+t.rectTransform.rect.height+",\"preferredHeight\":"+t.preferredHeight+",\"measuredHeight\":"+measured+",\"lines\":"+t.textInfo.lineCount+"}");
        }
        float gap=999;
        if(texts.Length==2&&texts[0].textInfo.lineCount>0&&texts[1].textInfo.lineCount>0)
        {
            var last=texts[0].textInfo.lineInfo[texts[0].textInfo.lineCount-1].descender;
            var first=texts[1].textInfo.lineInfo[0].ascender;
            gap=(texts[0].transform.TransformPoint(new Vector3(0,last,0)).y-texts[1].transform.TransformPoint(new Vector3(0,first,0)).y)/texts[0].transform.lossyScale.y;
        }
        File.AppendAllText(Path.Combine(LayoutPlugin.Root,"corpus-layout.jsonl"),"{\"index\":"+_index+",\"name\":"+Q(_rows[_index].Name)+",\"paragraphGap\":"+gap+",\"labels\":["+string.Join(",",parts)+"]}\n");
        if(_index+1==_rows.Length){_box.Destroy();return true;}
        Next();return false;
    }
    private void Next()
    {
        _index++;_frame=Time.frameCount;_last=Time.realtimeSinceStartup;
        var texts=new Il2CppSystem.Collections.Generic.List<string>();texts.Add(_rows[_index].Text);texts.Add("后续段落校验");
        var provider=new TemporaryTextProvider("排版回归",texts,new Il2CppSystem.Collections.Generic.List<Tooltip>(),TextBoxStyle.DefaultWhite);
        _box.Initialize(provider.Cast<ITextProvider>());_box.SetRectPosition(Vector2.zero);
    }
    private static string Q(string s){using var f=new MemoryStream();new DataContractJsonSerializer(typeof(string)).WriteObject(f,s);return Encoding.UTF8.GetString(f.ToArray());}
}
