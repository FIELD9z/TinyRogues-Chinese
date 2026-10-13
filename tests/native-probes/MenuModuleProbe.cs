using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UI;
using UI.Title_Screen;

// Read-only menu text probe. It opens the Options screen from the title menu,
// captures every instantiated scene TMP_Text, and visits only buttons whose
// visible label is on the explicit navigation whitelist below. It never clicks
// reset, delete, confirm, save-slot, exit, or other unclassified controls.
[BepInPlugin("community.tinyrogues.menu-module-probe", "Tiny Rogues Menu Module Probe", "0.1.0")]
public sealed class MenuModuleProbePlugin : BasePlugin
{
    private const string Switch = "-tinyrogues-menu-module-probe";
    internal static string OutputPath = "";
    internal static string SaveRoot = "";

    public override void Load()
    {
        if (!Environment.GetCommandLineArgs().Contains(Switch, StringComparer.OrdinalIgnoreCase))
        {
            Log.LogInfo("Menu module probe idle; dedicated switch not present.");
            return;
        }

        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        OutputPath = Path.Combine(pluginDirectory, "menu-module-capture.jsonl");
        SaveRoot = Path.Combine(pluginDirectory, "menu-module-probe-isolated-saves");
        try
        {
            Directory.CreateDirectory(SaveRoot);
            new Harmony("community.tinyrogues.menu-module-probe.save-isolation")
                .PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo("Save_System.SaveManager.SaveFilePath redirected to isolated directory: " + SaveRoot);
        }
        catch (Exception error)
        {
            Log.LogError("Save path isolation could not be installed; aborting the probe: " + error);
            Application.Quit(1);
            return;
        }
        AddComponent<MenuModuleProbe>();
        Log.LogInfo("Menu probe armed; title Options screen and safe navigation tabs will be captured.");
    }
}

[HarmonyPatch(typeof(Save_System.SaveManager), nameof(Save_System.SaveManager.SaveFilePath))]
internal static class MenuProbeSavePathIsolationPatch
{
    private static void Postfix(ref string __result)
    {
        if (string.IsNullOrWhiteSpace(MenuModuleProbePlugin.SaveRoot))
            throw new InvalidOperationException("Save path isolation root is not configured.");

        var original = __result ?? "";
        var fileName = Path.GetFileName(original);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("SaveManager returned an invalid save file path.");

        using var sha = SHA256.Create();
        var digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(original))))
            .Replace("-", "").Substring(0, 16).ToLowerInvariant();
        var safeName = Path.GetFileNameWithoutExtension(fileName) + "_" + digest + Path.GetExtension(fileName);
        __result = Path.GetFullPath(Path.Combine(MenuModuleProbePlugin.SaveRoot, safeName));

        var rootPrefix = Path.GetFullPath(MenuModuleProbePlugin.SaveRoot) + Path.DirectorySeparatorChar;
        if (!__result.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Redirected save path escaped the isolated directory.");
    }
}

[HarmonyPatch]
internal static class MenuProbeBlockSaveWrites
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        typeof(Save_System.SaveManager).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "Save");

    private static bool Prefix() => false;
}

