using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using System.Runtime.Serialization.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UI.Dialogue_System;

[BepInPlugin("community.tinyrogues.layout-probe", "Tiny Rogues Layout Probe", "0.1.0")]
public sealed class LayoutPlugin : BasePlugin
{
    internal static string Root = "";
    public override void Load()
    {
        if (!Environment.GetCommandLineArgs().Contains("-tinyrogues-layout-probe")) return;
        Root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        new Harmony("community.tinyrogues.layout-probe").PatchAll(Assembly.GetExecutingAssembly());
        AddComponent<LayoutProbe>();
    }
}
[HarmonyPatch]
internal static class BlockSave
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(Save_System.SaveManager).GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.Name=="Save");
    private static bool Prefix() => false;
}
public sealed class LayoutProbe : MonoBehaviour
{
    private float _start, _phaseStart;
    private int _phase=-1;
    private bool _done;
    private bool _captured;
    private FieldInfo? _catalogField;
    private object? _plugin, _catalog;
    private UI.Text.World_Text_Boxes.TextBox? _box;
    private CorpusScan? _corpus;
    public LayoutProbe(IntPtr p):base(p){}
    private void Awake(){_start=Time.realtimeSinceStartup;File.WriteAllText(Path.Combine(LayoutPlugin.Root,"layout.jsonl"),"");}
    private void Update()
    {
        if(_done)return;
        try
        {
            if(Time.realtimeSinceStartup-_start>240)throw new Exception("Layout probe timed out");
            var c=DialogueSystemController.Instance;
            if(c==null || c.text==null || Time.realtimeSinceStartup-_start<5)return;
            if(Environment.GetCommandLineArgs().Contains("-tinyrogues-layout-corpus"))
            {
                if(_corpus==null)_corpus=new CorpusScan();
                if(_corpus.Step()){_done=true;Application.Quit();}
                return;
            }
            if(_phase==-1)
            {
                var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("TinyRogues.Chinese.Plugin")).First(t=>t!=null)!;
                _plugin=type.GetField("Current",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null);
                _catalogField=type.GetField("Catalog",BindingFlags.Instance|BindingFlags.NonPublic);
                _catalog=_catalogField!.GetValue(_plugin);
                Next(c);return;
            }
            if(Time.realtimeSinceStartup-_phaseStart<3)return;
            if(_captured)
            {
                if(Time.realtimeSinceStartup-_phaseStart<3.3f)return;
                if(_phase>=8){_catalogField!.SetValue(_plugin,_catalog);_done=true;Application.Quit();return;}
                Next(c);return;
            }
            Capture(c.text,"body");Capture(c.choiceA,"choiceA");Capture(c.choiceB,"choiceB");
            if(_phase>=1&&_phase<=4&&(c.choiceA.textInfo.lineCount!=1||c.choiceB.textInfo.lineCount!=1))throw new Exception("Short Chinese choice wrapped");
            if((_phase==7&&(!c.choiceA.enableWordWrapping||!c.choiceB.enableWordWrapping))||(_phase==8&&!c.choiceA.enableWordWrapping))throw new Exception("Native choice wrapping not restored");
            if(_box!=null)foreach(var label in _box.GetComponentsInChildren<TextMeshProUGUI>(true))if(label.gameObject.activeInHierarchy)Capture(label,"card:"+label.name);
            foreach(var label in c.GetComponentsInChildren<TextMeshProUGUI>(true))
                if(label!=c.text && label!=c.choiceA && label!=c.choiceB && label.gameObject.activeInHierarchy)Capture(label,"other");
            ScreenCapture.CaptureScreenshot(Path.Combine(LayoutPlugin.Root,"layout-"+_phase+".png"));
            RenderOffscreen(_box!=null?_box.GetComponentInParent<Canvas>():c.text.GetComponentInParent<Canvas>());
            RenderGlyphs(_box!=null?_box.GetComponentsInChildren<TextMeshProUGUI>(true):c.GetComponentsInChildren<TextMeshProUGUI>(true));
            _captured=true;
        }
        catch(Exception e){File.AppendAllText(Path.Combine(LayoutPlugin.Root,"layout-error.txt"),e.ToString());_done=true;Application.Quit(2);}
    }
    private void Next(DialogueSystemController c)
    {
        c.HideMessage();_phase++;_phaseStart=Time.realtimeSinceStartup;_captured=false;
        _catalogField!.SetValue(_plugin,_phase==0||_phase==7?null:_catalog);
        if(_phase>=5&&_phase<=6)
        {
            if(_box!=null)_box.Destroy();
            Resources.LoadAll<GameObject>("");
            var prefab=Resources.FindObjectsOfTypeAll<UI.Text.World_Text_Boxes.TextBox>().First(t=>t.name.Contains("World"));
            _box=UnityEngine.Object.Instantiate(prefab);
            var sections=new Il2CppSystem.Collections.Generic.List<string>();
            sections.Add(_phase==5?"优秀 副手\n+80 装备负重<sprite name=\"Arrow_Right\">138/95\n(*)+1/1 格挡<sprite name=\"Block\">\n(*)+2/2 护甲<sprite name=\"Armor\">\n调谐：1/1":"(*)<i>压制<Suppression>可抵挡\n一次伤害，但仅在\n首领战中生效。</i>\n(*)<i>你的压制<Suppression>\n会在每层刷新。</i>");
            sections.Add("这是第二段物品说明，用于检查相邻段落是否重叠。\n数值 +25% 和图标<sprite name=\"Heart\">应当保持完整。");
            var provider=new UI.Text.World_Text_Boxes.TemporaryTextProvider("门盾",sections,new Il2CppSystem.Collections.Generic.List<Tooltip>(),UI.Text.World_Text_Boxes.TextBoxStyle.DefaultWhite);
            _box.Initialize(provider.Cast<UI.Text.World_Text_Boxes.ITextProvider>());
            _box.SetRectPosition(Vector2.zero);
            return;
        }
        if(_box!=null){_box.Destroy();_box=null;}
        var choices=new Il2CppSystem.Collections.Generic.List<Choice>();
        var callback=(Il2CppSystem.Action)(Action)(()=>{});
        var tips=new Il2CppSystem.Collections.Generic.List<UI.Text.Tag_Boxes.ITooltip>();
        choices.Add(new Choice(_phase==8?"多行\n选项":_phase==3?"Talk":"Yes",callback,tips,false));
        choices.Add(new Choice(_phase==3?"Leave":"No",callback,tips,true));
        string message=_phase==2?"Would you like to stay\nfor the night?\n<color=grey>[Will reroll tavern visitors.]</color> [<color=#FFFFFF>10</color><color=#FCC926> Gold<sprite name=\"Gold\"></color>]":_phase==4?"这是用于检查中文行距、自动换行和文字框边界的完整说明。\n第二行包含数值 +25% 和图标<sprite name=\"Heart\">。\n第三行用于检查正文与选项是否发生重叠。":"Need a little help?";
        c.PromptMessage(message,Vector2.zero,choices.Cast<Il2CppSystem.Collections.Generic.IEnumerable<Choice>>(),null,false,false);
    }
    private void Capture(TMP_Text t,string role)
    {
        t.ForceMeshUpdate(true,true);
        var b=new StringBuilder();
        b.Append("{\"phase\":").Append(_phase).Append(",\"role\":").Append(Q(role)).Append(",\"text\":").Append(Q(t.text));
        b.Append(",\"active\":").Append(t.gameObject.activeInHierarchy?"true":"false");
        b.Append(",\"font\":").Append(Q(t.font.name)).Append(",\"fontSize\":").Append(t.fontSize).Append(",\"lineSpacing\":").Append(t.lineSpacing);
        b.Append(",\"autoSize\":").Append(t.enableAutoSizing?"true":"false").Append(",\"wrap\":").Append(t.enableWordWrapping?"true":"false");
        b.Append(",\"preferredWidth\":").Append(t.preferredWidth).Append(",\"preferredHeight\":").Append(t.preferredHeight).Append(",\"rect\":").Append(Q(t.rectTransform.rect.ToString()));
        b.Append(",\"margin\":").Append(Q(t.margin.ToString())).Append(",\"lineCount\":").Append(t.textInfo.lineCount).Append(",\"chars\":[");
        for(int i=0;i<t.textInfo.characterCount;i++)
        {
            var ch=t.textInfo.characterInfo[i];if(i>0)b.Append(',');
            var bl=t.transform.TransformPoint(ch.bottomLeft);var tr=t.transform.TransformPoint(ch.topRight);
            b.Append("{\"c\":").Append(Q(ch.character.ToString())).Append(",\"line\":").Append(ch.lineNumber).Append(",\"visible\":").Append(ch.isVisible?"true":"false");
            b.Append(",\"x0\":").Append(bl.x).Append(",\"y0\":").Append(bl.y).Append(",\"x1\":").Append(tr.x).Append(",\"y1\":").Append(tr.y).Append('}');
        }
        b.Append("]}");File.AppendAllText(Path.Combine(LayoutPlugin.Root,"layout.jsonl"),b+"\n");
    }
    private static string Q(string s){using var m=new MemoryStream();new DataContractJsonSerializer(typeof(string)).WriteObject(m,s??"");return Encoding.UTF8.GetString(m.ToArray());}
    [Il2CppInterop.Runtime.Attributes.HideFromIl2Cpp]
    private void RenderGlyphs(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> labels)
    {
        var objects=new List<GameObject>();var materials=new List<Material>();
        var points=new List<Vector3>();
        void Add(Mesh mesh,Material original,Transform transform)
        {
            if(mesh==null||mesh.vertexCount==0||original==null)return;
            var obj=new GameObject("Captured native glyph mesh");obj.layer=31;
            obj.transform.position=transform.position;obj.transform.rotation=transform.rotation;obj.transform.localScale=transform.lossyScale;
            obj.AddComponent<MeshFilter>().sharedMesh=mesh;
            var material=new Material(original);material.DisableKeyword("UNITY_UI_CLIP_RECT");material.SetFloat("_StencilComp",8);material.SetFloat("_Stencil",0);material.SetFloat("_CullMode",0);material.SetFloat("unity_GUIZTestMode",8);
            obj.AddComponent<MeshRenderer>().sharedMaterial=material;objects.Add(obj);materials.Add(material);
            foreach(var p in mesh.vertices)if(p!=Vector3.zero)points.Add(transform.TransformPoint(p));
        }
        foreach(var label in labels)
        {
            if(!label.gameObject.activeInHierarchy||string.IsNullOrEmpty(label.text))continue;
            Add(label.mesh,label.fontSharedMaterial,label.transform);
            foreach(var sub in label.GetComponentsInChildren<TMP_SubMeshUI>(true))Add(sub.mesh,sub.sharedMaterial,sub.transform);
        }
        if(points.Count==0)return;
        var minX=points.Min(p=>p.x);var maxX=points.Max(p=>p.x);var minY=points.Min(p=>p.y);var maxY=points.Max(p=>p.y);
        var camObj=new GameObject("Glyph proof camera");var camera=camObj.AddComponent<Camera>();
        camera.transform.position=new Vector3((minX+maxX)/2,(minY+maxY)/2,points.Min(p=>p.z)-100);
        camera.orthographic=true;camera.orthographicSize=Math.Max((maxY-minY)/2,(maxX-minX)/2/ (1920f/1080))*1.08f+.03f;
        camera.nearClipPlane=.1f;camera.farClipPlane=1000;camera.cullingMask=1<<31;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.04f,.04f,.055f,1);
        var rt=new RenderTexture(1920,1080,24);rt.Create();camera.targetTexture=rt;var old=RenderTexture.active;
        camera.Render();RenderTexture.active=rt;var png=new Texture2D(1920,1080,TextureFormat.RGBA32,false);png.ReadPixels(new Rect(0,0,1920,1080),0,0);png.Apply();
        File.WriteAllBytes(Path.Combine(LayoutPlugin.Root,"glyphs-"+_phase+".png"),ImageConversion.EncodeToPNG(png));
        RenderTexture.active=old;camera.targetTexture=null;rt.Release();UnityEngine.Object.Destroy(rt);UnityEngine.Object.Destroy(png);UnityEngine.Object.Destroy(camObj);
        foreach(var obj in objects)UnityEngine.Object.Destroy(obj);foreach(var mat in materials)UnityEngine.Object.Destroy(mat);
    }
    private void RenderOffscreen(Canvas canvas)
    {
        if(canvas==null)return;
        canvas=canvas.rootCanvas;
        var previousMode=canvas.renderMode;var previousCamera=canvas.worldCamera;var previousDistance=canvas.planeDistance;
        var cameraObject=new GameObject("Offscreen layout camera");var camera=cameraObject.AddComponent<Camera>();
        var texture=new RenderTexture(1920,1080,24);texture.Create();
        camera.targetTexture=texture;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.06f,.06f,.08f,1);
        camera.orthographic=true;camera.orthographicSize=540;camera.transform.position=new Vector3(0,0,-100);camera.nearClipPlane=.1f;camera.farClipPlane=1000;
        var layers=new List<(GameObject obj,int layer)>();
        foreach(var transform in canvas.GetComponentsInChildren<Transform>(true)){layers.Add((transform.gameObject,transform.gameObject.layer));transform.gameObject.layer=30;}
        camera.cullingMask=1<<30;
        var previousActive=RenderTexture.active;
        try
        {
            canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=10;
            Canvas.ForceUpdateCanvases();camera.Render();RenderTexture.active=texture;
            var png=new Texture2D(1920,1080,TextureFormat.RGBA32,false);png.ReadPixels(new Rect(0,0,1920,1080),0,0);png.Apply();
            File.WriteAllBytes(Path.Combine(LayoutPlugin.Root,"offscreen-"+_phase+".png"),ImageConversion.EncodeToPNG(png));
            UnityEngine.Object.Destroy(png);
        }
        finally
        {
            canvas.renderMode=previousMode;canvas.worldCamera=previousCamera;canvas.planeDistance=previousDistance;
            foreach(var layer in layers)layer.obj.layer=layer.layer;
            RenderTexture.active=previousActive;camera.targetTexture=null;texture.Release();UnityEngine.Object.Destroy(texture);UnityEngine.Object.Destroy(cameraObject);
        }
    }
}
