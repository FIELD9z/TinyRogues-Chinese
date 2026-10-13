using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using TMPro;
using UnityEngine;
using UI.Dialogue_System;
using UI.Text.World_Text_Boxes;
using UI.Text.Tag_Boxes;
using UI.Text.Tag_Boxes.Tag_Box_Definitions;

internal sealed class RelatedRun
{
    [DataContract] private sealed class Row
    {
        [DataMember(Name="name")]public string Name="";
        [DataMember(Name="text")]public string Text="";
    }
    private sealed class Stage
    {
        internal string Name="";
        internal Action Begin=null!;
    }
    private readonly List<Stage> _stages=new();
    private int _index=-1, _frame;
    private float _time;
    private TextBox? _box;
    private TMP_Text[] _labels=Array.Empty<TMP_Text>();
    private readonly DialogueSystemController _dialogue=DialogueSystemController.Instance;
    private Dictionary<string,TooltipDefinition> _definitions;
    private readonly UI.Text.TextMeshSettingId _font;
    internal RelatedRun()
    {
        Resources.LoadAll<GameObject>("");
        var fontArg=Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith("-layout-font="));
        _font=fontArg==null?UI.Text.TextMeshSettingId.PcSenior:(UI.Text.TextMeshSettingId)Enum.Parse(typeof(UI.Text.TextMeshSettingId),fontArg.Substring(13));
        UI.Text.TextMeshProxy.SetFontSetting(_font);
        _definitions=Resources.LoadAll<TooltipDefinition>("").ToDictionary(t=>t.name);
        File.WriteAllText(Path.Combine(LayoutPlugin.Root,"related-layout.jsonl"),"");
        foreach(var count in new[]{3,6,11})foreach(var selected in new[]{0,count-1})
        {
            var n=count;var pick=selected;
            _stages.Add(new Stage{Name=$"dialogue-multiple:{n}:selected={pick}",Begin=()=>Choices(n,pick)});
        }
        Row[] cases;
        using(var f=File.OpenRead(Path.Combine(LayoutPlugin.Root,"layout-corpus.json")))cases=(Row[])new DataContractJsonSerializer(typeof(Row[])).ReadObject(f)!;
        foreach(var prefab in Resources.FindObjectsOfTypeAll<TextBox>().Where(b=>!b.gameObject.scene.IsValid()).ToArray())
        foreach(var sample in cases)
        {
            var p=prefab;var row=sample;var name=RelatedScan.PathOf(p.transform);
            if(!p.name.Contains("World"))_stages.Add(new Stage{Name="main:"+name+":"+row.Name,Begin=()=>Card(ResolvePrefab(name),row,false)});
            _stages.Add(new Stage{Name="comparison:"+name+":"+row.Name,Begin=()=>Card(ResolvePrefab(name),row,true)});
        }
        var world=Resources.FindObjectsOfTypeAll<TextBox>().First(b=>b.name.Contains("World")&&!b.gameObject.scene.IsValid());
        Row[] tips;
        using(var f=File.OpenRead(Path.Combine(LayoutPlugin.Root,"tooltip-corpus.json")))tips=(Row[])new DataContractJsonSerializer(typeof(Row[])).ReadObject(f)!;
        foreach(var row in tips)
        {
            var r=row;_stages.Add(new Stage{Name="tooltip-world:"+r.Name,Begin=()=>Tooltip(ResolvePrefab("Universal_World_Text_Box_Object"),r)});
        }
        if(Environment.GetCommandLineArgs().Contains("-layout-related-focus"))
            _stages.RemoveAll(s=>!s.Name.Contains("equipment:621")&&!s.Name.Contains("Avoid Taking Damage"));
        Next();
    }
    internal bool Step()
    {
        if(Time.frameCount-_frame<5||Time.realtimeSinceStartup-_time<.15f)return false;
        Canvas.ForceUpdateCanvases();
        var active=_labels.Where(t=>t!=null&&t.gameObject.activeInHierarchy).ToArray();
        var parts=active.Select(NativeTextGeometry.Capture).ToArray();
        if (_stages[_index].Name=="main:Item Element/Canvas:equipment:621" || _stages[_index].Name=="tooltip-world:Avoid Taking Damage Tag Box Definition:details=False:base=False")
            LayoutProbe.RenderGlyphs(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TextMeshProUGUI>(active.Select(t=>t.Cast<TextMeshProUGUI>()).ToArray()),"related-"+_index+".png");
        File.AppendAllText(Path.Combine(LayoutPlugin.Root,"related-layout.jsonl"),"{\"scenario\":"+RelatedScan.Q(_stages[_index].Name)+",\"labels\":["+string.Join(",",parts)+"]}\n");
        if(_index+1>=_stages.Count){Cleanup();return true;}Next();return false;
    }
    private void Cleanup(){_dialogue.HideMessage();if(_box!=null){_box.Destroy();_box=null;}}
    private void Next()
    {
        Cleanup();_index++;_stages[_index].Begin();
        _frame=Time.frameCount;_time=Time.realtimeSinceStartup;
    }
    private void Choices(int count,int selected)
    {
        var list=new Il2CppSystem.Collections.Generic.List<Choice>();
        var callback=(Il2CppSystem.Action)(Action)(()=>{});var tips=new Il2CppSystem.Collections.Generic.List<ITooltip>();
        var names=new[]{"交谈","购买","离开","查看详情","取消","交换","使用","装备","丢弃","继续","返回"};
        for(var i=0;i<count;i++)list.Add(new Choice(names[i],callback,tips,i==count-1));
        _dialogue.PromptMessage("选择一项操作。",Vector2.zero,list.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Choice>>(),null,false,false);
        _dialogue.SelectChoice(selected);
        _dialogue.currentLetterCount=_dialogue.TargetLetterCount;_dialogue.RefreshText();
        _labels=_dialogue.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t=>t.Cast<TMP_Text>()).ToArray();
    }
    private TemporaryTextProvider Provider(Row row)
    {
        var parts=new Il2CppSystem.Collections.Generic.List<string>();parts.Add(row.Text);parts.Add("后续段落校验");
        return new TemporaryTextProvider("说明与比较",parts,new Il2CppSystem.Collections.Generic.List<Tooltip>(),TextBoxStyle.DefaultWhite);
    }
    private void Card(TextBox prefab,Row row,bool compare)
    {
        _box=UnityEngine.Object.Instantiate((UnityEngine.Object)prefab).Cast<TextBox>();_box.gameObject.SetActive(true);
        _box.Initialize(Provider(row).Cast<ITextProvider>());_box.SetRectPosition(Vector2.zero);
        if(compare)_box.ShowComparison(Provider(row).Cast<ITextProvider>(),false,false);
        _labels=_box.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t=>t.Cast<TMP_Text>()).ToArray();
    }
    private static TextBox ResolvePrefab(string path)
    {
        Resources.LoadAll<GameObject>("");
        return Resources.FindObjectsOfTypeAll<TextBox>().First(b=>!b.gameObject.scene.IsValid()&&RelatedScan.PathOf(b.transform)==path);
    }
    private void Tooltip(TextBox prefab,Row row)
    {
        Card(prefab,new Row{Name="tip-host",Text="提示说明测试"},false);
        var key=row.Name.Split(':')[0];
        var definition=_definitions.TryGetValue(key,out var found)?found:_definitions.First(t=>t.Key.Contains("Block Tag")).Value;
        var tips=new Il2CppSystem.Collections.Generic.List<ITooltip>();tips.Add(definition.Cast<ITooltip>());
        var group=_box!.GetComponentInChildren<TooltipGroup>(true);group.SetTooltips(tips.Cast<Il2CppSystem.Collections.Generic.IEnumerable<ITooltip>>());group.ShowImmediately();
        foreach(var tipBox in group.GetComponentsInChildren<TooltipBox>(true))
        {
            if(tipBox.assignedToolTip==null)continue;
            tipBox.gameObject.SetActive(true);
            tipBox.ForceRefreshText(row.Name.Contains("details=True"),row.Name.Contains("base=True"));
            if(key=="capture")tipBox.textMeshProxy.SetText(row.Text);
        }
        _labels=_box.GetComponentsInChildren<TextMeshProUGUI>(true).Select(t=>t.Cast<TMP_Text>()).ToArray();
    }
}