public sealed class MenuModuleProbe : MonoBehaviour
{
    private const float TimeoutSeconds = 45f;
    private const float SettleSeconds = 0.45f;
    private static readonly HashSet<string> SafeNavigationLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Audio", "Sound", "Video", "Display", "Graphics", "Gameplay", "Controls",
        "Keyboard", "Gamepad", "Accessibility", "Language", "General", "Options",
        "音频", "声音", "视频", "显示", "画面", "游戏", "游戏体验", "操作", "控制", "键盘", "手柄", "常规", "设置", "选项"
    };

    private float _deadline;
    private float _nextCapture;
    private int _stage; // 0 title, 1 options open, 2 safe option tabs
    private bool _settlePending;
    private bool _finished;
    private string _pendingPage = "options-tab";
    private readonly HashSet<string> _capturedTabKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _capturedRows = new HashSet<string>(StringComparer.Ordinal);

    public MenuModuleProbe(IntPtr pointer) : base(pointer) { }

    private void Start()
    {
        _deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        try { File.WriteAllText(MenuModuleProbePlugin.OutputPath, "", new UTF8Encoding(false)); }
        catch (Exception error) { Fail("Unable to initialize capture file: " + error); }
    }

    private void Update()
    {
        if (_finished) return;
        if (Time.realtimeSinceStartup > _deadline) { Fail("Timed out while waiting for title/options menus."); return; }

        try
        {
            if (_stage == 0)
            {
                if (Time.realtimeSinceStartup < 5) return;
                var title = TitleMenuController.Instance;
                if (title == null) return;
                var splash = SplashTextHandler.Instance;
                if (splash != null && splash.Texts != null)
                {
                    var lines = new System.Collections.Generic.List<string>();
                    for (var i = 0; i < splash.Texts.Count; i++) lines.Add("\"" + Escape(splash.Texts[i] ?? "") + "\"");
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(MenuModuleProbePlugin.OutputPath)!, "splash-sources.json"), "[" + string.Join(",", lines) + "]");
                }
                Capture("title-before-options");
                title.PressOptions(); // confirmed public method; opens options without activating a prompt action
                _stage = 1;
                _nextCapture = Time.realtimeSinceStartup + SettleSeconds;
                Log("Called TitleMenuController.PressOptions().");
                return;
            }

            if (_stage == 1)
            {
                if (Time.realtimeSinceStartup < _nextCapture) return;
                Capture("options-initial");
                _stage = 2;
                return;
            }

            if (_stage == 3)
            {
                if (Time.realtimeSinceStartup >= _nextCapture)
                {
                    Capture("runtime-snapshot");
                    _nextCapture = Time.realtimeSinceStartup + 1f;
                }
                return;
            }

            if (_stage != 2 || Time.realtimeSinceStartup < _nextCapture) return;
            if (_settlePending)
            {
                Capture(_pendingPage);
                _settlePending = false;
                return;
            }

            // UI.MyButton.onClickButton is the button event used by the existing
            // compiled character-panel probe. Restrict invocation to a visible,
            // exact-whitelist label to avoid reset/confirm/delete actions.
            var candidates = Resources.FindObjectsOfTypeAll<MyButton>()
                .Where(button => button != null && button.gameObject.scene.IsValid() && button.gameObject.activeInHierarchy)
                .Select(button => new { Button = button, Label = GetButtonLabel(button), Path = HierarchyPath(button.transform) })
                .Where(item => item.Label.Length > 0 && SafeNavigationLabels.Contains(NormalizeLabel(item.Label)))
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ToArray();

            var next = candidates.FirstOrDefault(item => !_capturedTabKeys.Contains(TabKey(item.Path, item.Label)));
            if (next == null)
            {
                Capture("options-final");
                Log("Captured title and available options tabs; automatic menu probe completed.");
                _finished = true;
                Application.Quit();
                return;
            }

            var key = TabKey(next.Path, next.Label);
            _capturedTabKeys.Add(key);
            Log("Opening safe navigation tab label='" + next.Label + "' path='" + next.Path + "'.");
            next.Button.onClickButton.Invoke();
            _pendingPage = "options-tab:" + NormalizeLabel(next.Label);
            _settlePending = true;
            _nextCapture = Time.realtimeSinceStartup + SettleSeconds;
        }
        catch (Exception error) { Fail("Probe failed: " + error); }
    }

    private static string GetButtonLabel(MyButton button)
    {
        var texts = button.GetComponentsInChildren<TMP_Text>(true);
        return texts.FirstOrDefault(text => text != null && !string.IsNullOrWhiteSpace(text.text))?.text?.Trim() ?? "";
    }

    private static string NormalizeLabel(string label) => System.Text.RegularExpressions.Regex.Replace(label, "<[^>]*>", "").Trim(' ', '>').Replace("\r", "").Replace("\n", " ");
    private static string TabKey(string path, string label) => path + "\n" + NormalizeLabel(label);

    private void Capture(string page)
    {
        var texts = Resources.FindObjectsOfTypeAll<TMP_Text>()
            .Where(text => text != null && text.gameObject.scene.IsValid())
            .OrderBy(text => text.gameObject.scene.name, StringComparer.Ordinal)
            .ThenBy(text => HierarchyPath(text.transform), StringComparer.Ordinal)
            .ToArray();

        using var writer = new StreamWriter(MenuModuleProbePlugin.OutputPath, append: true, new UTF8Encoding(false));
        foreach (var text in texts)
        {
            var scene = text.gameObject.scene.name;
            var path = HierarchyPath(text.transform);
            var active = text.gameObject.activeInHierarchy;
            var alpha = EffectiveAlpha(text).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var content = text.text ?? "";
            var dedupeKey = scene + "\n" + path + "\n" + active + "\n" + text.enabled + "\n" + alpha + "\n" + content;
            if (!_capturedRows.Add(dedupeKey)) continue;
            writer.Write("{\"page\":\""); writer.Write(Escape(page));
            writer.Write("\",\"scene\":\""); writer.Write(Escape(scene));
            writer.Write("\",\"path\":\""); writer.Write(Escape(path));
            writer.Write("\",\"active\":"); writer.Write(active ? "true" : "false");
            writer.Write(",\"enabled\":"); writer.Write(text.enabled ? "true" : "false");
            writer.Write(",\"alpha\":"); writer.Write(alpha);
            writer.Write(",\"identity\":\""); writer.Write(Escape(Identity(text.transform)));
            writer.Write("\",\"text\":\""); writer.Write(Escape(content));
            writer.Write("\",\"geometry\":");
            writer.Write(text.gameObject.activeInHierarchy && EffectiveAlpha(text) > .01f && !string.IsNullOrEmpty(text.text) ? NativeTextGeometry.Capture(text) : "null");
            writer.WriteLine("}");
        }
    }

    private static float EffectiveAlpha(TMP_Text text)
    {
        var alpha = text.color.a;
        for (var current = text.transform; current != null; current = current.parent)
        {
            var group = current.GetComponent<CanvasGroup>();
            if (group == null) continue;
            alpha *= group.alpha;
            if (group.ignoreParentGroups) break;
        }
        return text.enabled ? alpha : 0f;
    }

    private static string HierarchyPath(Transform leaf)
    {
        var parts = new List<string>();
        for (var current = leaf; current != null; current = current.parent) parts.Add(current.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string Identity(Transform leaf)
    {
        var parts = new List<string>();
        for (var current = leaf; current != null; current = current.parent)
            parts.Add(current.name + "[" + current.GetSiblingIndex() + "]");
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static string Escape(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    if (ch < 0x20) result.Append("\\u").Append(((int)ch).ToString("x4"));
                    else result.Append(ch);
                    break;
            }
        }
        return result.ToString();
    }

    private static void Log(string message) =>
        BepInEx.Logging.Logger.CreateLogSource("MenuModuleProbe").LogInfo(message);

    private void Fail(string message)
    {
        if (_finished) return;
        _finished = true;
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(MenuModuleProbePlugin.OutputPath)!, "menu-module-capture.failure.txt"), message); }
        catch { }
        BepInEx.Logging.Logger.CreateLogSource("MenuModuleProbe").LogError(message);
        Application.Quit(1);
    }
}
