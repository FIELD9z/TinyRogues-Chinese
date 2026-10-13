using System;
using System.Linq;
using TMPro;
using UnityEngine;

internal static class NativeTextGeometry
{
    private static string Quote(string value)
    {
        using var stream=new System.IO.MemoryStream();
        new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(string)).WriteObject(stream,value);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
    private static string PathOf(Transform transform)
    {
        var parts=new System.Collections.Generic.List<string>();
        for(var current=transform;current!=null;current=current.parent)parts.Add(current.name);
        parts.Reverse();return string.Join("/",parts);
    }
    internal static string Capture(TMP_Text t)
    {
        if((t.text??"").Length>20000)throw new Exception("Oversized diagnostic text");
            t.ForceMeshUpdate(true,true);
            var info=t.textInfo;var count=info.lineCount;
            float actual=count>0?info.lineInfo[0].ascender-info.lineInfo[count-1].descender:0;
            float lineGap=999;
            for(var i=1;i<count;i++)if(info.lineInfo[i-1].visibleCharacterCount>0&&info.lineInfo[i].visibleCharacterCount>0)
                lineGap=Math.Min(lineGap,info.lineInfo[i-1].descender-info.lineInfo[i].ascender);
            var rect=t.rectTransform.rect;var corners=new Vector3[4];t.rectTransform.GetWorldCorners(corners);
            float left=float.MaxValue,right=float.MinValue,bottom=float.MaxValue,top=float.MinValue;
            int backwardAdvances=0, previousLine=-1;
            float previousOrigin=float.MinValue;
            for(var i=0;i<info.characterCount;i++)if(info.characterInfo[i].isVisible)
            {
                var ch=info.characterInfo[i];var lo=t.transform.TransformPoint(ch.bottomLeft);var hi=t.transform.TransformPoint(ch.topRight);
                if(ch.lineNumber==previousLine && ch.origin<previousOrigin-.01f)backwardAdvances++;
                previousLine=ch.lineNumber;previousOrigin=ch.origin;
                left=Math.Min(left,lo.x);right=Math.Max(right,hi.x);bottom=Math.Min(bottom,lo.y);top=Math.Max(top,hi.y);
            }
            float glyphGap=999;
            for(var line=1;line<count;line++)
            {
                float priorBottom=float.MaxValue,nextTop=float.MinValue;
                for(var c=0;c<info.characterCount;c++)if(info.characterInfo[c].isVisible)
                {
                    var ch=info.characterInfo[c];
                    if(ch.lineNumber==line-1)priorBottom=Math.Min(priorBottom,ch.bottomLeft.y);
                    if(ch.lineNumber==line)nextTop=Math.Max(nextTop,ch.topRight.y);
                }
                if(priorBottom!=float.MaxValue&&nextTop!=float.MinValue)glyphGap=Math.Min(glyphGap,priorBottom-nextTop);
            }
            var missing=(t.text??"").Where(ch=>ch>='\u4e00'&&ch<='\u9fff').Distinct().Count(ch=>!t.font.HasCharacter(ch,true,false));
            return "{\"screenWidth\":"+Screen.width+",\"screenHeight\":"+Screen.height+",\"backwardAdvances\":"+backwardAdvances+",\"glyphGap\":"+glyphGap+",\"spacing\":"+t.lineSpacing+",\"pointSize\":"+t.font.faceInfo.pointSize+",\"fontSize\":"+t.fontSize+",\"path\":"+Quote(PathOf(t.transform))+",\"text\":"+Quote(t.text??"")+",\"font\":"+Quote(t.font.name)+",\"missingCjk\":"+missing+",\"lines\":"+count+",\"lineGap\":"+lineGap+",\"height\":"+rect.height+",\"measuredHeight\":"+actual+",\"width\":"+rect.width+",\"preferredWidth\":"+t.preferredWidth+",\"wrap\":"+t.enableWordWrapping.ToString().ToLowerInvariant()+",\"glyphBounds\":["+left+","+bottom+","+right+","+top+"]}";
    }
}
