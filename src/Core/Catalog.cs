using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace TinyRogues.Chinese.Core;

[DataContract]
public sealed class TranslationPack
{
    [DataMember(Name = "schemaVersion", IsRequired = true)] public int SchemaVersion { get; set; } = 1;
    [DataMember(Name = "language", IsRequired = true)] public string Language { get; set; } = "zh-Hans";
    [DataMember(Name = "name")] public string Name { get; set; } = "";
    [DataMember(Name = "priority")] public int Priority { get; set; }
    [DataMember(Name = "entries", IsRequired = true)] public List<TranslationEntry> Entries { get; set; } = new();
    [OnDeserializing] private void Defaults(StreamingContext _) { Name = ""; Entries = new(); }
}

[DataContract]
public sealed class TranslationEntry
{
    [DataMember(Name = "id", IsRequired = true)] public string Id { get; set; } = "";
    [DataMember(Name = "source", IsRequired = true)] public string Source { get; set; } = "";
    [DataMember(Name = "translation", IsRequired = true)] public string Translation { get; set; } = "";
    [DataMember(Name = "context")] public string Context { get; set; } = "*";
    [DataMember(Name = "kind")] public string Kind { get; set; } = "exact";
    [DataMember(Name = "status")] public string Status { get; set; } = "translated";
    [DataMember(Name = "note")] public string Note { get; set; } = "";
    [DataMember(Name = "protectedTokens")] public List<string> ProtectedTokens { get; set; } = new();
    [OnDeserializing] private void Defaults(StreamingContext _) { Context = "*"; Kind = "exact"; Status = "translated"; Note = ""; ProtectedTokens = new(); }
}

public static class PackJson
{
    public static TranslationPack Read(string path)
    {
        using var stream = File.OpenRead(path);
        var serializer = new DataContractJsonSerializer(typeof(TranslationPack));
        return (TranslationPack)(serializer.ReadObject(stream) ?? throw new InvalidDataException("Empty translation pack."));
    }

