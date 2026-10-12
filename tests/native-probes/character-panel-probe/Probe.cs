using System;
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
using UI.Menus.Character_Panel;

[BepInPlugin("community.tinyrogues.character-panel-probe", "Tiny Rogues Character Panel Probe", "0.1.0")]
public sealed class CharacterPanelProbePlugin : BasePlugin
{
    private const string Switch = "-tinyrogues-character-panel-probe";
    internal static string OutputPath = "";
    internal static string SaveRoot = "";

    public override void Load()
    {
        if (!Environment.GetCommandLineArgs().Contains(Switch, StringComparer.OrdinalIgnoreCase))
        {
            Log.LogInfo("Character panel probe idle; dedicated switch not present.");
            return;
        }

        var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        OutputPath = Path.Combine(pluginDirectory, "character-panel-capture.jsonl");
        SaveRoot = Path.Combine(pluginDirectory, "character-panel-probe-isolated-saves");
        try
        {
            Directory.CreateDirectory(SaveRoot);
            new Harmony("community.tinyrogues.character-panel-probe.save-isolation")
                .PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo("Save_System.SaveManager.SaveFilePath redirected to isolated directory: " + SaveRoot);
        }
        catch (Exception error)
        {
            Log.LogError("Save path isolation could not be installed; aborting the probe: " + error);
            Application.Quit(1);
            return;
        }
        AddComponent<CharacterPanelProbe>();
        Log.LogInfo("Character panel probe armed; automated title-to-run flow enabled.");
    }
}

[HarmonyPatch(typeof(Save_System.SaveManager), nameof(Save_System.SaveManager.SaveFilePath))]
internal static class SavePathIsolationPatch
{
    private static bool _logged;

    private static void Postfix(ref string __result)
    {
        if (string.IsNullOrWhiteSpace(CharacterPanelProbePlugin.SaveRoot))
            throw new InvalidOperationException("Save path isolation root is not configured.");

        var original = __result ?? "";
        var fileName = Path.GetFileName(original);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("SaveManager returned an invalid save file path.");

        using var sha = SHA256.Create();
        var digest = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(original))))
            .Replace("-", "").Substring(0, 16).ToLowerInvariant();
        var safeName = Path.GetFileNameWithoutExtension(fileName) + "_" + digest + Path.GetExtension(fileName);
        __result = Path.GetFullPath(Path.Combine(CharacterPanelProbePlugin.SaveRoot, safeName));

        var rootPrefix = Path.GetFullPath(CharacterPanelProbePlugin.SaveRoot) + Path.DirectorySeparatorChar;
        if (!__result.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Redirected save path escaped the isolated directory.");

        if (!_logged)
        {
            _logged = true;
            BepInEx.Logging.Logger.CreateLogSource("CharacterPanelProbe.SaveIsolation")
                .LogInfo("Save path redirected; original path is not used for IO.");
        }
    }
}

[HarmonyPatch]
internal static class BlockDiagnosticSaveWrites
{
    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods() =>
        typeof(Save_System.SaveManager).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "Save");
    private static bool Prefix() => false;
}

public sealed class CharacterPanelProbe : MonoBehaviour
{
    private const float TimeoutSeconds = 90f;
    private const float StatusLogInterval = 5f;
    private static readonly string[] PageNames = { "general", "offense", "defense", "misc" };
    private float _nextStatusLog;
    private CharacterPanelController? _panel;
    private UI.MyButton[]? _buttons;
    private float _deadline;
    private int _pageIndex;
    private int _waitFrames;
    private int _stage;
    private bool _slotSelected;
    private bool _callbackFallbackUsed;
    private Player.PlayerClass? _chosenClass;
    private bool _finished;

    public CharacterPanelProbe(IntPtr pointer) : base(pointer) { }

    private void Start()
    {
        _deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        _nextStatusLog = Time.realtimeSinceStartup;
        try { File.WriteAllText(CharacterPanelProbePlugin.OutputPath, ""); }
        catch (Exception error) { Fail("Unable to initialize capture file: " + error); }
    }

