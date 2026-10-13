using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TinyRogues.Chinese;

internal static class TavernVerification
{
    [DataContract]
    private sealed class Capture
    {
        [DataMember(Name = "context")] public string Context = "";
        [DataMember(Name = "source")] public string Source = "";
    }
    internal static string[] Sources(string context)
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "diagnostics", "tavern-load.json");
        using var stream = File.OpenRead(path);
        var rows = (Capture[])new DataContractJsonSerializer(typeof(Capture[])).ReadObject(stream)!;
        return rows.Where(row => row.Context == context).Select(row => row.Source).ToArray();
    }
}