    public static string Quote(string value)
    {
        using var stream = new MemoryStream();
        new DataContractJsonSerializer(typeof(string)).WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

// A deliberately small template language: translators never have to write regex.
// Numeric/key/text captures stay verbatim. Term captures require a known exact label.
public sealed class CompiledEntry
{
    private static readonly Regex Tokens = new(@"\{(number|key|text|term):([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);
    private static readonly Regex Protected = new(@"<[^>]*>|\[\[|\]\]|##|\{[^{}]*\}|\((?:[+\-#x%]*x\d+[x%\d]*|rate|br|lb|\*)\)", RegexOptions.CultureInvariant);
    private static readonly Regex Numbers = new(@"\d+(?:[.,]\d+)?", RegexOptions.CultureInvariant);
    private static readonly Regex RedMarkers = new(@"\(\([^()]*\)\)", RegexOptions.CultureInvariant);
    private readonly Regex? _pattern;
    private readonly HashSet<string> _terms = new(StringComparer.Ordinal);
    public TranslationEntry Entry { get; }
    public CompiledEntry(TranslationEntry entry)
    {
        Entry = entry;
        if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrEmpty(entry.Source) || string.IsNullOrEmpty(entry.Translation))
            throw new InvalidDataException("Entries require id, source and translation.");
        if (entry.Kind != "exact" && entry.Kind != "template") throw new InvalidDataException($"Unknown kind: {entry.Id}");
        if (string.IsNullOrEmpty(entry.Context)) entry.Context = "*";
        var before = Protected.Matches(entry.Source).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
        var after = Protected.Matches(entry.Translation).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
        if (!before.SequenceEqual(after)) throw new InvalidDataException($"Tags or placeholders changed: {entry.Id}");
        if (RedMarkers.Matches(entry.Source).Count != RedMarkers.Matches(entry.Translation).Count)
            throw new InvalidDataException($"Red color markers changed: {entry.Id}");
        var beforeNumbers = Numbers.Matches(Protected.Replace(entry.Source, "")).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
        var afterNumbers = Numbers.Matches(Protected.Replace(entry.Translation, "")).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
        if (!beforeNumbers.SequenceEqual(afterNumbers)) throw new InvalidDataException($"Literal numbers changed: {entry.Id}");
        foreach (var token in entry.ProtectedTokens)
        {
            if (string.IsNullOrEmpty(token) || !entry.Source.Contains(token)) throw new InvalidDataException($"Invalid protected token: {entry.Id}");
            if (Regex.Matches(entry.Source, Regex.Escape(token)).Count != Regex.Matches(entry.Translation, Regex.Escape(token)).Count)
                throw new InvalidDataException($"Native token changed: {entry.Id}: {token}");
        }
        if (entry.Context.EndsWith("-template", StringComparison.Ordinal))
        {
            // This parenthetical is visible prose, rather than a game value macro.
            var nativeTokens = new Regex(@"\((?!multiplicative\))(?:[+\-#x0-9%.,]+|[A-Za-z_][A-Za-z_0-9]*)\)", RegexOptions.CultureInvariant);
            var nativeBefore = nativeTokens.Matches(RedMarkers.Replace(entry.Source, "")).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
            var nativeAfter = nativeTokens.Matches(RedMarkers.Replace(entry.Translation, "")).Cast<Match>().Select(m => m.Value).OrderBy(x => x, StringComparer.Ordinal);
            if (!nativeBefore.SequenceEqual(nativeAfter)) throw new InvalidDataException($"Native template variables changed: {entry.Id}");
        }
        if (entry.Source.Count(c => c == '\n') != entry.Translation.Count(c => c == '\n'))
            throw new InvalidDataException($"Line breaks changed: {entry.Id}");
        if (entry.Kind == "exact") return;
        var expression = new StringBuilder(@"\A");
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var last = 0;
        foreach (Match token in Tokens.Matches(entry.Source))
        {
            expression.Append(Regex.Escape(entry.Source.Substring(last, token.Index - last)));
            var name = token.Groups[2].Value;
            if (names.TryGetValue(name, out var type))
            {
                if (type != token.Groups[1].Value) throw new InvalidDataException($"Variable has conflicting types: {entry.Id}: {name}");
                expression.Append(@"\k<" + name + ">");
            }
            else
            {
                names.Add(name, token.Groups[1].Value);
                if (token.Groups[1].Value == "term") _terms.Add(name);
                var body = token.Groups[1].Value switch
                {
                    "number" => @"[+\-−]?(?:\d+(?:[.,]\d+)?|[.,]\d+)%?",
                    "key" => @"[^<>\r\n{}]{1,48}",
                    "term" => @"(?:<(?:color=[^<>\r\n]{1,40}|b|i)>){0,3}[^<>\r\n{}]{1,80}(?:</(?:color|b|i)>){0,3}",
                    _ => @"[^<>\r\n{}]{1,120}"
                };
                expression.Append("(?<" + name + ">" + body + ")");
            }
            last = token.Index + token.Length;
        }
        if (names.Count == 0) throw new InvalidDataException($"Template has no typed placeholders: {entry.Id}");
        expression.Append(Regex.Escape(entry.Source.Substring(last))).Append(@"\z");
        _pattern = new Regex(expression.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30));
    }

    public bool TryTranslate(string source, out string target, Func<string, string?>? translateTerm = null)
    {
        target = source;
        if (_pattern == null)
        {
            if (!StringComparer.Ordinal.Equals(source, Entry.Source)) return false;
            target = Entry.Translation;
            return true;
        }
        Match match;
        try { match = _pattern.Match(source); } catch (RegexMatchTimeoutException) { return false; }
        if (!match.Success) return false;
        var terms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in _terms)
        {
            var translated = translateTerm?.Invoke(match.Groups[name].Value);
            if (translated == null) return false; // Never hide an unknown item behind a partly translated sentence.
            terms[name] = translated;
        }
        // Replace tokens once; captured strings containing $ or braces are never interpreted.
        target = Tokens.Replace(Entry.Translation, token => terms.TryGetValue(token.Groups[2].Value, out var term) ? term : match.Groups[token.Groups[2].Value].Value);
        return true;
    }
}

