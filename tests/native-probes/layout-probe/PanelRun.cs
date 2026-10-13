using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UI.Text;

internal sealed class PanelRun
{
    private sealed class Stage{internal string Name="";internal Action Begin=null!;}
    private readonly List<Stage> _stages=new();
    private TMP_Text[] _labels=Array.Empty<TMP_Text>();
    private int _index=-1,_frame;
    private float _time;
    private Action? _hide;
    private GameObject? _anchor;
    private float? _expectedSpacing;
    internal PanelRun()
    {
        Resources.LoadAll<GameObject>("");
        var fontArg=Environment.GetCommandLineArgs().FirstOrDefault(a=>a.StartsWith("-layout-font="));
        var font=fontArg==null?TextMeshSettingId.PcSenior:(TextMeshSettingId)Enum.Parse(typeof(TextMeshSettingId),fontArg.Substring(13));
        TextMeshProxy.SetFontSetting(font);
        File.WriteAllText(Path.Combine(LayoutPlugin.Root,"panel-layout.jsonl"),"");
        var inspector=UI.Title_Screen.PlayerClassInspector.Instance;
        if(inspector!=null)foreach(var resource in Resources.LoadAll<Player.PlayerClass>(""))
        {
            var pc=resource;
            _stages.Add(new Stage{Name="class-inspector:"+pc.name,Begin=()=>
            {
                Activate(inspector.transform);inspector.enabled=false;inspector.Show(pc);
                _labels=new TMP_Text[]{inspector.classNameText,inspector.classDescriptionText,inspector.classStats,inspector.skillName,inspector.skillDescription,inspector.classAbilityTitle,inspector.classAbilityDescription};
                _hide=()=>inspector.Hide(false);
            }});
        }
        var meta=Resources.FindObjectsOfTypeAll<Meta_Perk_Tree.UI.MetaPerkTooltip>().FirstOrDefault(t=>t.gameObject.scene.IsValid());
        if(meta!=null)
        {
            _anchor=new GameObject("Layout probe stable anchor");var anchor=_anchor.AddComponent<RectTransform>();anchor.SetParent(meta.parentCanvas.transform,false);anchor.anchoredPosition=Vector2.zero;
            foreach(var resource in Resources.LoadAll<Meta_Perk_Tree.System.MetaPerk>(""))
            {
                var perk=resource;
                _stages.Add(new Stage{Name="mastery-tooltip:"+perk.name,Begin=()=>
                {
                    Activate(meta.transform);meta.HoverMetaPerk(anchor,Vector2.zero,perk);
                    _labels=new TMP_Text[]{meta.titleText,meta.descriptionText};_hide=()=>meta.HideHover();
                }});
            }
        }
        var help=UI.Help_Texts.HelpTextManager.Instance;
        if(help!=null)
        {
            UI.Help_Texts.HelpTextManager.LoadHelpTexts();
            var all=UI.Help_Texts.HelpTextManager.AllHelpTexts;
            for(var i=0;i<all.Count;i++)
            {
                var text=all[i];var index=i;
                _stages.Add(new Stage{Name="help-panel:"+index+":"+text.GetIl2CppType().Name,Begin=()=>
                {
                    Activate(help.transform);help.enabled=false;
                    help.titleProxy.SetText(text.Title());help.textProxy1.SetText(text.GetTextPart1());help.textProxy2.SetText(text.GetTextPart2());
                    _labels=new TMP_Text[]{help.title,help.textPart1,help.textPart2};
                    // Render through the native content controls without marking
                    // tutorials seen or writing player preferences.
                }});
            }
        }
        if(inspector!=null)
        {
            var text=inspector.skillDescription;
            void Reuse(string name,string value,float? nativeSpacing,float? expected)
            {
                _stages.Add(new Stage{Name=name,Begin=()=>
                {
                    Activate(text.transform);text.text=value;
                    if(nativeSpacing.HasValue)text.lineSpacing=nativeSpacing.Value;
                    _labels=new TMP_Text[]{text};_expectedSpacing=expected;
                }});
            }
            Reuse("reuse:chinese", "中文第一行\n中文第二行", 0, null);
            Reuse("reuse:english", "English first line\nEnglish second line", null, 0);
            Reuse("reuse:native-spacing", "中文第一行\n中文第二行", 77, 77);
            Reuse("reuse:english-native-spacing", "English first line\nEnglish second line", null, 77);
        }
        if(Environment.GetCommandLineArgs().Contains("-layout-panel-focus"))
            _stages.RemoveAll(s=>!s.Name.Contains("Holy Devoition")&&s.Name!="class-inspector:Dancer");
        if(Environment.GetCommandLineArgs().Contains("-layout-panel-reuse"))
            _stages.RemoveAll(s=>!s.Name.StartsWith("reuse:"));
        Next();
    }
    private static void Activate(Transform t){for(var p=t;p!=null;p=p.parent)p.gameObject.SetActive(true);}
    internal bool Step()
    {
        if(_index>=_stages.Count){if(_anchor!=null)UnityEngine.Object.Destroy(_anchor);return true;}
        if(Time.frameCount-_frame<8||Time.realtimeSinceStartup-_time<.4f)return false;
        Canvas.ForceUpdateCanvases();
        try
        {
            var parts=_labels.Where(t=>t!=null&&t.gameObject.activeInHierarchy&&!string.IsNullOrEmpty(t.text)).Select(NativeTextGeometry.Capture).ToArray();
            if(_expectedSpacing.HasValue && Math.Abs(_labels[0].lineSpacing-_expectedSpacing.Value)>.01f)
                throw new Exception("Reused panel line spacing not restored: "+_labels[0].lineSpacing+" expected "+_expectedSpacing.Value);
            if(_stages[_index].Name.Contains("Holy Devoition")||_stages[_index].Name=="class-inspector:Dancer")
                LayoutProbe.RenderGlyphs(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<TextMeshProUGUI>(_labels.Select(t=>t.Cast<TextMeshProUGUI>()).ToArray()),"panel-"+_index+".png");
            File.AppendAllText(Path.Combine(LayoutPlugin.Root,"panel-layout.jsonl"),"{\"scenario\":"+RelatedScan.Q(_stages[_index].Name)+",\"labels\":["+string.Join(",",parts)+"]}\n");
        }
        catch(Exception error){Error(error);}
        Next();return false;
    }
    private void Error(Exception e)=>File.AppendAllText(Path.Combine(LayoutPlugin.Root,"panel-layout.jsonl"),"{\"scenario\":"+RelatedScan.Q(_stages[_index].Name)+",\"error\":"+RelatedScan.Q(e.ToString())+"}\n");
    private void Next()
    {
        _hide?.Invoke();_hide=null;
        _expectedSpacing=null;
        while(++_index<_stages.Count)
        {
            try{_stages[_index].Begin();_frame=Time.frameCount;_time=Time.realtimeSinceStartup;return;}
            catch(Exception error){Error(error);}
        }
    }
}