    private void Update()
    {
        if (_finished) return;
        if (Time.realtimeSinceStartup > _deadline) { Fail("Timed out waiting for all four pages."); return; }
        if (Time.realtimeSinceStartup >= _nextStatusLog)
        {
            LogProgress();
            _nextStatusLog = Time.realtimeSinceStartup + StatusLogInterval;
        }

        try
        {
            if (_stage == 0)
            {
                var title = UI.Title_Screen.TitleMenuController.Instance;
                if (title == null) return;
                title.PressPlay();
                _stage = 1;
                _stageTime = Time.realtimeSinceStartup;
                Log("Called TitleMenuController.PressPlay().");
                return;
            }

            if (_stage == 1)
            {
                var slotMenu = UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.Instance;
                if (!_slotSelected && slotMenu != null && UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.IsOpen)
                {
                    var slots = Resources.FindObjectsOfTypeAll<UI.Title_Screen.Save_Slot_Menu.SaveSlotPanel>()
                        .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid())
                        .OrderBy(candidate => candidate.index)
                        .ToArray();
                    if (slots.Length == 0 || slots[0].slotButton == null)
                    {
                        if (Time.realtimeSinceStartup - _stageTime > 15f) Fail("Save slot menu opened but no usable slot button appeared.");
                        return;
                    }
                    Log("Selecting first slot in the isolated, initially empty save directory.");
                    slots[0].slotButton.onClickButton.Invoke();
                    _slotSelected = true;
                    _stage = 2;
                    _stageTime = Time.realtimeSinceStartup;
                    return;
                }

                var classSelection = UI.Title_Screen.PlayerClassSelectionController.Instance;
                if (classSelection != null && classSelection.IsActive)
                {
                    _stage = 3;
                    _stageTime = Time.realtimeSinceStartup;
                    Log("Class selection is active after PressPlay.");
                    return;
                }

                if (Time.realtimeSinceStartup - _stageTime > 2f)
                {
                    var title = UI.Title_Screen.TitleMenuController.Instance;
                    if (title != null)
                    {
                        title.GoToClassSelection();
                        _stage = 2;
                        _stageTime = Time.realtimeSinceStartup;
                        Log("Called TitleMenuController.GoToClassSelection().");
                    }
                    else if (Time.realtimeSinceStartup - _stageTime > 15f)
                        Fail("Title menu disappeared without opening save slots or class selection.");
                }
                return;
            }

            if (_stage == 2)
            {
                var slotMenu = UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.Instance;
                if (!_slotSelected && slotMenu != null && UI.Title_Screen.Save_Slot_Menu.SaveSlotMenu.IsOpen)
                {
                    var slots = Resources.FindObjectsOfTypeAll<UI.Title_Screen.Save_Slot_Menu.SaveSlotPanel>()
                        .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid())
                        .OrderBy(candidate => candidate.index)
                        .ToArray();
                    if (slots.Length > 0 && slots[0].slotButton != null)
                    {
                        Log("Selecting first slot in the isolated, initially empty save directory.");
                        slots[0].slotButton.onClickButton.Invoke();
                        _slotSelected = true;
                        _stage = 3;
                        _stageTime = Time.realtimeSinceStartup;
                    }
                    else if (Time.realtimeSinceStartup - _stageTime > 15f)
                        Fail("Save slot menu opened but no usable slot button appeared.");
                    return;
                }

                var selection = UI.Title_Screen.PlayerClassSelectionController.Instance;
                if (selection != null && selection.IsActive)
                {
                    _stage = 3;
                    _stageTime = Time.realtimeSinceStartup;
                    Log("Class selection is active.");
                    return;
                }
                if (Time.realtimeSinceStartup - _stageTime > 15f)
                    Fail("Class selection did not become active after requesting the title transition.");
                return;
            }

            if (_stage == 3)
            {
                var classSelection = UI.Title_Screen.PlayerClassSelectionController.Instance;
                if (classSelection == null || !classSelection.IsActive)
                {
                    if (Time.realtimeSinceStartup - _stageTime > 30f) Fail("Class selection became unavailable before choosing a class.");
                    return;
                }

                var classButtons = Resources.FindObjectsOfTypeAll<UI.Title_Screen.PlayerClassButton>()
                    .Where(button => button != null && button.gameObject.scene.IsValid() && button.assignedClass != null &&
                                     !button.disabled && button.assignedClass.IsUnlocked)
                    .OrderBy(button => button.buttonIndex)
                    .ToArray();
                if (classButtons.Length == 0)
                {
                    if (Time.realtimeSinceStartup - _stageTime > 15f) Fail("No unlocked player class button appeared.");
                    return;
                }

                var chosen = classButtons[0];
                _chosenClass = chosen.assignedClass;
                Log("Selecting unlocked class " + chosen.assignedClass.name + " through PlayerClassButton.Select().");
                chosen.Select();
                _stage = 4;
                _stageTime = Time.realtimeSinceStartup;
                return;
            }

            if (_stage == 4)
            {
                var playButton = UI.Title_Screen.PlayButton.Instance;
                var player = Player.Player.Instance;
                if (player != null && player.PlayerActor != null)
                {
                    Log("PlayerActor appeared while the class selection callback transitioned scenes.");
                    _stage = 5;
                    _stageTime = Time.realtimeSinceStartup;
                    return;
                }
                if (playButton != null && playButton.classWasSelected)
                {
                    Log("Class selected; invoking the play button's native OnClick entry.");
                    playButton.OnClick();
                    _stage = 5;
                    _stageTime = Time.realtimeSinceStartup;
                }
                else if (playButton != null && !_callbackFallbackUsed && Time.realtimeSinceStartup - _stageTime > 2f && _chosenClass != null)
                {
                    Log("Calling confirmed PlayButton.OnPlayerSelectsClass(PlayerClass) callback directly as a fallback.");
                    playButton.OnPlayerSelectsClass(_chosenClass);
                    _callbackFallbackUsed = true;
                }
                else if (Time.realtimeSinceStartup - _stageTime > 15f)
                    Fail("Selecting a class did not reach PlayButton.OnPlayerSelectsClass.");
                return;
            }

