using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TinyRogues.Chinese.Core;
using TMPro;
using UnityEngine;
using UI.Dialogue_System;

namespace TinyRogues.Chinese;

[BepInPlugin(Id, "Tiny Rogues Chinese", "0.1.0")]
[BepInIncompatibility("gravydevsupreme.xunity.autotranslator")]
[BepInIncompatibility("mugen.tinyrogues.tmpfallback")]
public sealed class Plugin : BasePlugin
{
    public const string Id = "community.tinyrogues.chinese";
    internal static Plugin Current = null!;
    internal Catalog? Catalog;
    internal IntPtr DialogueBody;
    private string _root = "";
    private string _fingerprint = "";
    private bool _record;
    private readonly HashSet<string> _misses = new(StringComparer.Ordinal);
    private float _nextReload;
    private TMP_FontAsset? _font;
    private bool _fontAttempted;
    internal string FontPath = "";
    internal Font? SourceFont;
    internal bool Diagnostic;

    public override void Load()
    {
        Current = this;
        _root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        _record = Config.Bind("Translation", "RecordMissing", true, "Record complete unknown English strings, never dialogue prefixes.").Value;
        FontPath = Config.Bind("Font", "Path", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf"), "Local CJK font file. Font binaries are not distributed.").Value;
        Diagnostic = Environment.GetCommandLineArgs().Contains("-tinyrogues-chinese-selftest");
        // Refuse the old translator stack instead of silently mixing its dictionaries.
        var plugins = IL2CPPChainloader.Instance.Plugins;
        if (plugins.ContainsKey("gravydevsupreme.xunity.autotranslator") || plugins.Values.Any(p => p.Metadata.GUID.Contains("tmpfallback")))
        {
            Log.LogError("Independent Chinese plugin disabled: another translator is installed. Move that translator outside BepInEx/plugins first.");
            return;
        }
        try
        {
            if (!File.Exists(FontPath)) throw new FileNotFoundException("Set Font.Path to an installed CJK font before enabling localization.", FontPath);
            Reload();
            Adapters.Install();
            AddComponent<LocalizationDriver>();
            if (Diagnostic) AddComponent<RuntimeVerification>();
            Log.LogInfo($"Independent localization active: {Catalog!.EntryCount} entries; no XUnity/legacy dictionaries loaded.");
        }
        catch (Exception error)
        {
            Adapters.Remove();
            Log.LogError($"Localization disabled safely: {error}");
        }
    }

    private (string Path, bool User)[] Files()
    {
        var list = new List<(string, bool)>();
        foreach (var folder in new[] { ("locales/zh-Hans", false), ("overrides", true) })
        {
            var path = Path.Combine(_root, folder.Item1);
            if (!Directory.Exists(path)) continue;
            list.AddRange(Directory.GetFiles(path, "*.json", SearchOption.AllDirectories).Select(file => (file, folder.Item2)));
        }
        return list.OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
    }

    private void Reload()
    {
        var files = Files();
        if (files.Length == 0) throw new InvalidDataException("No translation packs found.");
        var replacement = new Catalog(files.Select(file => (PackJson.Read(file.Path), file.User, file.Path)));
        Catalog = replacement; // Atomic replacement only after every file validates.
        Log.LogInfo($"Catalog loaded: {replacement.EntryCount} active entries.");
    }

    internal void Tick()
    {
        if (Time.unscaledTime < _nextReload) return;
        _nextReload = Time.unscaledTime + 1f;
        var fingerprint = string.Join("|", Files().Select(file => file.Path + ":" + File.GetLastWriteTimeUtc(file.Path).Ticks + ":" + new FileInfo(file.Path).Length));
        if (fingerprint == _fingerprint) return;
        _fingerprint = fingerprint;
        try { Reload(); } catch (Exception error) { Log.LogWarning($"Pack reload rejected; keeping previous catalog: {error.Message}"); }
    }

    internal string Translate(string source, string context, bool capture = true)
    {
        if (Catalog != null)
        {
            var label = context.StartsWith("ui:", StringComparison.Ordinal) || context == "styled-ui";
            if (label ? Catalog.TryTranslateLabel(source, context, out var target) : Catalog.TryTranslate(source, context, out target)) return target;
        }
        if (_record && capture && source != null && source.Length <= 12000 && Regex.IsMatch(Regex.Replace(source, "<[^>]*>", ""), "[A-Za-z]{3,}"))
        {
            var key = context + "\0" + source;
            if (_misses.Count < 20000 && _misses.Add(key))
                try
                {
                    Directory.CreateDirectory(Path.Combine(_root, "captures"));
                    File.AppendAllText(Path.Combine(_root, "captures", "missing.jsonl"),
                        "{\"context\":" + PackJson.Quote(context) + ",\"source\":" + PackJson.Quote(source) + "}" + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception error) { Log.LogWarning($"Cannot record untranslated text: {error.Message}"); }
        }
        return source!;
    }

    internal void FontFor(TMP_Text body)
    {
        if (!_fontAttempted)
        {
            _fontAttempted = true;
            try
            {
                if (!File.Exists(FontPath)) throw new FileNotFoundException("Local CJK font file missing.", FontPath);
                // The game's OS-font factory is stripped. Use a private source object
                // and redirect only its FontEngine face load to a local font file.
                SourceFont = new Font();
                _font = TMP_FontAsset.CreateFontAsset(SourceFont);
                if (_font == null) throw new InvalidOperationException("TMP FontEngine rejected the local CJK font.");
                _font.name = "TinyRogues Chinese OS Font";
                UnityEngine.Object.DontDestroyOnLoad(SourceFont);
                UnityEngine.Object.DontDestroyOnLoad(_font);
                Log.LogInfo($"Chinese fallback font created from local file: {Path.GetFileName(FontPath)}.");
            }
            catch (Exception error) { Log.LogError($"Chinese font unavailable: {error}. Set Font.Path to a local CJK font file."); }
        }
        var original = body.font;
        if (_font == null || original == null || original.Pointer == _font.Pointer) return;
        var fallbacks = original.fallbackFontAssetTable;
        if (fallbacks == null) original.fallbackFontAssetTable = fallbacks = new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();
        for (var index = 0; index < fallbacks.Count; index++) if (fallbacks[index]?.Pointer == _font.Pointer) return;
        fallbacks.Add(_font);
    }

    internal void Info(string message) => Log.LogInfo(message);
    internal void Error(string message) => Log.LogError(message);
}

public sealed class LocalizationDriver : MonoBehaviour
{
    public LocalizationDriver(IntPtr pointer) : base(pointer) { }
    private void Update()
    {
        try { Plugin.Current.Tick(); } catch (Exception error) { Plugin.Current.Error($"Catalog polling failed: {error.Message}"); }
    }
}