public sealed class Catalog
{
    private static readonly Regex Label = new(@"\A(?<prefix>(?:<(?:color=[^<>]+|size=[^<>]+|b|i|u)>)+)(?<label>[^<>\r\n]{1,120})(?<suffix>(?:</(?:color|size|b|i|u)>)+)\z", RegexOptions.CultureInvariant);
    private static readonly Regex LabelTags = new(@"</?(color|size|b|i|u)(?:=[^<>]+)?>", RegexOptions.CultureInvariant);
    private readonly Layer[] _layers;
    public int EntryCount { get; }
    public Catalog(IEnumerable<(TranslationPack Pack, bool User, string Path)> packs)
    {
        var list = new List<Layer>();
        foreach (var item in packs.OrderByDescending(p => p.User).ThenByDescending(p => p.Pack.Priority).ThenBy(p => p.Path, StringComparer.Ordinal))
        {
            if (item.Pack.SchemaVersion != 1 || item.Pack.Language != "zh-Hans") throw new InvalidDataException($"Unsupported schema/language: {item.Path}");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var unique = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries = new List<CompiledEntry>();
            foreach (var entry in item.Pack.Entries)
            {
                if (!ids.Add(entry.Id)) throw new InvalidDataException($"Duplicate id: {entry.Id}");
                if (entry.Status is "draft" or "needs-review" or "obsolete") continue;
                if (entry.Status != "translated" && entry.Status != "reviewed") throw new InvalidDataException($"Unknown status: {entry.Id}");
                var compiled = new CompiledEntry(entry);
                var key = entry.Context + "\0" + entry.Source;
                if (unique.TryGetValue(key, out var previous))
                {
                    if (previous != entry.Translation) throw new InvalidDataException($"Conflicting context/source: {entry.Id}");
                    continue; // Multiple Dialogue_EN identities can have the same visible message.
                }
                unique.Add(key, entry.Translation);
                entries.Add(compiled);
            }
            list.Add(new Layer(entries.ToArray()));
            EntryCount += entries.Count;
        }
        _layers = list.ToArray();
    }

    public bool TryTranslate(string source, string context, out string target)
    {
        target = source;
        if (string.IsNullOrEmpty(source) || source.Length > 12000) return false;
        foreach (var layer in _layers)
        {
            if (layer.TryTranslate(source, context, out target, TranslateTerm)) return true;
            if (context != "*" && layer.TryTranslate(source, "*", out target, TranslateTerm)) return true;
        }
        return false;
    }

    private string? TranslateTerm(string source)
    {
        foreach (var layer in _layers) if (layer.TryExact(source, "*", out var exact)) return exact;
        var wrapped = Label.Match(source);
        if (!wrapped.Success) return null;
        var prefix = wrapped.Groups["prefix"].Value;
        var suffix = wrapped.Groups["suffix"].Value;
        if (!LabelTags.Matches(prefix).Cast<Match>().Select(m => m.Groups[1].Value).Reverse().SequenceEqual(LabelTags.Matches(suffix).Cast<Match>().Select(m => m.Groups[1].Value))) return null;
        foreach (var layer in _layers)
            if (layer.TryExact(wrapped.Groups["label"].Value, "*", out var label)) return prefix + label + suffix;
        return null;
    }

    // A complete styled label is still one lookup. Never translate words inside prose.
    public bool TryTranslateLabel(string source, string context, out string target)
    {
        if (TryTranslate(source, context, out target)) return true;
        if (string.IsNullOrEmpty(source) || source.Length > 12000) return false;
        var wrapped = Label.Match(source);
        if (!wrapped.Success) return false;
        var prefix = wrapped.Groups["prefix"].Value;
        var suffix = wrapped.Groups["suffix"].Value;
        var opens = LabelTags.Matches(prefix).Cast<Match>().Select(m => m.Groups[1].Value).Reverse();
        var closes = LabelTags.Matches(suffix).Cast<Match>().Select(m => m.Groups[1].Value);
        if (!opens.SequenceEqual(closes)) return false;
        if (!TryTranslate(wrapped.Groups["label"].Value, context, out var label)) return false;
        target = prefix + label + suffix;
        return true;
    }

    private sealed class Layer
    {
        private readonly Dictionary<string, Dictionary<string, CompiledEntry>> _exact = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CompiledEntry>> _templates = new(StringComparer.Ordinal);
        public Layer(CompiledEntry[] entries)
        {
            foreach (var entry in entries)
            {
                var context = entry.Entry.Context;
                if (entry.Entry.Kind == "exact")
                {
                    if (!_exact.TryGetValue(context, out var map)) _exact[context] = map = new Dictionary<string, CompiledEntry>(StringComparer.Ordinal);
                    map.Add(entry.Entry.Source, entry);
                }
                else
                {
                    if (!_templates.TryGetValue(context, out var list)) _templates[context] = list = new List<CompiledEntry>();
                    list.Add(entry);
                }
            }
        }
        public bool TryExact(string source, string context, out string target)
        {
            target = source;
            if (_exact.TryGetValue(context, out var map) && map.TryGetValue(source, out var exact)) return exact.TryTranslate(source, out target);
            return false;
        }
        public bool TryTranslate(string source, string context, out string target, Func<string, string?> translateTerm)
        {
            if (TryExact(source, context, out target)) return true;
            if (_templates.TryGetValue(context, out var list))
                foreach (var entry in list) if (entry.TryTranslate(source, out target, translateTerm)) return true;
            return false;
        }
    }
}