            if (_stage == 5)
            {
                var player = Player.Player.Instance;
                if (player == null || player.PlayerActor == null) return;
                var panel = Resources.FindObjectsOfTypeAll<CharacterPanelController>()
                    .FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid());
                if (panel == null) return;

                _panel = panel;
                _buttons = new[] { _panel.generalTabButton, _panel.offenseTabButton, _panel.defenseTabButton, _panel.miscTabButton };
                if (_buttons.Any(button => button == null)) { Fail("One or more expected tab buttons are missing."); return; }

                Log("Valid PlayerActor and CharacterPanelController found; opening panel.");
                CharacterPanelController.ForceOpen();
                _buttons[0].onClickButton.Invoke();
                _stage = 6;
                _waitFrames = 1;
                return;
            }

            if (_stage != 6) return;
            if (_waitFrames > 0) { _waitFrames--; return; }
            if (_panel == null || _buttons == null) { Fail("Character panel disappeared during capture."); return; }

            CapturePage(PageNames[_pageIndex]);
            Log("Captured " + PageNames[_pageIndex] + " page.");
            _pageIndex++;
            if (_pageIndex >= PageNames.Length)
            {
                _finished = true;
                BepInEx.Logging.Logger.CreateLogSource("CharacterPanelProbe").LogInfo("Captured all four character panel pages.");
                Application.Quit();
                return;
            }

            _buttons[_pageIndex].onClickButton.Invoke();
            _waitFrames = 1;
        }
        catch (Exception error) { Fail("Probe failed: " + error); }
    }

    private float _stageTime;

    private void LogProgress()
    {
        var stageName = _stage switch
        {
            0 => "waiting for title menu",
            1 => "press-play transition/save slot",
            2 => "waiting for class selection",
            3 => "choosing class",
            4 => "waiting for class callback",
            5 => "waiting for gameplay PlayerActor and panel",
            6 => "capturing pages " + (_pageIndex + 1) + "/4",
            _ => "unknown stage"
        };
        Log("Progress: " + stageName + "; elapsed=" + (Time.realtimeSinceStartup - (_deadline - TimeoutSeconds)).ToString("0") + "s.");
    }

    private static void Log(string message) =>
        BepInEx.Logging.Logger.CreateLogSource("CharacterPanelProbe").LogInfo(message);

    private void CapturePage(string page)
    {
        if (_panel == null) throw new InvalidOperationException("Panel is unavailable.");
        var texts = Resources.FindObjectsOfTypeAll<TMP_Text>()
            .Where(text => text != null && text.gameObject.scene.IsValid() && text.transform.IsChildOf(_panel.transform))
            .OrderBy(text => HierarchyPath(text.transform), StringComparer.Ordinal)
            .ToArray();

        using var writer = new StreamWriter(CharacterPanelProbePlugin.OutputPath, append: true, new UTF8Encoding(false));
        foreach (var text in texts)
        {
            writer.Write("{\"page\":\""); writer.Write(Escape(page));
            writer.Write("\",\"path\":\""); writer.Write(Escape(HierarchyPath(text.transform)));
            writer.Write("\",\"active\":"); writer.Write(text.gameObject.activeInHierarchy ? "true" : "false");
            writer.Write(",\"alpha\":"); writer.Write(EffectiveAlpha(text).ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(",\"identity\":\""); writer.Write(Escape(Identity(text.transform))); writer.Write("\"");
            writer.Write(",\"text\":\""); writer.Write(Escape(text.text ?? ""));
            writer.WriteLine("\"}");
        }
        writer.Flush();
    }

    private string HierarchyPath(Transform leaf)
    {
        if (_panel == null) return leaf.name;
        var parts = new System.Collections.Generic.List<string>();
        var current = leaf;
        while (current != null)
        {
            parts.Add(current.name);
            if (current == _panel.transform) break;
            current = current.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
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
    private string Identity(Transform leaf)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var current = leaf; current != null; current = current.parent)
        {
            parts.Add(current.name + "[" + current.GetSiblingIndex() + "]");
            if (_panel != null && current == _panel.transform) break;
        }
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

    private void Fail(string message)
    {
        if (_finished) return;
        _finished = true;
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(CharacterPanelProbePlugin.OutputPath)!, "character-panel-capture.failure.txt"), message); }
        catch { }
        BepInEx.Logging.Logger.CreateLogSource("CharacterPanelProbe").LogError(message);
        Application.Quit(1);
    }
}
